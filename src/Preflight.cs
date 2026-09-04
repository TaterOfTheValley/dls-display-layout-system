using System.Text;
using Microsoft.Win32;

namespace DLS;

/// <summary>
/// First-launch environment checks.
///
/// The .NET runtime itself is not checked here — if it is missing this code never
/// runs, and the .NET apphost already shows its own dialog with a download link.
/// What IS worth checking is everything the apphost cannot see: whether this build
/// of Windows exposes the display APIs the app is built on, whether any monitor is
/// visible through them, and whether the app can actually save a layout where it has
/// been put. That last one is the likeliest real-world failure — drop the exe in
/// Program Files and .dls is unwritable.
/// </summary>
internal static class Preflight
{
    internal sealed record Check(string Name, bool Passed, bool Fatal, string Detail);

    private const string SettingsKey = @"Software\DLS";
    private const string StartupInitialisedValue = "StartupInitialised";

    /// <summary>
    /// True the first time this user ever runs the app. Used to apply first-run
    /// defaults exactly once, so a later "no thanks" is never undone.
    /// </summary>
    public static bool IsFirstRun
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
                return key?.GetValue(StartupInitialisedValue) == null;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void MarkFirstRunComplete()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey, writable: true);
            key?.SetValue(StartupInitialisedValue, 1, RegistryValueKind.DWord);
        }
        catch
        {
            // If this cannot be written the defaults are simply applied again next
            // launch — annoying, never harmful.
        }
    }

    public static List<Check> Run()
    {
        var results = new List<Check>();

        // --- Windows version. CCD exists from Vista, but per-monitor DPI awareness
        // and the scaling API need 1607.
        var os = Environment.OSVersion.Version;
        bool modernEnough = os.Major > 10 || (os.Major == 10 && os.Build >= 14393);
        results.Add(new Check(
            "Windows version",
            modernEnough,
            Fatal: os.Major < 6,
            modernEnough
                ? $"Windows {os.Major}.{os.Minor} build {os.Build}"
                : $"Build {os.Build}. Windows 10 1607 (build 14393) or later is needed for per-monitor scaling."));

        // --- Struct layouts. A mismatch here corrupts every display call, so it is
        // worth failing loudly rather than misbehaving quietly.
        bool layoutOk = true;
        string layoutDetail = "Display API structures match this platform";
        try
        {
            CcdNative.AssertLayout();
        }
        catch (Exception ex)
        {
            layoutOk = false;
            layoutDetail = ex.Message;
        }
        results.Add(new Check("Display API compatibility", layoutOk, Fatal: true, layoutDetail));

        // --- Can we actually read the display configuration?
        var topology = layoutOk ? CcdEngine.Query(includeInactive: true) : new CcdTopology();
        int monitors = topology.Entries
            .Select(e => e.MonitorDevicePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        results.Add(new Check(
            "Display configuration",
            monitors > 0,
            Fatal: true,
            monitors > 0
                ? $"{monitors} monitor{(monitors == 1 ? "" : "s")} detected"
                : "Windows reported no monitors. A remote session or an unusual display driver can cause this."));

        // --- Where layouts get saved. Portable app, so this is wherever the exe sits.
        bool writable = TryWriteProbe(out string writeDetail);
        results.Add(new Check("Settings are writable", writable, Fatal: true, writeDetail));

        // --- Scaling is a nicety built on an undocumented call; degrade, never fail.
        bool scalingOk = topology.Entries.Any(e => e.IsActive && e.ScalePercent > 0);
        results.Add(new Check(
            "Display scaling control",
            scalingOk,
            Fatal: false,
            scalingOk
                ? "Per-monitor scaling can be read and set"
                : "Scaling could not be read on this system. Layouts will still switch; the scale option will do nothing."));

        return results;
    }

    private static bool TryWriteProbe(out string detail)
    {
        string dir = AppPaths.SettingsDirectory;
        string probe = Path.Combine(dir, ".dls-write-test");

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            detail = AppPaths.IsPortable ? $"{dir} (portable)" : dir;
            return true;
        }
        catch (Exception ex)
        {
            detail = $"Cannot write to {dir} — {ex.GetType().Name}. " +
                     "Move the app somewhere your account can write, such as a folder under your user profile.";
            return false;
        }
    }

    public static string Format(IEnumerable<Check> checks)
    {
        var sb = new StringBuilder();
        foreach (var c in checks)
        {
            string mark = c.Passed ? "OK  " : c.Fatal ? "FAIL" : "WARN";
            sb.AppendLine($"[{mark}] {c.Name}");
            sb.AppendLine($"       {c.Detail}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Shows a dialog only when something is actually wrong. A clean machine gets no
    /// interruption at all — a "everything is fine" splash on first launch is noise.
    /// </summary>
    public static bool ReportProblems(List<Check> checks)
    {
        var problems = checks.Where(c => !c.Passed).ToList();
        if (problems.Count == 0) return true;

        bool fatal = problems.Any(p => p.Fatal);
        var sb = new StringBuilder();
        sb.AppendLine(fatal
            ? $"{AppInfo.Name} cannot run properly on this system:"
            : $"{AppInfo.Name} started, but one feature is unavailable:");
        sb.AppendLine();

        foreach (var p in problems)
        {
            sb.AppendLine($"• {p.Name}");
            sb.AppendLine($"  {p.Detail}");
            sb.AppendLine();
        }

        MessageBox.Show(
            sb.ToString().TrimEnd(),
            $"{AppInfo.Branded}",
            MessageBoxButtons.OK,
            fatal ? MessageBoxIcon.Error : MessageBoxIcon.Warning);

        return !fatal;
    }
}
