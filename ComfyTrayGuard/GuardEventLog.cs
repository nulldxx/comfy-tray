using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;

namespace ComfyTray;

/// <summary>
/// Where the guard says what it did.
///
/// <para>
/// A service that silently creates and deletes firewall rules is a support nightmare, so
/// everything that touches the machine's firewall is recorded twice: to the Windows event log,
/// where an administrator would look, and to a file under <c>%ProgramData%</c>, which survives
/// and can be pasted into an issue. Neither is allowed to fail loudly — losing a log line must
/// never take down the service that was writing it.
/// </para>
/// </summary>
internal sealed class GuardEventLog
{
    private const string Source = "ComfyTrayGuard";
    private const string LogName = "Application";
    private const long MaxFileBytes = 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly bool _echoToConsole;

    public GuardEventLog(bool echoToConsole)
    {
        _echoToConsole = echoToConsole;

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ComfyTray");
        _filePath = Path.Combine(directory, "guard.log");

        TryCreateDirectory(directory);
    }

    public void Info(string message) => Write(EventLogEntryType.Information, message);

    public void Warning(string message) => Write(EventLogEntryType.Warning, message);

    public void Error(string message) => Write(EventLogEntryType.Error, message);

    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Logging is best-effort; a failure to record must never propagate to the caller.")]
    private void Write(EventLogEntryType level, string message)
    {
        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-ddTHH:mm:ssZ} [{1}] {2}",
            DateTimeOffset.UtcNow,
            level,
            message);

        lock (_gate)
        {
            if (_echoToConsole)
            {
                Console.WriteLine(line);
            }

            try
            {
                RollIfLarge();
                File.AppendAllText(_filePath, line + Environment.NewLine);
            }
            catch (Exception)
            {
                // The event log below is the other half of this; losing one is survivable.
            }

            // Only warnings and errors go to the event log. Every blocked executable would
            // otherwise flood an administrator's Application log during a busy ComfyUI session.
            if (level == EventLogEntryType.Information)
            {
                return;
            }

            try
            {
                // The installer registers the source, so this never needs the administrative
                // registry write that creating one at runtime would.
                EventLog.WriteEntry(Source, message, level);
            }
            catch (Exception)
            {
                // Source not registered (a development run, say). The file has it.
            }
        }
    }

    private void RollIfLarge()
    {
        var file = new FileInfo(_filePath);
        if (!file.Exists || file.Length < MaxFileBytes)
        {
            return;
        }

        var previous = _filePath + ".1";
        File.Delete(previous);
        File.Move(_filePath, previous);
    }

    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Falls back to event-log-only logging when the directory cannot be made.")]
    private static void TryCreateDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            // File logging will fail too, and is handled there.
        }
    }
}
