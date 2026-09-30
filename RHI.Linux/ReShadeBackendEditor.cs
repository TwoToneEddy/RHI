using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using RHI.Linux.Core;

namespace RHI.Linux;

// Shared by the ReShade cog and Advanced settings. The host owns persistence and
// rebuilding its surrounding Windows/native controls after a successful edit.
public sealed class ReShadeBackendEditor : StackPanel
{
    private readonly Window _owner;
    private readonly Game _game;
    private readonly GamePreferences _preferences;
    private readonly Action _save;
    private readonly Action _changed;
    private readonly TextBlock _error = Text("");

    public ReShadeBackendEditor(Window owner, Game game, GamePreferences preferences, Action save, Action changed)
    {
        _owner = owner;
        _game = game;
        _preferences = preferences;
        _save = save;
        _changed = changed;
        Spacing = 12;
        Build();
    }

    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private void Build()
    {
        Children.Clear();
        Children.Add(Text("ReShade backend"));
        var picker = new ComboBox
        {
            ItemsSource = new[] { "Windows ReShade through Proton (default)", NativeReShade.Title },
            SelectedIndex = NativeReShade.Selected(_preferences) ? 1 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Name = "BackendPicker"
        };
        picker.SelectionChanged += (_, _) => ChangeBackend(picker.SelectedIndex == 1);
        Children.Add(picker);
        Children.Add(_error);
        if (!NativeReShade.Selected(_preferences)) return;

        Children.Add(Text("For native x86-64 Linux Vulkan games. Windows RenoDX mods and DLL add-ons are not compatible."));
        var executable = NativeReShade.Executable(_game, _preferences);
        Children.Add(Text("Native executable: " + (executable ?? "None selected")));
        var choose = new Button { Content = "Choose native Linux executable…" };
        choose.Click += async (_, _) => await ChooseNativeExecutable();
        Children.Add(choose);
        var vulkan = new CheckBox
        {
            Content = "This game renders with Vulkan",
            IsChecked = _preferences.NativeVulkanConfirmed,
            IsEnabled = executable != null,
            Name = "NativeVulkanConfirmed"
        };
        vulkan.IsCheckedChanged += (_, _) => Update(() => _preferences.NativeVulkanConfirmed = vulkan.IsChecked == true);
        Children.Add(vulkan);
        Children.Add(Text(NativeReShade.Unsupported(_game, _preferences) ?? "Ready. Install the runtime and enable it on the game page."));
    }

    private void ChangeBackend(bool native)
    {
        if (native == NativeReShade.Selected(_preferences)) return;
        Update(() => NativeReShade.SelectBackend(_game, _preferences, native));
    }

    private async Task ChooseNativeExecutable()
    {
        try
        {
            var files = await _owner.StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "Choose the native Linux game executable", AllowMultiple = false
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            Update(() =>
            {
                NativeBinary.RequireGameExecutable(path, _game.Root);
                _preferences.NativeExecutable = LinuxPaths.Canonical(path);
                _preferences.NativeVulkanConfirmed = false;
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Update(Action edit)
    {
        try
        {
            edit();
            _save();
            _error.Text = "";
            Build();
            _changed();
        }
        catch (Exception ex)
        {
            Build(); // Restore the picker after a rejected backend change.
            ShowError(ex);
        }
    }

    private void ShowError(Exception error)
    {
        _error.Text = error is IOException or UnauthorizedAccessException or FormatException
            ? error.Message
            : "Unexpected error updating ReShade settings: " + error.Message;
    }
}
