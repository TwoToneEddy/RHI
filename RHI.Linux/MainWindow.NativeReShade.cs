using Avalonia.Controls;
using Avalonia.Layout;
using RHI.Core;
using RHI.Linux.Core;

namespace RHI.Linux;

public sealed partial class MainWindow
{
    private readonly NativeReShade _native = new();
    private NativeGameSnapshot? _nativeSnapshot;

    private NativeGameSnapshot ReadNativeSnapshot(Game game) =>
        NativeGameSnapshot.Read(_native, game, _settings.For(game));

    private async Task ShowNativeRuntimeSettings()
    {
        var body = new StackPanel { Spacing = 12 };
        var dialog = Dialog("Native Vulkan ReShade runtime", body);
        void Refresh()
        {
            body.Children.Clear();
            var runtime = _native.Status();
            body.Children.Add(Label(runtime.Label, 13, Secondary));
            var users = NativeReShade.Users(_settings);
            body.Children.Add(Label(users.Count == 0 ? "No games select the native backend. Disable any manually configured launcher options before removal." :
                $"{users.Count} game(s) select the native backend. Disable their launch options and switch their backend in Advanced settings before removal.", 12, Secondary));
            var remove = DialogAction("Remove shared runtime", body, async () =>
            {
                if (!await Confirm("Remove native runtime?", "Remove RHI-owned runtime files? Edited shaders, presets and configuration will be kept.", "Remove")) return;
                await Task.Run(() => _native.Remove(_settings));
                Refresh();
                if (Selected != null) ShowGame();
            }, "danger");
            remove.Name = "RemoveSharedNativeRuntime";
            remove.IsEnabled = users.Count == 0 && runtime.State is NativeRuntimeState.Installed or NativeRuntimeState.Damaged;
            body.Children.Add(remove);
        }
        Refresh();
        await dialog.ShowDialog(this);
    }

    // Shown instead of the Windows/Proton components when a game opts into the experimental backend.
    private Control NativeReShadeSection(Game game, NativeGameSnapshot snapshot)
    {
        var prefs = _settings.For(game);
        _nativeSnapshot = snapshot;
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Row(Label(NativeReShade.Title, 13, null, true), Badge("Experimental", warning: true)));
        body.Children.Add(Label("For native x86-64 Linux games that render with Vulkan. Uses the community Linux port of ReShade (" + NativeReShade.Version +
            ") as an opt-in Vulkan layer, enabled only for launches with RESHADE_ENABLE=1. Windows RenoDX mods, Windows add-ons, RE Framework and other DLL components do not work with it, and it does not provide RenoDX HDR. OpenGL games are not supported.", 11, Secondary));

        var unsupported = snapshot.TargetError ?? snapshot.UnsupportedReason;
        var executable = snapshot.Executable;
        body.Children.Add(Step("Game", unsupported == null ? "✓ " + Path.GetFileName(executable) + " · x86-64 · Vulkan confirmed" : unsupported, unsupported == null,
            Action("Advanced settings", OpenAdvanced, "", "NativeReShadeTarget")));

        var runtime = snapshot.Runtime;
        if (snapshot.RuntimeError != null)
            body.Children.Add(Label("Could not check the runtime: " + snapshot.RuntimeError, 11, Amber));
        var runtimeText = runtime.State switch
        {
            NativeRuntimeState.Installed => "✓ Installed for your user · " + runtime.Version + " (shared by all native games)",
            NativeRuntimeState.Damaged => "Installed files are missing or changed. Reinstall restores missing files. If a runtime file has changed, move it aside first; RHI preserves changed files and reports their paths when reinstalling.",
            NativeRuntimeState.UserManaged => "A ReShade Vulkan layer you installed yourself was found. RHI will use it as-is and will not replace or remove it: " + string.Join(", ", runtime.ForeignLayers),
            NativeRuntimeState.ConflictingFiles => "A ReShade library exists without a discoverable Vulkan layer manifest. Move it aside or repair it with its original installer before installing the runtime: " + string.Join(", ", runtime.ForeignLayers),
            _ => "Not installed. RHI downloads the pinned upstream release " + NativeReShade.Version + " and verifies its checksum."
        };
        var runtimeButtons = Row(Action("⚙", () => ShowReShadeSettings(game), "", "ReShadeSettings"));
        if (runtime.State != NativeRuntimeState.UserManaged)
            runtimeButtons.Children.Add(Action(runtime.State is NativeRuntimeState.Installed or NativeRuntimeState.Damaged ? "↻  Reinstall" : "↓  Install runtime", async () =>
            {
                await _native.Install(_downloads, Progress);
                await Changed(game, "Native Vulkan ReShade " + NativeReShade.Version + " installed", offerSteam: false);
            }, "action", "InstallNativeReShade"));
        runtimeButtons.Children.Add(Action("Manage shared runtime…", ShowNativeRuntimeSettings, "", "ManageNativeRuntime"));
        body.Children.Add(Step("Runtime", runtimeText, runtime.State is NativeRuntimeState.Installed or NativeRuntimeState.UserManaged, runtimeButtons));

        body.Children.Add(NativeActivationStep(game, prefs, snapshot, unsupported == null && runtime.State is NativeRuntimeState.Installed or NativeRuntimeState.UserManaged));

        var evidence = snapshot.Evidence;
        if (snapshot.EvidenceError != null)
            body.Children.Add(Label("Could not read native load evidence: " + snapshot.EvidenceError, 11, Amber));
        body.Children.Add(Step("Last launch", evidence != null
            ? "✓ ReShade's log shows the layer loaded into this executable at " + evidence.Time.ToLocalTime().ToString("g") + ". Check that effects render as expected."
            : "No load recorded yet. Configured launch options are not proof that the layer loaded; launch the game and press Home to open ReShade.", evidence != null, null));

        if (executable != null && File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "ReShade.ini")))
            body.Children.Add(Label("A ReShade.ini beside this executable is used in place of the per-game profile. Move it aside if it belongs to a Windows ReShade installation.", 11, Amber));
        body.Children.Add(Label("Configuration: ReShade creates a profile per game below " + Path.Combine(_native.ReShadeData, "configurations") +
            " (Steam games use the install folder and app ID). Logs are in " + _native.LogRoot + ". Presets and shaders are only as compatible as each effect; test them per game.", 10, Muted));
        body.Children.Add(Row(Plain("Open ReShade data folder", () => { Directory.CreateDirectory(_native.ReShadeData); Proton.Open(_native.ReShadeData); }),
            Plain("Upstream project ↗", () => Proton.Open(Sources.NativeReShadeProject))));
        return Card(body);
    }

    private Control Step(string title, string text, bool good, Control? action)
    {
        var grid = new Grid { ColumnDefinitions = new("100,*,Auto") };
        grid.Children.Add(Label(title, 12, Secondary));
        var detail = Label(text, 11, good ? Green : Amber); detail.Margin = new Avalonia.Thickness(6, 0); Grid.SetColumn(detail, 1); grid.Children.Add(detail);
        if (action != null) { Grid.SetColumn(action, 2); grid.Children.Add(action); }
        return grid;
    }

    private Control NativeActivationStep(Game game, GamePreferences prefs, NativeGameSnapshot snapshot, bool ready)
    {
        var panel = new StackPanel { Spacing = 6 };
        var launcher = NativeReShade.UnsupportedLauncher(game);
        var configs = snapshot.LaunchConfigurations.Where(c => c.Path.Length > 0).Select(c => c.Path).ToList();
        var saved = prefs.SteamConfig;
        var picker = new ComboBox
        {
            ItemsSource = configs.Select(c => "Steam account " + Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(c)))).ToList(),
            SelectedIndex = saved != null && configs.Contains(saved) ? configs.IndexOf(saved) : 0, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        string? Config() => picker.SelectedIndex >= 0 && picker.SelectedIndex < configs.Count ? configs[picker.SelectedIndex] : null;
        var configuration = snapshot.LaunchConfigurations.FirstOrDefault(c => c.Path == Config());
        var readError = configuration?.Error ?? snapshot.LaunchConfigurations.FirstOrDefault(c => c.Path.Length == 0)?.Error;
        var enabled = NativeReShadeLaunchOptions.IsEnabled(configuration?.Options ?? "");
        var status = readError != null ? "Could not read launch options: " + readError :
            launcher ?? (Config() == null ? "Add RESHADE_ENABLE=1 to this game's launcher. Use the preview to copy it." :
                enabled ? "✓ RESHADE_ENABLE=1 is saved in the Steam launch options. Disabling removes only this setting." :
                "Not enabled. RHI adds RESHADE_ENABLE=1 before %command% and keeps your other launch options.");
        var buttons = Row();
        if (launcher == null && Config() != null)
        {
            var toggle = Action(enabled ? "✓ Enabled — Disable" : "Enable for this game", async () =>
            {
                if (Config() is not { } config) return;
                if (Proton.SteamRunning() && !await Confirm("Restart Steam?", "Steam needs to close and reopen to save this game's launch options. Close running games first.", "Apply & restart Steam")) return;
                await NativeReShade.SetActivation(game, prefs, config, !enabled, Progress);
                prefs.SteamConfig = config;
                _settings.Save();
                await Changed(game, enabled ? "Native Vulkan ReShade disabled for " + game.Name : "Native Vulkan ReShade enabled for " + game.Name, offerSteam: false);
            }, enabled ? "success" : "action", "NativeReShadeToggle");
            toggle.IsEnabled = enabled || ready;
            buttons.Children.Add(toggle);
        }
        if (launcher == null) buttons.Children.Add(Action("Preview…", ShowNativeLaunchPreview, "", "NativeReShadePreview"));
        panel.Children.Add(Step("Launch", status, enabled && launcher == null, buttons));
        if (configs.Count > 1 && launcher == null) { picker.SelectionChanged += (_, _) => { prefs.SteamConfig = Config(); _settings.Save(); ShowGame(); }; panel.Children.Add(picker); }
        return panel;
    }

    private async Task ShowNativeLaunchPreview()
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(Label("Paste your current launch options, then preview and copy the result into your launcher. Nothing is saved automatically. For a launcher without %command%, set the environment variable RESHADE_ENABLE=1 for the game instead.", 12, Secondary));
        var original = new TextBox { Text = "%command%", Watermark = "Existing launch options" };
        var preview = new TextBox { IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var status = Label("", 11, Muted);
        var copy = DialogAction("Copy preview", body, async () =>
        {
            if (Clipboard == null) throw new IOException("Clipboard unavailable.");
            await Clipboard.SetTextAsync(preview.Text ?? "");
            status.Text = "Copied. Paste into your game's launch options.";
        });
        copy.IsEnabled = false;
        void Generate(bool enable)
        {
            preview.Text = ""; copy.IsEnabled = false;
            try { preview.Text = enable ? NativeReShadeLaunchOptions.Enable(original.Text ?? "") : NativeReShadeLaunchOptions.Disable(original.Text ?? ""); copy.IsEnabled = true; status.Text = "Review the preview before copying."; }
            catch (FormatException ex) { status.Text = ex.Message; }
        }
        original.TextChanged += (_, _) => { preview.Text = ""; copy.IsEnabled = false; status.Text = "Generate a new preview after editing."; };
        body.Children.Add(original);
        body.Children.Add(Row(Plain("Preview enable", () => Generate(true)), Plain("Preview removal", () => Generate(false))));
        body.Children.Add(preview); body.Children.Add(copy); body.Children.Add(status);
        await Dialog(NativeReShade.Title, body).ShowDialog(this);
    }
}
