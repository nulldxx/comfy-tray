using System;
using Microsoft.Win32;

namespace ComfyTray;

/// <summary>
/// The user's hook commands — one run immediately before ComfyUI starts, one immediately
/// after it stops. Unlike the launch configuration (which lives in <c>config.json</c>),
/// these are persisted per-user in the registry under
/// <c>HKCU\Software\ComfyTray</c>, base64-encoded so quoting and non-ASCII paths survive
/// the round trip. Edited from the tray's Configuration dialog.
/// </summary>
internal sealed class HookSettings
{
    public const string RegistryKeyPath = @"Software\ComfyTray";
    public const string BeforeStartValueName = "BeforeStartCommand";
    public const string AfterStopValueName = "AfterStopCommand";

    /// <summary>Command line run before the server process is launched. Empty means none.</summary>
    public string BeforeStartCommand { get; set; } = string.Empty;

    /// <summary>Command line run once the server process has stopped. Empty means none.</summary>
    public string AfterStopCommand { get; set; } = string.Empty;

    /// <summary>
    /// Reads the stored commands. A missing key, an unreadable one, or a value that isn't
    /// valid base64 all yield "no hook configured" rather than an error: a bad registry
    /// value must never stop the tray from running ComfyUI.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Hook settings are optional; any read failure falls back to none.")]
    public static HookSettings Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
            if (key == null)
            {
                return new HookSettings();
            }

            return new HookSettings
            {
                BeforeStartCommand = Base64Text.Decode(key.GetValue(BeforeStartValueName) as string),
                AfterStopCommand = Base64Text.Decode(key.GetValue(AfterStopValueName) as string),
            };
        }
        catch (Exception)
        {
            return new HookSettings();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Saving is best-effort; the error is returned to the caller for display.")]
    public bool TrySave(out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
            key.SetValue(BeforeStartValueName, Base64Text.Encode(BeforeStartCommand), RegistryValueKind.String);
            key.SetValue(AfterStopValueName, Base64Text.Encode(AfterStopCommand), RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
