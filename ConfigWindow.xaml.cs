using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ComfyTray;

/// <summary>
/// Editor for the <see cref="HookSettings">hook commands</see>. Saving is blocked until every
/// non-empty command names a program that exists on disk (or on PATH), so a typo is caught here
/// rather than silently at start/stop time. On save the settings object passed in is updated in
/// place and written to the registry.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated from MainWindow")]
internal sealed partial class ConfigWindow : Window
{
    private readonly HookSettings _settings;

    public ConfigWindow(HookSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        BeforeStartBox.Text = settings.BeforeStartCommand;
        AfterStopBox.Text = settings.AfterStopCommand;
        Loaded += (_, _) => _ = BeforeStartBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate(BeforeStartBox, "Before Start") || !Validate(AfterStopBox, "After Stop"))
        {
            return;
        }

        _settings.BeforeStartCommand = BeforeStartBox.Text.Trim();
        _settings.AfterStopCommand = AfterStopBox.Text.Trim();
        if (!_settings.TrySave(out var error))
        {
            ErrorText.Text = $"Could not save to the registry: {error}";
            return;
        }

        // Setting DialogResult closes a window shown with ShowDialog().
        DialogResult = true;
    }

    /// <summary>
    /// True when the box is empty (no hook) or names a program that resolves to a real file.
    /// Reports the failure inline and focuses the offending box.
    /// </summary>
    private bool Validate(TextBox box, string label)
    {
        ErrorText.Text = string.Empty;
        var text = box.Text.Trim();
        if (text.Length == 0)
        {
            return true;
        }

        if (HookCommand.TryResolve(text, out _, out _, out var error))
        {
            return true;
        }

        ErrorText.Text = $"{label} command: {error}";
        _ = box.Focus();
        return false;
    }

    private void BrowseBeforeStart_Click(object sender, RoutedEventArgs e) => Browse(BeforeStartBox);

    private void BrowseAfterStop_Click(object sender, RoutedEventArgs e) => Browse(AfterStopBox);

    /// <summary>
    /// Picks a program, replacing only the executable part of the box so any arguments the
    /// user has already typed are kept. The path is quoted when it contains spaces.
    /// </summary>
    private static void Browse(TextBox box)
    {
        var (_, arguments) = HookCommand.Split(box.Text);
        var dialog = new OpenFileDialog
        {
            Title = "Select a program",
            Filter = "Programs (*.exe;*.bat;*.cmd;*.com)|*.exe;*.bat;*.cmd;*.com|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var path = dialog.FileName.Contains(' ', System.StringComparison.Ordinal)
            ? $"\"{dialog.FileName}\""
            : dialog.FileName;
        box.Text = arguments.Length == 0 ? path : $"{path} {arguments}";
        box.CaretIndex = box.Text.Length;
        _ = box.Focus();
    }
}
