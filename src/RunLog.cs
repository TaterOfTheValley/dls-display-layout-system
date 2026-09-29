using System.Text;

namespace DLS;

/// <summary>
/// One line per command run, written to the console and optionally to a log file.
///
/// A scheduled switch runs with no terminal anyone is watching, so the log is the only
/// way to learn afterwards that the 22:30 switch happened — or why it did not.
/// </summary>
internal sealed class RunLog
{
    public const string FileName = "dls.log";

    /// <summary>Past this the oldest lines are dropped. A line a day for years is
    /// still small; this only guards against something running in a loop.</summary>
    private const int MaxBytes = 128 * 1024;
    private const int KeepLines = 200;

    private readonly bool _toFile;

    public RunLog(bool toFile) => _toFile = toFile;

    public static string LogPath => Path.Combine(AppPaths.SettingsDirectory, FileName);

    public void Say(string message)
    {
        Console.WriteLine(message);
        if (!_toFile) return;

        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            Trim();
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // Logging must never turn a successful switch into a failed one.
        }
    }

    private static void Trim()
    {
        var info = new FileInfo(LogPath);
        if (!info.Exists || info.Length <= MaxBytes) return;

        var lines = File.ReadAllLines(LogPath);
        File.WriteAllLines(LogPath, lines.Skip(Math.Max(0, lines.Length - KeepLines)), Encoding.UTF8);
    }
}
