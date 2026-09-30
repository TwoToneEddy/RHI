using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using RHI.Linux.Core;
using System.Text;
using Xunit;

namespace RHI.Linux.UiTests;

public sealed class NativeReShadeUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-ui-native-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldData = Environment.GetEnvironmentVariable("XDG_DATA_HOME"), _oldCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
    private MainWindow? _window;
    public NativeReShadeUiTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "data"));
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(_root, "cache"));
    }
    public void Dispose()
    {
        if (_window != null) { foreach (var child in _window.OwnedWindows.ToArray()) child.Close(); _window.Close(); }
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _oldData); Environment.SetEnvironmentVariable("XDG_CACHE_HOME", _oldCache);
        Directory.Delete(_root, true);
    }
    private static byte[] Elf()
    {
        var bytes = new byte[256];
        bytes[0] = 0x7F; bytes[1] = (byte)'E'; bytes[2] = (byte)'L'; bytes[3] = (byte)'F';
        bytes[4] = 2; bytes[5] = 1; bytes[6] = 1; bytes[16] = 3; bytes[18] = 0x3E;
        bytes[20] = 1; bytes[24] = 128; bytes[32] = 64; bytes[52] = 64; bytes[54] = 56; bytes[56] = 1;
        bytes[64] = 1; bytes[68] = 5; bytes[97] = 1; bytes[105] = 1;
        return bytes;
    }
    // A folder with both a Windows and a native build, as some Steam games ship.
    private Game MixedGame(bool native, bool confirmed = true)
    {
        var root = Path.Combine(_root, "Mixed Game"); Directory.CreateDirectory(root);
        var exe = Path.Combine(root, "Game.exe"); File.WriteAllText(exe, "windows build");
        var elf = Path.Combine(root, "game.x86_64"); File.WriteAllBytes(elf, Elf());
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(elf, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var game = new Game { Name = "Mixed Game", Root = root, Executable = exe, Executables = [exe] };
        if (native)
        {
            var settings = Settings.Load(); var prefs = settings.For(game);
            prefs.Backend = NativeReShade.Backend; prefs.NativeExecutable = elf; prefs.NativeVulkanConfirmed = confirmed; settings.Save();
        }
        return game;
    }
    private async Task Open(Game game)
    {
        _window = new MainWindow([game]); _window.Show();
        for (var i = 0; i < 100 && !Text.Contains("Advanced settings"); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs();
    }
    private IEnumerable<T> All<T>() => _window!.GetLogicalDescendants().OfType<T>();
    private string Text => string.Join("\n", All<TextBlock>().Select(t => t.Text));
    private bool Has(string name) => All<Button>().Any(b => b.Name == name);

    private Task Invoke(string method, params object[] args) => (Task)typeof(MainWindow)
        .GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(_window, args)!;

    [AvaloniaFact] public async Task OrphanedLibraryDoesNotOfferActivationAsReady()
    {
        var game = MixedGame(native: true);
        game.AppId = "123";
        game.SteamRoot = Path.Combine(_root, "Steam");
        var config = Path.Combine(game.SteamRoot, "userdata/1/config/localconfig.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "\"UserLocalConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { \"apps\" { \"123\" { \"LaunchOptions\" \"%command%\" } } } } } }");
        // Persist the native preferences under the Steam identity as well.
        var settings = Settings.Load();
        var prefs = settings.For(game);
        prefs.Backend = NativeReShade.Backend;
        prefs.NativeExecutable = Path.Combine(game.Root, "game.x86_64");
        prefs.NativeVulkanConfirmed = true;
        settings.Save();
        var runtime = new NativeReShade();
        Directory.CreateDirectory(Path.GetDirectoryName(runtime.LibraryPath)!);
        File.WriteAllBytes(runtime.LibraryPath, Elf());
        await Open(game);
        Assert.Contains("without a discoverable Vulkan layer manifest", Text);
        Assert.DoesNotContain("will use it as-is", Text);
        Assert.True(Has("InstallNativeReShade"));
        Assert.False(All<Button>().Single(b => b.Name == "NativeReShadeToggle").IsEnabled);
    }

    [AvaloniaFact] public async Task NewNativeLogRefreshesAnAlreadyOpenPage()
    {
        var game = MixedGame(native: true);
        await Open(game);
        Assert.Contains("No load recorded yet", Text);
        var log = Path.Combine(_root, "data/reshade/logs/game.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, $"Initializing ReShade version '6.8.0' loaded from '/lib/ReShade64.so' into '{Path.Combine(game.Root, "game.x86_64")}'");
        await Invoke("RefreshStatus");
        Assert.Contains("log shows the layer loaded", Text);
    }

    [AvaloniaFact] public async Task ExternalLaunchOptionChangesRefreshNativeToggle()
    {
        var game = MixedGame(native: true);
        var settings = Settings.Load();
        var prefs = settings.For(game);
        game.AppId = "123";
        game.SteamRoot = Path.Combine(_root, "Steam");
        settings.Games[game.Id] = prefs;
        settings.Save();
        var config = Path.Combine(game.SteamRoot, "userdata/1/config/localconfig.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        void Write(string value) => File.WriteAllText(config,
            "\"UserLocalConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { \"apps\" { \"123\" { \"LaunchOptions\" \"" + value + "\" } } } } } }");
        Write("%command%");
        await Open(game);
        Button Toggle() => All<Button>().Single(b => b.Name == "NativeReShadeToggle");
        Assert.Equal("Enable for this game", Toggle().Content);
        Write("RESHADE_ENABLE=1 %command%");
        await Invoke("RefreshStatus");
        Assert.Equal("✓ Enabled — Disable", Toggle().Content);
        Write("%command%");
        await Invoke("RefreshStatus");
        Assert.Equal("Enable for this game", Toggle().Content);
    }

    [AvaloniaFact] public async Task WindowsSetupRejectsNativeBackend()
    {
        var game = MixedGame(native: true);
        await Open(game);
        await Assert.ThrowsAsync<IOException>(() => Invoke("ShowSteamSetup", game));
        Assert.Empty(_window!.OwnedWindows);
    }

    [AvaloniaFact] public async Task SharedRuntimeRemovalIsAvailableAfterLastGameSwitchesBack()
    {
        var game = MixedGame(native: true);
        var runtime = new NativeReShade();
        runtime.Install(new NativePackage(Elf(), Encoding.UTF8.GetBytes("{}"), [], null));
        var settings = Settings.Load();
        Assert.Throws<IOException>(() => runtime.Remove(settings));
        settings.For(game).Backend = null;
        settings.Save();
        await Open(game);
        var pending = Invoke("ShowNativeRuntimeSettings");
        Dispatcher.UIThread.RunJobs();
        var dialog = Assert.Single(_window!.OwnedWindows);
        var remove = dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "RemoveSharedNativeRuntime");
        Assert.True(remove.IsEnabled);
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var confirmation = _window.OwnedWindows.Single(w => w != dialog);
        confirmation.GetLogicalDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Remove")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 100 && File.Exists(runtime.LibraryPath); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.False(File.Exists(runtime.LibraryPath));
        Assert.False(File.Exists(runtime.ManifestPath));
        dialog.Close();
        await pending;
    }

    [AvaloniaFact] public async Task ReShadeCogOffersNativeSetupWithoutAWindowsExecutable()
    {
        var game = MixedGame(native: false);
        File.Delete(game.Executable!); game.Executable = null; game.Executables = [];
        await Open(game);
        All<Button>().Single(b => b.Name == "ReShadeSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var dialog = Assert.Single(_window!.OwnedWindows);
        var picker = dialog.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "BackendPicker");
        picker.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.True(NativeReShade.Selected(Settings.Load().For(game)));
        Assert.Contains(dialog.GetLogicalDescendants().OfType<Button>(), b => b.Content?.ToString() == "Choose native Linux executable…");
        Assert.DoesNotContain(dialog.GetLogicalDescendants().OfType<ComboBox>(), c => c.Name == "ReShadeChannel");
        Assert.True(Has("ReShadeSettings"));
        dialog.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "BackendPicker").SelectedIndex = 0;
        Assert.False(NativeReShade.Selected(Settings.Load().For(game)));
        Assert.Contains(dialog.GetLogicalDescendants().OfType<ComboBox>(), c => c.Name == "ReShadeChannel");
        dialog.Close();
    }

    [AvaloniaFact] public async Task SharedBackendEditorRestoresSelectionAfterRejectedSwitch()
    {
        var game = MixedGame(native: false);
        new Installation(game.Root).Install("ReShade", "test", [new("dxgi.dll", Encoding.UTF8.GetBytes("payload"))], proxy: "dxgi.dll");
        await Open(game);
        var pending = Invoke("ShowReShadeSettings", game);
        Dispatcher.UIThread.RunJobs();
        var dialog = Assert.Single(_window!.OwnedWindows);
        dialog.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "BackendPicker").SelectedIndex = 1;
        Assert.Equal(0, dialog.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "BackendPicker").SelectedIndex);
        Assert.False(NativeReShade.Selected(Settings.Load().For(game)));
        Assert.Contains(dialog.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Windows ReShade is installed") == true);
        dialog.Close();
        await pending;
    }

    [AvaloniaFact] public async Task DefaultGamesKeepTheWindowsWorkflowWithoutNativeControls()
    {
        await Open(MixedGame(native: false));
        Assert.True(Has("InstallReShade")); Assert.True(Has("NativeHdrToggle")); Assert.True(Has("InstallRecommended"));
        Assert.False(Has("InstallNativeReShade")); Assert.False(Has("NativeReShadeToggle"));
        Assert.DoesNotContain(NativeReShade.Title, Text);
        Assert.Contains("Proton", Text);
    }

    [AvaloniaFact] public async Task NativeTargetsHideWindowsOnlyActions()
    {
        await Open(MixedGame(native: true));
        Assert.Contains(NativeReShade.Title, Text); Assert.Contains("Experimental", Text); Assert.Contains("Native Linux", Text);
        Assert.Contains("game.x86_64 · x86-64 · Vulkan confirmed", Text);
        Assert.Contains("does not provide RenoDX HDR", Text);
        foreach (var name in new[] { "InstallReShade", "InstallRenoDX", "InstallRecommended", "NativeHdrToggle", "ReEngineWineDetectionToggle", "InstallShaders:Standard" })
            Assert.False(Has(name), name);
        Assert.True(Has("InstallNativeReShade")); Assert.True(Has("NativeReShadePreview"));
        Assert.DoesNotContain("WINEDLLOVERRIDES", Text); Assert.DoesNotContain("Proton", string.Join("\n", All<Border>().Select(b => (b.Child as TextBlock)?.Text)));
        // Without a Steam account configuration activation is copied by the user, never saved.
        Assert.False(Has("NativeReShadeToggle"));
    }

    [AvaloniaFact] public async Task ConfigurationIsNotReportedAsLoading()
    {
        await Open(MixedGame(native: true));
        Assert.Contains("No load recorded yet", Text);
        Assert.DoesNotContain("log shows the layer loaded", Text);
    }

    [AvaloniaFact] public async Task LayerLogForTheExecutableIsShownAsLoadEvidence()
    {
        var game = MixedGame(native: true);
        var log = Path.Combine(_root, "data/reshade/logs/Mixed_Game/ReShade.log"); Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, $"INFO | Initializing ReShade version '6.8.0.0' loaded from '/lib/ReShade64.so' into '{LinuxPaths.Canonical(Path.Combine(game.Root, "game.x86_64"))}'.\n");
        await Open(game);
        Assert.Contains("log shows the layer loaded", Text);
    }

    [AvaloniaFact] public async Task UnconfirmedVulkanIsExplained()
    {
        await Open(MixedGame(native: true, confirmed: false));
        Assert.Contains("renders with Vulkan", Text);
        Assert.DoesNotContain("Vulkan confirmed", Text);
    }

    [AvaloniaFact] public async Task ShaderDialogDoesNotInstallWindowsPacksForNativeTargets()
    {
        await Open(MixedGame(native: true));
        All<Button>().First(b => b.Content?.ToString() == "Shaders/Addons").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 50 && _window!.OwnedWindows.Count == 0; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        var dialog = Assert.Single(_window!.OwnedWindows);
        var text = string.Join("\n", dialog.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains("Windows add-ons and RenoDX are not compatible", text);
        Assert.DoesNotContain(dialog.GetLogicalDescendants().OfType<Button>(), b => b.Content?.ToString()?.StartsWith("Install") == true);
    }
}
