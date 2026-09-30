using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RHI.Linux.Core;

public enum NativeRuntimeState { NotInstalled, Installed, Damaged, UserManaged, ConflictingFiles }

public sealed record NativeRuntimeStatus(NativeRuntimeState State, string? Version, IReadOnlyList<string> ForeignLayers)
{
    public string Label => State switch
    {
        NativeRuntimeState.Installed => "Installed · " + Version,
        NativeRuntimeState.Damaged => "Needs repair",
        NativeRuntimeState.UserManaged => "User-managed installation",
        NativeRuntimeState.ConflictingFiles => "Conflicting runtime files",
        _ => "Not installed"
    };
}

// Evidence that the layer initialised in this executable, taken from ReShade's own log.
// It does not show that effects rendered correctly.
public sealed record NativeLoadEvidence(string Log, DateTime Time);

public sealed record NativePackage(byte[] Library, byte[] Manifest, IReadOnlyList<(string Path, byte[] Content)> Shaders, byte[]? License);

// EXPERIMENTAL: the native Linux/Vulkan ReShade port (Sources.NativeReShadeProject).
// Its archive installs one shared runtime for the user: the host library, an implicit Vulkan layer
// manifest that stays inactive unless RESHADE_ENABLE=1 is set, and the standard shaders.
// RHI installs the same layout, records ownership in its own data folder, never runs the archive's
// install scripts and never installs its optional development add-ons.
public sealed class NativeReShade
{
    public const string Backend = "NativeVulkan";
    public const string Title = "Native Vulkan ReShade (experimental)";
    public const string Component = "Native Vulkan ReShade";
    public const string Version = Sources.NativeReShadeVersion;
    public const string ArchiveRoot = "reshade-linux-vulkan-" + Version + "-x86_64";
    public const string LayerName = "VK_LAYER_reshade";
    // Values written by upstream's CMake install rule and res/reshade_layer.json.in.
    private const string PackageLibrary = "lib/reshade/ReShade64.so";
    private const string PackageManifest = "share/vulkan/implicit_layer.d/ReShade64.json";
    private const string PackageShaders = "share/reshade/reshade-shaders/";
    private const string ManifestLibraryPath = "../../../lib/reshade/ReShade64.so";
    private const long PackageLimit = 256L * 1024 * 1024;

    private readonly IReadOnlyList<string> _layerDirectories;

    // XDG_DATA_HOME, where the Vulkan loader finds user layers and ReShade keeps configurations and logs.
    public string DataHome { get; }
    // The manifest resolves the library relative to itself: <DataHome>/../lib/reshade.
    public string Prefix => Path.GetDirectoryName(DataHome.TrimEnd('/'))!;
    public string Metadata { get; }
    public string LibraryPath => Path.Combine(Prefix, PackageLibrary);
    public string ManifestPath => Path.Combine(DataHome, "vulkan/implicit_layer.d/ReShade64.json");
    public string ReShadeData => Path.Combine(DataHome, "reshade");
    public string ShaderRoot => Path.Combine(ReShadeData, "reshade-shaders");
    public string LogRoot => Path.Combine(ReShadeData, "logs");
    private string Relative(string path) => Path.GetRelativePath(Prefix, path);

    public NativeReShade(string? dataHome = null, string? metadata = null, IEnumerable<string>? layerDirectories = null)
    {
        DataHome = Path.GetFullPath(dataHome ?? (Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(LinuxPaths.Home, ".local/share")));
        Metadata = metadata ?? Path.Combine(LinuxPaths.Data, "native-reshade");
        _layerDirectories = (layerDirectories ?? DefaultLayerDirectories(DataHome)).ToList();
    }

    // Implicit-layer search locations used by the Vulkan loader on Linux.
    public static IEnumerable<string> DefaultLayerDirectories(string dataHome)
    {
        string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
        var config = Env("XDG_CONFIG_HOME", Path.Combine(LinuxPaths.Home, ".config"));
        var roots = new[] { config }.Concat(Env("XDG_CONFIG_DIRS", "/etc/xdg").Split(':')).Append("/etc")
            .Append(dataHome).Concat(Env("XDG_DATA_DIRS", "/usr/local/share:/usr/share").Split(':'));
        return roots.Where(r => r.Length > 0).Select(r => Path.Combine(r, "vulkan/implicit_layer.d")).Distinct();
    }

    public static bool Selected(GamePreferences preferences) => preferences.Backend == Backend;

    public static void SelectBackend(Game game, GamePreferences preferences, bool native)
    {
        if (native) RequireNoWindowsReShade(game, null);
        else if (Proton.LocalConfigs(game).Any(c => (Proton.ReadOptions(c, game.AppId!) ?? "").Contains(NativeReShadeLaunchOptions.Variable, StringComparison.Ordinal)))
            throw new IOException("Disable Native Vulkan ReShade on the game page first, so RESHADE_ENABLE is removed from the Steam launch options.");
        preferences.Backend = native ? Backend : null;
    }

    public static void RequireWindowsBackend(GamePreferences preferences)
    {
        if (Selected(preferences))
            throw new IOException("This game uses " + Title + ". Windows ReShade, RenoDX and other DLL components are unavailable for it. Switch the ReShade backend back in Advanced settings first.");
    }

    // Windows ReShade must not be set up for a launch that also enables the native layer.
    public static void RequireNotActivated(string options)
    {
        if (options.Contains(NativeReShadeLaunchOptions.Variable, StringComparison.Ordinal))
            throw new IOException("These launch options enable Native Vulkan ReShade (RESHADE_ENABLE). Disable it before setting up Windows ReShade, so both are not loaded together.");
    }

    // Games that use the shared runtime; removing it would break their activation.
    public static IReadOnlyList<string> Users(Settings settings) => settings.Games.Where(p => Selected(p.Value)).Select(p => p.Key).ToList();

    public static string? Executable(Game game, GamePreferences preferences) =>
        preferences.NativeExecutable is { } path && File.Exists(path) && LinuxPaths.IsWithin(game.Root, path) ? path : null;

    // Where the target is a runnable, confirmed native x86-64 Vulkan game; otherwise the reason it is not.
    public static string? Unsupported(Game game, GamePreferences preferences)
    {
        if (!Selected(preferences)) return "Native Vulkan ReShade is not selected for this game.";
        if (Executable(game, preferences) is not { } executable) return "Choose the game's native Linux executable in Advanced settings.";
        try { NativeBinary.RequireGameExecutable(executable, game.Root); }
        catch (IOException ex) { return ex.Message; }
        if (!preferences.NativeVulkanConfirmed) return "Confirm in Advanced settings that this game renders with Vulkan. OpenGL games are not supported.";
        return null;
    }

    // Only the native Steam client and other launchers on the host can see ~/.local layers.
    public static string? UnsupportedLauncher(Game game)
    {
        var source = game.SteamRoot is { } root ? GameDiscovery.SteamSource(root) : game.Source;
        return source is "Steam Flatpak" or "Steam Snap"
            ? source + " runs games in a sandbox that cannot see layers installed in your home directory. This experimental backend supports the native Steam client and other host launchers only."
            : null;
    }

    // Adds or removes RESHADE_ENABLE=1 for one Steam account, with the usual backup and Steam restart.
    public static Task SetActivation(Game game, GamePreferences preferences, string config, bool enable, IProgress<string>? progress)
    {
        if (enable && (Unsupported(game, preferences) ?? UnsupportedLauncher(game)) is { } reason) throw new IOException(reason);
        GameSetup.RequireClosed(game, Executable(game, preferences));
        return GameSetup.ConfigureLaunchOptions(game, config, progress, options =>
        {
            if (!enable) return NativeReShadeLaunchOptions.Disable(options);
            RequireNoWindowsReShade(game, options);
            return NativeReShadeLaunchOptions.Enable(options);
        });
    }

    // Both backends must not load in the same launch. An existing Windows installation is reported, not deleted.
    public static void RequireNoWindowsReShade(Game game, string? options)
    {
        if (game.Executable != null && File.Exists(game.Executable) && new Installation(game.InstallDirectory).ReadState().Components.ContainsKey("ReShade"))
            throw new IOException("Windows ReShade is installed for this game by RHI. Remove it in Advanced settings first; RHI will not delete it automatically.");
        var overrides = Proton.ReadVariable(options ?? "", "WINEDLLOVERRIDES") ?? "";
        if (overrides.Split(';').Select(entry => entry.Split('=', 2)).Any(pair => pair.Length == 2 &&
            pair[1].Split(',').Any(mode => mode.Trim().Equals("n", StringComparison.OrdinalIgnoreCase)) &&
            pair[0].Split(',').Any(name => new[] { "dxgi", "d3d9", "opengl32" }.Contains(
                Path.GetFileNameWithoutExtension(name.Trim()), StringComparer.OrdinalIgnoreCase))))
            throw new IOException("These launch options load Windows ReShade through WINEDLLOVERRIDES. Remove that override before enabling Native Vulkan ReShade.");
    }

    public List<string> ForeignLayers(bool owned)
    {
        var result = new List<string>();
        foreach (var directory in _layerDirectories.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(directory, "*.json").ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                if (owned && Path.GetFullPath(file) == ManifestPath) continue;
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(file));
                    var layers = json.RootElement.TryGetProperty("layers", out var many) && many.ValueKind == JsonValueKind.Array ? many.EnumerateArray().ToList()
                        : json.RootElement.TryGetProperty("layer", out var one) ? [one] : [];
                    if (layers.Any(l => l.TryGetProperty("name", out var name) && name.GetString() == LayerName)) result.Add(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
            }
        }
        if (!owned && File.Exists(LibraryPath)) result.Add(LibraryPath);
        return result.Distinct().ToList();
    }

    public NativeRuntimeStatus Status()
    {
        var state = new InstallState();
        if (Directory.Exists(Prefix))
        {
            var installation = new Installation(Prefix, Metadata);
            installation.RecoverPendingTransaction();
            state = installation.ReadState();
        }
        var owned = state.Components.TryGetValue(Component, out var version);
        var foreign = ForeignLayers(owned);
        if (!owned)
        {
            // A library alone is a conflict, but cannot be discovered by the Vulkan loader.
            var hasLayer = foreign.Any(path => path != LibraryPath);
            return new(hasLayer ? NativeRuntimeState.UserManaged : foreign.Count > 0
                ? NativeRuntimeState.ConflictingFiles : NativeRuntimeState.NotInstalled, null, foreign);
        }
        var valid = state.Files.Where(f => f.Component == Component).All(f =>
        {
            var path = Path.Combine(Prefix, f.Path);
            return File.Exists(path) && (f.Mutable || Installation.Hash(File.ReadAllBytes(path)) == f.Hash);
        });
        return new(valid ? NativeRuntimeState.Installed : NativeRuntimeState.Damaged, version, foreign);
    }

    public async Task Install(Downloads downloads, IProgress<string>? progress = null)
    {
        RequireNoForeign();
        var archive = await downloads.Fetch(Sources.NativeReShadeArchive, progress);
        using (var stream = File.OpenRead(archive))
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(Sources.NativeReShadeArchiveSha256, StringComparison.OrdinalIgnoreCase))
            {
                stream.Close(); File.Delete(archive);
                throw new IOException("The Native Vulkan ReShade download did not match its pinned checksum and was deleted. Try again later.");
            }
        progress?.Report("Checking Native Vulkan ReShade " + Version);
        var package = await ReadArchive(archive);
        await Task.Run(() => Install(package));
    }

    private void RequireNoForeign()
    {
        var status = Status();
        if (status.State == NativeRuntimeState.UserManaged || status.ForeignLayers.Count > 0)
            throw new IOException("Another ReShade Vulkan layer is already installed and was left unchanged: " + string.Join(", ", status.ForeignLayers) +
                ". Use that installation, or remove it with its own uninstaller before installing through RHI.");
    }

    // 7-Zip decompresses the .tar.xz; entries are read in memory and never extracted directly.
    public static async Task<NativePackage> ReadArchive(string archive)
    {
        var start = new ProcessStartInfo(ArchiveTools.SevenZip) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "e", "-so", "-txz", archive }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start 7z.");
        var error = process.StandardError.ReadToEndAsync();
        using var tar = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(tar);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("Could not decompress Native Vulkan ReShade: " + await error);
        tar.Position = 0;
        return ReadPackage(tar);
    }

    public static NativePackage ReadPackage(Stream tar)
    {
        byte[]? library = null, manifest = null, license = null;
        var shaders = new List<(string, byte[])>();
        long total = 0;
        using var reader = new TarReader(tar);
        while (reader.GetNextEntry() is { } entry)
        {
            var name = entry.Name.TrimEnd('/');
            if (name == ArchiveRoot && entry.EntryType == TarEntryType.Directory) continue;
            if (!name.StartsWith(ArchiveRoot + "/", StringComparison.Ordinal) || name.Contains('\\') ||
                name.Split('/').Any(s => s is "" or "." or ".."))
                throw new IOException("The Native Vulkan ReShade package contains an unsafe path: " + entry.Name);
            var relative = name[(ArchiveRoot.Length + 1)..];
            if (entry.EntryType == TarEntryType.Directory) continue;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new IOException("The Native Vulkan ReShade package contains an unsupported link or special file: " + relative);
            total += entry.Length;
            if (total > PackageLimit) throw new IOException("The Native Vulkan ReShade package is unexpectedly large.");
            // Installer scripts are never run and optional development add-ons stay unselected.
            var wanted = relative is PackageLibrary or PackageManifest or "LICENSE.md" || relative.StartsWith(PackageShaders, StringComparison.Ordinal);
            if (!wanted) continue;
            using var content = new MemoryStream();
            entry.DataStream?.CopyTo(content);
            var bytes = content.ToArray();
            if (relative == PackageLibrary) library = bytes;
            else if (relative == PackageManifest) manifest = bytes;
            else if (relative == "LICENSE.md") license = bytes;
            else shaders.Add((relative[PackageShaders.Length..], bytes));
        }
        if (library == null || manifest == null) throw new IOException("The Native Vulkan ReShade package is missing its host library or Vulkan layer manifest.");
        if (NativeBinary.Inspect(library) != NativeBinaryKind.ElfX64) throw new IOException("The Native Vulkan ReShade host library is not an x86-64 Linux library.");
        ValidateManifest(manifest);
        return new(library, manifest, shaders, license);
    }

    // Refuse a manifest that could enable the layer globally or load another library.
    public static void ValidateManifest(byte[] manifest)
    {
        try
        {
            using var json = JsonDocument.Parse(manifest);
            var layer = json.RootElement.GetProperty("layer");
            if (layer.GetProperty("name").GetString() != LayerName ||
                layer.GetProperty("library_path").GetString() != ManifestLibraryPath ||
                layer.GetProperty("enable_environment").GetProperty(NativeReShadeLaunchOptions.Variable).GetString() != "1" ||
                layer.GetProperty("disable_environment").GetProperty(NativeReShadeLaunchOptions.DisableVariable).GetString() != "1")
                throw new IOException();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            throw new IOException("The Native Vulkan ReShade layer manifest is not the expected opt-in layer. RHI did not install it.");
        }
    }

    public void Install(NativePackage package)
    {
        RequireNoForeign();
        Directory.CreateDirectory(Prefix);
        var payloads = new List<Payload> { new(Relative(LibraryPath), package.Library), new(Relative(ManifestPath), package.Manifest) };
        // Shaders are seeded: existing files, including user edits and other packs, are never replaced.
        payloads.AddRange(package.Shaders.Select(s => new Payload(Relative(Path.Combine(ShaderRoot, s.Path)), s.Content, SeedOnly: true)));
        new Installation(Prefix, Metadata).Install(Component, Version, payloads);
        if (package.License != null) File.WriteAllBytes(Path.Combine(Metadata, "LICENSE.md"), package.License);
    }

    // Removes only files RHI installed and nobody changed. Configurations, presets, logs,
    // add-ons and edited shaders in the ReShade data folder are preserved.
    public void Remove(Settings settings)
    {
        var users = Users(settings);
        if (users.Count > 0)
            throw new IOException($"{users.Count} game(s) still use Native Vulkan ReShade. Switch them back to Windows ReShade in Advanced settings first; their Steam launch option must be disabled too.");
        var installation = new Installation(Prefix, Metadata);
        installation.RecoverPendingTransaction();
        if (!installation.ReadState().Components.ContainsKey(Component))
            throw new IOException("RHI did not install this Native Vulkan ReShade runtime, so it was left unchanged. Use the uninstaller of the installation you set up.");
        installation.Remove(Component);
    }

    public NativeLoadEvidence? Evidence(string executable)
    {
        if (!Directory.Exists(LogRoot)) return null;
        var target = LinuxPaths.Canonical(executable);
        var since = File.Exists(LibraryPath) ? File.GetLastWriteTimeUtc(LibraryPath) : DateTime.MinValue;
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
        foreach (var log in Directory.EnumerateFiles(LogRoot, "*.log", options).OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var time = File.GetLastWriteTimeUtc(log);
            if (time < since) break;
            try
            {
                using var reader = new StreamReader(log);
                var buffer = new char[65536];
                var text = new string(buffer, 0, reader.ReadBlock(buffer));
                // Written by ReShade when the layer initialises in a process.
                if (Regex.IsMatch(text, "Initializing ReShade version '[^']*' loaded from '[^']*' into '" + Regex.Escape(target) + "'"))
                    return new(log, time);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}
