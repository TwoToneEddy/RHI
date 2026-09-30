using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using RHI.Linux.Core;
using Xunit;

namespace RHI.Linux.Tests;

public sealed class NativeReShadeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-native-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    private string Data => Path.Combine(_root, "home/.local/share");
    private string Layers => Path.Combine(Data, "vulkan/implicit_layer.d");
    private string SystemLayers => Path.Combine(_root, "usr/share/vulkan/implicit_layer.d");

    public NativeReShadeTests()
    {
        Directory.CreateDirectory(Data);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "rhi-data"));
    }
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _oldData);
        Directory.Delete(_root, true);
    }

    private NativeReShade Runtime() => new(Data, Path.Combine(_root, "rhi-data/rhi-linux/native-reshade"), [Layers, SystemLayers]);

    internal static byte[] Elf(ushort machine = 0x3E, byte bits = 2, byte endian = 1, ushort type = 3, int length = 256)
    {
        var bytes = new byte[Math.Max(length, 20)];
        bytes[0] = 0x7F; bytes[1] = (byte)'E'; bytes[2] = (byte)'L'; bytes[3] = (byte)'F';
        bytes[4] = bits; bytes[5] = endian; bytes[6] = 1;
        BitConverter.GetBytes(type).CopyTo(bytes, 16);
        BitConverter.GetBytes(machine).CopyTo(bytes, 18);
        if (length >= 120)
        {
            BitConverter.GetBytes(1u).CopyTo(bytes, 20);
            BitConverter.GetBytes(0x400080ul).CopyTo(bytes, 24);
            BitConverter.GetBytes(64ul).CopyTo(bytes, 32);
            BitConverter.GetBytes((ushort)64).CopyTo(bytes, 52);
            BitConverter.GetBytes((ushort)56).CopyTo(bytes, 54);
            BitConverter.GetBytes((ushort)1).CopyTo(bytes, 56);
            BitConverter.GetBytes(1u).CopyTo(bytes, 64);
            BitConverter.GetBytes(5u).CopyTo(bytes, 68);
            BitConverter.GetBytes(0x400000ul).CopyTo(bytes, 80);
            BitConverter.GetBytes((ulong)length).CopyTo(bytes, 96);
            BitConverter.GetBytes((ulong)length).CopyTo(bytes, 104);
        }
        return bytes[..length];
    }

    private string FileAt(string relative, byte[] content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static string Manifest(string libraryPath = "../../../lib/reshade/ReShade64.so", bool optIn = true) => JsonSerializer.Serialize(new
    {
        file_format_version = "1.2.0",
        layer = optIn
            ? (object)new { name = "VK_LAYER_reshade", type = "GLOBAL", library_path = libraryPath, api_version = "1.4.0", implementation_version = "1", description = "ReShade post-processing injector",
                enable_environment = new Dictionary<string, string> { ["RESHADE_ENABLE"] = "1" }, disable_environment = new Dictionary<string, string> { ["DISABLE_RESHADE"] = "1" } }
            : new { name = "VK_LAYER_reshade", type = "GLOBAL", library_path = libraryPath, api_version = "1.4.0", implementation_version = "1", description = "ReShade post-processing injector" }
    });

    // Same layout as the upstream release archive.
    private static MemoryStream Package(Action<TarWriter>? extra = null, byte[]? library = null, string? manifest = null, bool withManifest = true)
    {
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, leaveOpen: true))
        {
            void Add(string name, byte[] content)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, NativeReShade.ArchiveRoot + "/" + name) { DataStream = new MemoryStream(content) };
                writer.WriteEntry(entry);
            }
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, NativeReShade.ArchiveRoot + "/"));
            Add("lib/reshade/ReShade64.so", library ?? Elf());
            if (withManifest) Add("share/vulkan/implicit_layer.d/ReShade64.json", Encoding.UTF8.GetBytes(manifest ?? Manifest()));
            Add("share/reshade/reshade-shaders/Shaders/ReShade.fxh", Encoding.UTF8.GetBytes("upstream fxh"));
            Add("share/reshade/reshade-shaders/Shaders/Deband.fx", Encoding.UTF8.GetBytes("upstream deband"));
            Add("share/reshade/reshade-shaders/Textures/lut.png", [1, 2, 3]);
            Add("optional-addons/api_trace.addon64", Elf());
            Add("install.sh", Encoding.UTF8.GetBytes("#!/bin/sh\nrm -rf /"));
            Add("uninstall.sh", Encoding.UTF8.GetBytes("#!/bin/sh"));
            Add("LICENSE.md", Encoding.UTF8.GetBytes("BSD"));
            extra?.Invoke(writer);
        }
        stream.Position = 0;
        return stream;
    }

    [Theory]
    [InlineData(0x3E, 2, 1, 2, 64, NativeBinaryKind.ElfX64)]
    [InlineData(0x3E, 2, 1, 3, 64, NativeBinaryKind.ElfX64)]
    [InlineData(0x03, 1, 1, 2, 52, NativeBinaryKind.ElfOtherArchitecture)]
    [InlineData(0xB7, 2, 1, 3, 64, NativeBinaryKind.ElfOtherArchitecture)]
    [InlineData(0x3E, 2, 2, 2, 64, NativeBinaryKind.ElfOtherArchitecture)]
    [InlineData(0x3E, 2, 1, 1, 64, NativeBinaryKind.Malformed)]
    [InlineData(0x3E, 3, 1, 2, 64, NativeBinaryKind.Malformed)]
    [InlineData(0x3E, 2, 1, 2, 40, NativeBinaryKind.Malformed)]
    [InlineData(0x3E, 2, 1, 2, 12, NativeBinaryKind.Malformed)]
    public void ElfHeadersAreClassifiedWithoutExecuting(ushort machine, byte bits, byte endian, ushort type, int length, NativeBinaryKind expected) =>
        Assert.Equal(expected, NativeBinary.Inspect(FileAt("bin/game", Elf(machine, bits, endian, type, length))));

    [Fact] public void NativeExecutableValidationRejectsWrongTargets()
    {
        var game = Path.Combine(_root, "Game");
        NativeBinary.RequireGameExecutable(FileAt("Game/bin/game.x86_64", Elf()), game);
        Assert.Throws<FileNotFoundException>(() => NativeBinary.RequireGameExecutable(Path.Combine(game, "missing"), game));
        Assert.Contains("launch script", Assert.Throws<IOException>(() => NativeBinary.RequireGameExecutable(FileAt("Game/start.sh", Encoding.UTF8.GetBytes("#!/bin/sh\nexec ./game")), game)).Message);
        Assert.Contains("Windows", Assert.Throws<IOException>(() => NativeBinary.RequireGameExecutable(FileAt("Game/Game.exe", Encoding.ASCII.GetBytes("MZ....")), game)).Message);
        Assert.Contains("x86-64", Assert.Throws<IOException>(() => NativeBinary.RequireGameExecutable(FileAt("Game/game.x86", Elf(0x03, 1, 1, 2, 52)), game)).Message);
        Assert.Contains("shared library", Assert.Throws<IOException>(() => NativeBinary.RequireGameExecutable(FileAt("Game/libgame.so.1", Elf()), game)).Message);
        Assert.Contains("not a valid", Assert.Throws<IOException>(() => NativeBinary.RequireGameExecutable(FileAt("Game/broken", Elf(length: 30)), game)).Message);
        Assert.Throws<IOException>(() => NativeBinary.RequireGameExecutable(FileAt("Game/data.pak", [1, 2, 3, 4]), game));
        Assert.Contains("inside this game", Assert.Throws<IOException>(() => NativeBinary.RequireGameExecutable(FileAt("Other/game", Elf()), game)).Message);
    }

    [Fact] public void GameValidationRejectsTruncatedTablesLibrariesAndNonExecutableFiles()
    {
        var bytes = Elf();
        BitConverter.GetBytes(ulong.MaxValue).CopyTo(bytes, 32);
        Assert.Throws<IOException>(() => NativeBinary.RequireGameExecutable(FileAt("bad-table", bytes)));
        Assert.Throws<IOException>(() => NativeBinary.RequireGameExecutable(FileAt("header-only", Elf(length: 64))));
        bytes = Elf();
        Array.Clear(bytes, 24, 8);
        Assert.Contains("shared library", Assert.Throws<IOException>(() => NativeBinary.RequireGameExecutable(FileAt("renamed-library", bytes))).Message);
        var path = FileAt("not-executable", Elf());
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (OperatingSystem.IsLinux()) Assert.Contains("permission", Assert.Throws<IOException>(() => NativeBinary.RequireGameExecutable(path)).Message);
        NativeBinary.RequireGameExecutable(FileAt("static-executable", Elf(type: 2)));
        NativeBinary.RequireGameExecutable(FileAt("pie-executable", Elf(type: 3)));
    }

    [Theory]
    [InlineData("", "%command%")]
    [InlineData("%command%", "%command%")]
    [InlineData("-vulkan", "%command% -vulkan")]
    [InlineData("MANGOHUD=1 gamemoderun %command% --name \"My Game\"", "MANGOHUD=1 gamemoderun %command% --name \"My Game\"")]
    [InlineData("PROTON_ENABLE_HDR=1 WINEDLLOVERRIDES='dinput8=n,b' gamescope -W 3840 -- %command%", "PROTON_ENABLE_HDR=1 WINEDLLOVERRIDES='dinput8=n,b' gamescope -W 3840 -- %command%")]
    public void LaunchActivationIsReversibleAndPreservesOptions(string original, string normalized)
    {
        var enabled = NativeReShadeLaunchOptions.Enable(original);
        Assert.Equal("RESHADE_ENABLE=1 " + normalized, enabled);
        Assert.True(NativeReShadeLaunchOptions.IsEnabled(enabled));
        Assert.Equal(enabled, NativeReShadeLaunchOptions.Enable(enabled));
        Assert.Equal(normalized, NativeReShadeLaunchOptions.Disable(enabled));
        Assert.Equal(original, NativeReShadeLaunchOptions.Disable(original));
        Assert.DoesNotContain("PROTON_ENABLE_WAYLAND", enabled);
    }

    [Theory]
    [InlineData("RESHADE_ENABLE='1' MANGOHUD=1 %command%", "MANGOHUD=1 %command%")]
    [InlineData("MANGOHUD=1 RESHADE_ENABLE=\"1\" %command% -x", "MANGOHUD=1 %command% -x")]
    public void DisablingRemovesOnlyTheOwnedAssignment(string options, string expected)
    {
        Assert.True(NativeReShadeLaunchOptions.IsEnabled(options));
        Assert.Equal(expected, NativeReShadeLaunchOptions.Disable(options));
    }

    [Theory]
    [InlineData("RESHADE_ENABLE=0 %command%")]
    [InlineData("RESHADE_ENABLE=1 RESHADE_ENABLE=1 %command%")]
    [InlineData("%command% RESHADE_ENABLE=1")]
    [InlineData("gamescope RESHADE_ENABLE=1 -- %command%")]
    [InlineData("FOO='RESHADE_ENABLE=1' %command%")]
    [InlineData("DISABLE_RESHADE=1 %command%")]
    [InlineData("env -i %command%")]
    [InlineData("RESHADE_ENABLE=$X %command%")]
    [InlineData("%command%; echo done")]
    [InlineData("%command% %command%")]
    [InlineData("\"%command%\"")]
    [InlineData("/path/to/launcher")]
    [InlineData("%command%\n-x")]
    public void AmbiguousOrConflictingOptionsAreRefused(string options)
    {
        Assert.Throws<FormatException>(() => NativeReShadeLaunchOptions.Enable(options));
        Assert.Throws<FormatException>(() => NativeReShadeLaunchOptions.Disable(options));
        Assert.False(NativeReShadeLaunchOptions.IsEnabled(options));
    }

    [Fact] public void OtherLaunchFeaturesKeepActivationAndItsRemovalKeepsThem()
    {
        var options = NativeReShadeLaunchOptions.Enable("%command% -vulkan");
        options = HdrLaunchOptions.Merge(options, false, true);
        options = ReEngineLaunchOptions.SetEnabled(options, true);
        Assert.True(NativeReShadeLaunchOptions.IsEnabled(options));
        var disabled = NativeReShadeLaunchOptions.Disable(options);
        Assert.True(HdrLaunchOptions.IsEnabled(disabled));
        Assert.True(ReEngineLaunchOptions.IsEnabled(disabled));
        Assert.DoesNotContain("RESHADE_ENABLE", disabled);
    }

    [Fact] public void WindowsBackendCannotBeConfiguredOnAnActivatedLaunch()
    {
        Assert.Throws<IOException>(() => NativeReShade.RequireNotActivated("RESHADE_ENABLE=1 %command%"));
        NativeReShade.RequireNotActivated("WINEDLLOVERRIDES='dxgi=n,b;d3dcompiler_47=n,b' %command%");
        var preferences = new GamePreferences { Backend = NativeReShade.Backend };
        Assert.Contains("Windows ReShade", Assert.Throws<IOException>(() => NativeReShade.RequireWindowsBackend(preferences)).Message);
        NativeReShade.RequireWindowsBackend(new GamePreferences());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    public async Task WindowsInstallRejectsManualNativeActivationInAnySteamAccount(string activatedAccount)
    {
        var exe = FileAt("Game/Game.exe", Encoding.ASCII.GetBytes("MZ"));
        var game = new Game
        {
            Root = Path.GetDirectoryName(exe)!, Executable = exe,
            SteamRoot = Path.Combine(_root, "Steam"), AppId = "123"
        };
        var configs = new Dictionary<string, string>();
        foreach (var account in new[] { "1", "2" })
        {
            var options = account == activatedAccount ? "RESHADE_ENABLE=1 %command%" : "%command%";
            var content = Proton.EditOptions(""" "UserLocalConfigStore" { "Software" { "Valve" { "Steam" { "apps" { } } } } } """, game.AppId, options);
            var path = FileAt($"Steam/userdata/{account}/config/localconfig.vdf", Encoding.UTF8.GetBytes(content));
            configs.Add(path, content);
        }
        // A disposed client ensures the rejection happens before any download is attempted.
        using var http = new HttpClient();
        var setup = new GameSetup(new Downloads(http), new Catalog(http));
        http.Dispose();

        var error = await Assert.ThrowsAsync<IOException>(() =>
            setup.InstallReShade(game, new GamePreferences { SteamConfig = configs.Keys.First() }, null));

        Assert.Contains("RESHADE_ENABLE", error.Message);
        Assert.Equal(new[] { exe }, Directory.GetFiles(game.Root));
        Assert.False(Directory.Exists(Path.Combine(game.Root, ".rhi-linux")));
        foreach (var (path, content) in configs) Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact] public void ExistingWindowsReShadeBlocksNativeActivationWithoutBeingRemoved()
    {
        var root = Path.Combine(_root, "Game"); Directory.CreateDirectory(root);
        var exe = FileAt("Game/Game.exe", Encoding.ASCII.GetBytes("MZ"));
        var game = new Game { Name = "Mixed", Root = root, Executable = exe };
        NativeReShade.RequireNoWindowsReShade(game, "%command%");
        Assert.Throws<IOException>(() => NativeReShade.RequireNoWindowsReShade(game, "WINEDLLOVERRIDES='dxgi=n,b;d3dcompiler_47=n,b' %command%"));
        new Installation(root).Install("ReShade", "Nightly", [new("dxgi.dll", [1])], proxy: "dxgi.dll");
        Assert.Throws<IOException>(() => NativeReShade.RequireNoWindowsReShade(game, "%command%"));
        Assert.True(File.Exists(Path.Combine(root, "dxgi.dll")));
    }

    [Theory]
    [InlineData("dxgi=n,b")]
    [InlineData("d3d9=n")]
    [InlineData("opengl32=b,n")]
    [InlineData("DXGI.dll,d3d11=n,b")]
    public void ProxyOverrideAloneBlocksNativeActivation(string value)
    {
        var game = new Game { Root = _root };
        Assert.Throws<IOException>(() => NativeReShade.RequireNoWindowsReShade(game,
            $"WINEDLLOVERRIDES=\"{value}\" %command%"));
        NativeReShade.RequireNoWindowsReShade(game, "WINEDLLOVERRIDES='dxgi=b;d3d9=;d3dcompiler_47=n,b' %command%");
    }

    [Fact] public void OldSettingsKeepTheWindowsBackend()
    {
        var settings = JsonSerializer.Deserialize<Settings>("""{"Games":{"steam:1:/g":{"Channel":"Stable","Favourite":true}}}""", LinuxPaths.Json)!;
        var preferences = settings.Games["steam:1:/g"];
        Assert.Null(preferences.Backend); Assert.False(NativeReShade.Selected(preferences)); Assert.Equal("Stable", preferences.Channel);
        Assert.Empty(NativeReShade.Users(settings));
        Assert.Null(new GamePreferences().Backend);
    }

    [Fact] public void SnapshotComparisonUsesValuesAndDetectsNewLogEvidence()
    {
        var executable = FileAt("Native/game", Elf());
        var game = new Game { Root = Path.GetDirectoryName(executable)! };
        var preferences = new GamePreferences
        {
            Backend = NativeReShade.Backend, NativeExecutable = executable, NativeVulkanConfirmed = true
        };
        var runtime = Runtime();
        var first = NativeGameSnapshot.Read(runtime, game, preferences);
        Assert.True(first.HasSameValues(NativeGameSnapshot.Read(runtime, game, preferences)));
        Directory.CreateDirectory(runtime.LogRoot);
        File.WriteAllText(Path.Combine(runtime.LogRoot, "game.log"),
            $"Initializing ReShade version '6.8.0' loaded from '/lib/ReShade64.so' into '{executable}'");
        var loaded = NativeGameSnapshot.Read(runtime, game, preferences);
        Assert.NotNull(loaded.Evidence);
        Assert.False(first.HasSameValues(loaded));
        Assert.True(loaded.HasSameValues(NativeGameSnapshot.Read(runtime, game, preferences)));
    }

    [Fact] public void SnapshotReportsRuntimeReadErrorsWithoutLosingTargetDetails()
    {
        var runtime = Runtime();
        Directory.CreateDirectory(runtime.Metadata);
        File.WriteAllText(Path.Combine(runtime.Metadata, "manifest.json"), "invalid json");
        var executable = FileAt("Native/game", Elf());
        var game = new Game { Root = Path.GetDirectoryName(executable)! };
        var preferences = new GamePreferences { Backend = NativeReShade.Backend, NativeExecutable = executable, NativeVulkanConfirmed = true };
        var snapshot = NativeGameSnapshot.Read(runtime, game, preferences);
        Assert.NotNull(snapshot.RuntimeError);
        Assert.Equal(executable, snapshot.Executable);
        Assert.Null(snapshot.TargetError);
        Assert.Null(snapshot.UnsupportedReason);
    }

    [Fact] public void TargetNeedsSelectionExecutableAndVulkanConfirmation()
    {
        var root = Path.Combine(_root, "Native"); Directory.CreateDirectory(root);
        var game = new Game { Name = "Native", Root = root };
        var preferences = new GamePreferences();
        Assert.Contains("not selected", NativeReShade.Unsupported(game, preferences));
        preferences.Backend = NativeReShade.Backend;
        Assert.Contains("native Linux executable", NativeReShade.Unsupported(game, preferences));
        preferences.NativeExecutable = FileAt("Native/run.sh", Encoding.UTF8.GetBytes("#!/bin/sh"));
        Assert.Contains("launch script", NativeReShade.Unsupported(game, preferences));
        preferences.NativeExecutable = FileAt("Native/game", Elf());
        Assert.Contains("Vulkan", NativeReShade.Unsupported(game, preferences));
        preferences.NativeVulkanConfirmed = true;
        Assert.Null(NativeReShade.Unsupported(game, preferences));
        preferences.NativeExecutable = FileAt("Elsewhere/game", Elf());
        Assert.NotNull(NativeReShade.Unsupported(game, preferences));
    }

    [Fact] public void SandboxedSteamIsNotOffered()
    {
        Assert.NotNull(NativeReShade.UnsupportedLauncher(new Game { SteamRoot = "/home/u/.var/app/com.valvesoftware.Steam/.local/share/Steam" }));
        Assert.NotNull(NativeReShade.UnsupportedLauncher(new Game { SteamRoot = "/home/u/snap/steam/common/.local/share/Steam" }));
        Assert.Null(NativeReShade.UnsupportedLauncher(new Game { SteamRoot = "/home/u/.local/share/Steam" }));
        Assert.Null(NativeReShade.UnsupportedLauncher(new Game { Source = "Manual" }));
    }

    [Fact] public void PackageReaderTakesOnlyTheRuntimeAndShaders()
    {
        var package = NativeReShade.ReadPackage(Package());
        Assert.Equal(Elf(), package.Library);
        Assert.Equal(["Shaders/ReShade.fxh", "Shaders/Deband.fx", "Textures/lut.png"], package.Shaders.Select(s => s.Path));
        Assert.Equal("BSD", Encoding.UTF8.GetString(package.License!));
    }

    [Fact] public void PackageReaderRejectsUnsafeOrUnexpectedPackages()
    {
        void Rejects(Stream tar) => Assert.Throws<IOException>(() => NativeReShade.ReadPackage(tar));
        Rejects(Package(w => w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, NativeReShade.ArchiveRoot + "/../escape") { DataStream = new MemoryStream([1]) })));
        Rejects(Package(w => w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "other-root/file") { DataStream = new MemoryStream([1]) })));
        Rejects(Package(w => w.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, NativeReShade.ArchiveRoot + "/share/reshade/reshade-shaders/Shaders/link") { LinkName = "/etc/passwd" })));
        Rejects(Package(withManifest: false));
        Rejects(Package(library: Elf(0x03, 1, 1, 3, 52)));
        Rejects(Package(library: Encoding.ASCII.GetBytes("MZ not elf")));
        // A manifest without enable_environment would load the layer into every Vulkan application.
        Rejects(Package(manifest: Manifest(optIn: false)));
        Rejects(Package(manifest: Manifest("/usr/lib/other.so")));
        Rejects(Package(manifest: "{ not json"));
    }

    [Fact] public void InstallAndRemoveKeepUserFilesAndAreRepeatable()
    {
        var runtime = Runtime();
        var userShader = FileAt("home/.local/share/reshade/reshade-shaders/Shaders/Deband.fx", Encoding.UTF8.GetBytes("user edit"));
        var config = FileAt("home/.local/share/reshade/configurations/Game-1/ReShade.ini", Encoding.UTF8.GetBytes("[GENERAL]"));
        var unrelated = FileAt("home/.local/share/vulkan/implicit_layer.d/steamoverlay_x86_64.json", Encoding.UTF8.GetBytes("{\"layer\":{\"name\":\"VK_LAYER_VALVE_steam_overlay_64\"}}"));
        Assert.Equal(NativeRuntimeState.NotInstalled, runtime.Status().State);

        runtime.Install(NativeReShade.ReadPackage(Package()));
        Assert.Equal(NativeRuntimeState.Installed, runtime.Status().State);
        Assert.Equal(NativeReShade.Version, runtime.Status().Version);
        Assert.Equal(Path.Combine(_root, "home/.local/lib/reshade/ReShade64.so"), runtime.LibraryPath);
        Assert.Equal(Elf(), File.ReadAllBytes(runtime.LibraryPath));
        NativeReShade.ValidateManifest(File.ReadAllBytes(Path.Combine(Layers, "ReShade64.json")));
        Assert.Equal("upstream fxh", File.ReadAllText(Path.Combine(runtime.ShaderRoot, "Shaders/ReShade.fxh")));
        Assert.Equal("user edit", File.ReadAllText(userShader));
        Assert.False(Directory.Exists(Path.Combine(Data, "reshade/optional-addons")));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.addon64", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(_root, "install.sh", SearchOption.AllDirectories));

        // Reinstalling is idempotent; changed shaders stay as the user left them.
        File.WriteAllText(Path.Combine(runtime.ShaderRoot, "Shaders/ReShade.fxh"), "tuned");
        runtime.Install(NativeReShade.ReadPackage(Package()));
        Assert.Equal("tuned", File.ReadAllText(Path.Combine(runtime.ShaderRoot, "Shaders/ReShade.fxh")));
        Assert.Equal(NativeRuntimeState.Installed, runtime.Status().State);

        runtime.Remove(new Settings());
        Assert.False(File.Exists(runtime.LibraryPath)); Assert.False(File.Exists(runtime.ManifestPath));
        Assert.False(File.Exists(Path.Combine(runtime.ShaderRoot, "Textures/lut.png")));
        Assert.Equal("tuned", File.ReadAllText(Path.Combine(runtime.ShaderRoot, "Shaders/ReShade.fxh")));
        Assert.Equal("user edit", File.ReadAllText(userShader));
        Assert.True(File.Exists(config)); Assert.True(File.Exists(unrelated));
        Assert.Equal(NativeRuntimeState.NotInstalled, runtime.Status().State);

        runtime.Install(NativeReShade.ReadPackage(Package()));
        Assert.Equal(NativeRuntimeState.Installed, runtime.Status().State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedFirstInstallRecoversBeforeOwnershipChecks(bool checkStatusFirst)
    {
        var runtime = Runtime();
        var package = NativeReShade.ReadPackage(Package());
        Directory.CreateDirectory(Path.GetDirectoryName(runtime.LibraryPath)!);
        Directory.CreateDirectory(Layers);
        File.WriteAllBytes(runtime.LibraryPath, package.Library);
        File.WriteAllBytes(runtime.ManifestPath, package.Manifest);
        var journal = Path.Combine(runtime.Metadata, "transaction.json");
        LinuxPaths.WriteJson(journal, new Journal
        {
            State = new(),
            Files = [new(Path.GetRelativePath(runtime.Prefix, runtime.LibraryPath), null),
                new(Path.GetRelativePath(runtime.Prefix, runtime.ManifestPath), null)]
        });

        if (checkStatusFirst)
        {
            Assert.Equal(NativeRuntimeState.NotInstalled, runtime.Status().State);
            Assert.False(File.Exists(runtime.LibraryPath));
            Assert.False(File.Exists(runtime.ManifestPath));
        }
        runtime.Install(package);
        Assert.Equal(NativeRuntimeState.Installed, runtime.Status().State);
        Assert.False(File.Exists(journal));
        runtime.Remove(new Settings());
        Assert.Equal(NativeRuntimeState.NotInstalled, runtime.Status().State);
    }

    [Fact] public void InterruptedRemovalRecoversOwnershipBeforeRemovalCheck()
    {
        var runtime = Runtime();
        runtime.Install(NativeReShade.ReadPackage(Package()));
        var state = new Installation(runtime.Prefix, runtime.Metadata).ReadState();
        var undo = Path.Combine(runtime.Metadata, "transaction/library");
        Directory.CreateDirectory(Path.GetDirectoryName(undo)!);
        File.Copy(runtime.LibraryPath, undo);
        LinuxPaths.WriteJson(Path.Combine(runtime.Metadata, "transaction.json"), new Journal
        {
            State = state,
            Files = [new(Path.GetRelativePath(runtime.Prefix, runtime.LibraryPath), "transaction/library")]
        });
        File.Delete(runtime.LibraryPath);
        // Simulate interruption after writing the new state but before deleting the journal.
        LinuxPaths.WriteJson(Path.Combine(runtime.Metadata, "manifest.json"), new InstallState());

        runtime.Remove(new Settings());
        Assert.False(File.Exists(runtime.LibraryPath));
        Assert.False(File.Exists(runtime.ManifestPath));
        Assert.False(File.Exists(Path.Combine(runtime.Metadata, "transaction.json")));
        Assert.Equal(NativeRuntimeState.NotInstalled, runtime.Status().State);
    }

    [Fact] public void ReinstallRestoresMissingRuntimeFiles()
    {
        var runtime = Runtime();
        var package = NativeReShade.ReadPackage(Package());
        runtime.Install(package);
        File.Delete(runtime.LibraryPath);
        File.Delete(runtime.ManifestPath);
        Assert.Equal(NativeRuntimeState.Damaged, runtime.Status().State);
        runtime.Install(package);
        Assert.Equal(package.Library, File.ReadAllBytes(runtime.LibraryPath));
        Assert.Equal(package.Manifest, File.ReadAllBytes(runtime.ManifestPath));
        Assert.Equal(NativeRuntimeState.Installed, runtime.Status().State);
    }

    [Fact] public void ChangedRuntimeIsReportedAndPreserved()
    {
        var runtime = Runtime();
        runtime.Install(NativeReShade.ReadPackage(Package()));
        File.WriteAllBytes(runtime.LibraryPath, Elf(type: 2));
        Assert.Equal(NativeRuntimeState.Damaged, runtime.Status().State);
        Assert.Throws<IOException>(() => runtime.Remove(new Settings()));
        var error = Assert.Throws<IOException>(() => runtime.Install(NativeReShade.ReadPackage(Package())));
        Assert.Contains(runtime.LibraryPath, error.Message);
        Assert.Contains("Move it aside", error.Message);
        Assert.Equal(Elf(type: 2), File.ReadAllBytes(runtime.LibraryPath));
        var saved = runtime.LibraryPath + ".saved";
        File.Move(runtime.LibraryPath, saved);
        runtime.Install(NativeReShade.ReadPackage(Package()));
        Assert.Equal(NativeRuntimeState.Installed, runtime.Status().State);
        Assert.Equal(Elf(type: 2), File.ReadAllBytes(saved));
    }

    [Fact] public void SharedRuntimeCannotBeRemovedWhileAGameUsesIt()
    {
        var runtime = Runtime();
        runtime.Install(NativeReShade.ReadPackage(Package()));
        var settings = new Settings();
        settings.Games["steam:1:/a"] = new() { Backend = NativeReShade.Backend };
        settings.Games["steam:2:/b"] = new();
        Assert.Equal(["steam:1:/a"], NativeReShade.Users(settings));
        Assert.Contains("1 game", Assert.Throws<IOException>(() => runtime.Remove(settings)).Message);
        Assert.True(File.Exists(runtime.LibraryPath));
        settings.Games["steam:1:/a"].Backend = null;
        runtime.Remove(settings);
        Assert.False(File.Exists(runtime.LibraryPath));
    }

    [Fact] public void UserManagedInstallationsAreDetectedAndNeverReplaced()
    {
        var foreign = FileAt("usr/share/vulkan/implicit_layer.d/reshade.json", Encoding.UTF8.GetBytes(Manifest("/usr/lib/reshade/ReShade64.so")));
        var runtime = Runtime();
        var status = runtime.Status();
        Assert.Equal(NativeRuntimeState.UserManaged, status.State);
        Assert.Equal([foreign], status.ForeignLayers);
        Assert.Contains("left unchanged", Assert.Throws<IOException>(() => runtime.Install(NativeReShade.ReadPackage(Package()))).Message);
        Assert.False(File.Exists(runtime.LibraryPath));
        File.Delete(foreign);

        // An upstream install in the same prefix is user-managed until RHI owns it.
        var library = FileAt("home/.local/lib/reshade/ReShade64.so", Encoding.UTF8.GetBytes("self-built"));
        FileAt("home/.local/share/vulkan/implicit_layer.d/ReShade64.json", Encoding.UTF8.GetBytes(Manifest()));
        Assert.Equal(NativeRuntimeState.UserManaged, runtime.Status().State);
        Assert.Throws<IOException>(() => runtime.Install(NativeReShade.ReadPackage(Package())));
        Assert.Throws<IOException>(() => runtime.Remove(new Settings()));
        Assert.Equal("self-built", File.ReadAllText(library));
    }

    [Fact] public void OrphanedLibraryIsAConflictAndIsPreserved()
    {
        var runtime = Runtime();
        var library = FileAt("home/.local/lib/reshade/ReShade64.so", Elf());
        var status = runtime.Status();
        Assert.Equal(NativeRuntimeState.ConflictingFiles, status.State);
        Assert.Equal([library], status.ForeignLayers);
        Assert.Throws<IOException>(() => runtime.Install(NativeReShade.ReadPackage(Package())));
        Assert.Throws<IOException>(() => runtime.Remove(new Settings()));
        Assert.Equal(Elf(), File.ReadAllBytes(library));
        Assert.False(File.Exists(runtime.ManifestPath));
        File.Delete(library);
        runtime.Install(NativeReShade.ReadPackage(Package()));
        Assert.Equal(NativeRuntimeState.Installed, runtime.Status().State);
    }

    [Fact] public void LoadEvidenceComesFromTheLayerLogForThisExecutable()
    {
        var runtime = Runtime();
        var exe = FileAt("Native/game.x86_64", Elf());
        Assert.Null(runtime.Evidence(exe));
        var log = FileAt("home/.local/share/reshade/logs/Game-1/ReShade-game.log", Encoding.UTF8.GetBytes(
            $"18:38:59:797 [1] | INFO  | Initializing ReShade version '6.8.0.0' loaded from '/x/lib/reshade/ReShade64.so' into '{LinuxPaths.Canonical(exe)}'.\n"));
        Assert.Equal(log, runtime.Evidence(exe)?.Log);
        Assert.Null(runtime.Evidence(FileAt("Native/other", Elf())));
        // A log older than the installed runtime does not count.
        runtime.Install(NativeReShade.ReadPackage(Package()));
        File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddDays(-1));
        Assert.Null(runtime.Evidence(exe));
    }
}
