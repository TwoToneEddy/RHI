using System.Text.RegularExpressions;

namespace RHI.Linux.Core;

public enum NativeBinaryKind { Missing, ElfX64, ElfOtherArchitecture, ElfLibrary, WindowsExecutable, Script, Malformed, Unknown }

// Inspect classifies the header; RequireGameExecutable also validates program headers.
// The target is never executed. The format identifies a binary,
// not the launch target Steam actually runs or its rendering API, so callers still need the
// user's explicit selection.
public static class NativeBinary
{
    private const ushort X86_64 = 0x3E;
    private const ushort ExecutableType = 2;
    private const ushort DynamicType = 3;
    private const int Elf64HeaderSize = 64;
    private const int Elf64ProgramHeaderSize = 56;
    private const uint LoadSegment = 1;
    private const uint ExecutableSegmentFlag = 1;
    private const int VersionOffset = 20;
    private const int EntryPointOffset = 24;
    private const int ProgramTableOffset = 32;
    private const int HeaderSizeOffset = 52;
    private const int ProgramEntrySizeOffset = 54;
    private const int ProgramEntryCountOffset = 56;

    public static NativeBinaryKind Inspect(string path)
    {
        if (!File.Exists(path)) return NativeBinaryKind.Missing;
        var buffer = new byte[Elf64HeaderSize];
        int read;
        using (var stream = File.OpenRead(path)) read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return Inspect(buffer.AsSpan(0, read));
    }

    public static NativeBinaryKind Inspect(ReadOnlySpan<byte> header)
    {
        var read = Math.Min(header.Length, Elf64HeaderSize);
        if (read >= 2 && header[0] == '#' && header[1] == '!') return NativeBinaryKind.Script;
        if (read >= 2 && header[0] == 'M' && header[1] == 'Z') return NativeBinaryKind.WindowsExecutable;
        if (read < 4 || header[0] != 0x7F || header[1] != 'E' || header[2] != 'L' || header[3] != 'F') return NativeBinaryKind.Unknown;
        // e_ident: class 32/64-bit, data encoding, version 1.
        if (read < 20 || header[4] is not 1 and not 2 || header[5] is not 1 and not 2 || header[6] != 1) return NativeBinaryKind.Malformed;
        if (header[5] != 1) return NativeBinaryKind.ElfOtherArchitecture; // big-endian
        var type = BitConverter.ToUInt16(header[16..]);
        var machine = BitConverter.ToUInt16(header[18..]);
        // ET_EXEC or ET_DYN (position-independent executables are ET_DYN).
        if (type is not ExecutableType and not DynamicType) return NativeBinaryKind.Malformed;
        if (header[4] != 2 || machine != X86_64) return NativeBinaryKind.ElfOtherArchitecture;
        return read < Elf64HeaderSize ? NativeBinaryKind.Malformed : NativeBinaryKind.ElfX64;
    }

    public static bool IsSharedLibraryName(string path) => Regex.IsMatch(Path.GetFileName(path), @"\.so(\.\d+)*$");

    // Validates a user-selected native game executable inside the game's folder.
    public static void RequireGameExecutable(string path, string? gameRoot = null)
    {
        if (gameRoot != null && !LinuxPaths.IsWithin(gameRoot, path))
            throw new IOException("Choose an executable inside this game's folder.");
        var kind = Inspect(path);
        if (kind == NativeBinaryKind.ElfX64 && IsSharedLibraryName(path)) kind = NativeBinaryKind.ElfLibrary;
        switch (kind)
        {
            case NativeBinaryKind.ElfX64:
                ValidateExecutable(path);
                if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(path) &
                    (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                    throw new IOException("The selected Linux binary has no execute permission. Make it executable before selecting it.");
                return;
            case NativeBinaryKind.Missing: throw new FileNotFoundException("The selected executable does not exist.", path);
            case NativeBinaryKind.Script: throw new IOException("This is a launch script. RHI does not run or parse launch scripts; choose the native game binary it starts.");
            case NativeBinaryKind.WindowsExecutable: throw new IOException("This is a Windows executable. Use the default Windows ReShade backend for games running through Proton.");
            case NativeBinaryKind.ElfOtherArchitecture: throw new IOException("Native Vulkan ReShade supports x86-64 Linux executables only.");
            case NativeBinaryKind.ElfLibrary: throw new IOException("This is a shared library. Choose the game's executable.");
            case NativeBinaryKind.Malformed: throw new IOException("The selected file is not a valid Linux executable.");
            default: throw new IOException("The selected file is not a Linux (ELF) executable.");
        }
    }

    // Inspect is format classification only. A game target additionally needs a bounded ELF
    // program table and an entry point in an executable load segment (including PIE/static PIE).
    private static void ValidateExecutable(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        var header = reader.ReadBytes(Elf64HeaderSize);
        ulong U64(int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(offset));
        ushort U16(int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(offset));
        var entry = U64(EntryPointOffset);
        var table = U64(ProgramTableOffset);
        var count = U16(ProgramEntryCountOffset);
        var size = U16(ProgramEntrySizeOffset);
        var length = (ulong)stream.Length;
        bool Fits(ulong offset, ulong bytes) => offset <= length && bytes <= length - offset;
        void Invalid() => throw new IOException("The selected file is not a valid Linux executable.");
        if (U16(HeaderSizeOffset) != Elf64HeaderSize || BitConverter.ToUInt32(header, VersionOffset) != 1 || size != Elf64ProgramHeaderSize || count == 0 ||
            count == ushort.MaxValue || table < Elf64HeaderSize || !Fits(table, (ulong)count * size)) Invalid();
        bool executableEntry = false;
        for (var i = 0; i < count; i++)
        {
            stream.Position = (long)(table + (ulong)i * size);
            var type = reader.ReadUInt32();
            var flags = reader.ReadUInt32();
            var offset = reader.ReadUInt64();
            var address = reader.ReadUInt64();
            _ = reader.ReadUInt64(); // Physical address is not used for process loading.
            var fileSize = reader.ReadUInt64();
            var memorySize = reader.ReadUInt64();
            if (!Fits(offset, fileSize) || (type == LoadSegment && fileSize > memorySize)) Invalid();
            if (type == LoadSegment && (flags & ExecutableSegmentFlag) != 0 && entry >= address && entry - address < fileSize)
                executableEntry = true;
        }
        if (entry == 0 || !executableEntry)
            throw new IOException("This ELF file has no executable entry point; it may be a shared library. Choose the game's executable.");
    }
}
