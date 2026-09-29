using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace DLS;

/// <summary>
/// Scheduled layout switches, as Windows Task Scheduler tasks.
///
/// Task Scheduler is the built-in cron: it survives reboots, needs no service of ours
/// running, and shows up in a tool people already know. Each schedule is one task under
/// the "DLS" folder that runs <c>DLS.exe apply "&lt;layout&gt;" --quiet --log</c>.
///
/// Tasks are created from XML rather than with schtasks' own flags, because the flags
/// produce defaults that are wrong here — most importantly "don't start on battery",
/// which would silently skip every run on a laptop.
///
/// The semantics chosen, deliberately:
///  - Runs only if the computer is on at that moment (<c>StartWhenAvailable</c> off).
///    A screen switch hours late is worse than none.
///  - Runs as the signed-in user, interactively. Display configuration belongs to the
///    user's session; a background service account cannot change it.
///  - Never elevated. Changing the display layout does not need it.
/// </summary>
internal static class ScheduleService
{
    private const string Folder = "DLS";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>Characters Task Scheduler forbids in a task name.</summary>
    private static readonly char[] Forbidden = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    public sealed record ScheduledSwitch(string Name, string Layout, string When, string NextRun, string Status);

    public static string Sanitise(string name)
    {
        var clean = new string(name.Select(c => Forbidden.Contains(c) ? '-' : c).ToArray()).Trim();
        return clean.Length == 0 ? "Layout switch" : clean;
    }

    private static string FullName(string name) => $"{Folder}\\{Sanitise(name)}";

    // ---- parsing ----------------------------------------------------------

    public static bool TryParseTime(string text, out TimeSpan time)
    {
        string[] formats = { "H:mm", "HH:mm", "h:mmtt", "h:mm tt", "htt", "h tt" };
        if (DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture,
                                   DateTimeStyles.AllowWhiteSpaces, out DateTime parsed))
        {
            time = parsed.TimeOfDay;
            return true;
        }
        time = default;
        return false;
    }

    /// <summary>Null list means every day.</summary>
    public static bool TryParseDays(string text, out List<DayOfWeek>? days)
    {
        days = null;
        text = text.Trim().ToLowerInvariant();
        if (text is "daily" or "everyday" or "every day" or "") return true;

        if (text == "weekdays")
        {
            days = new() { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
            return true;
        }
        if (text == "weekends")
        {
            days = new() { DayOfWeek.Saturday, DayOfWeek.Sunday };
            return true;
        }

        var picked = new List<DayOfWeek>();
        foreach (string part in text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Enum.GetValues<DayOfWeek>().Cast<DayOfWeek?>()
                .FirstOrDefault(d => d.ToString()!.StartsWith(part.Length >= 3 ? part : "\0", StringComparison.OrdinalIgnoreCase));
            if (match == null) return false;
            if (!picked.Contains(match.Value)) picked.Add(match.Value);
        }
        if (picked.Count == 0) return false;

        days = picked;
        return true;
    }

    public static string Describe(List<DayOfWeek>? days, TimeSpan time)
    {
        string at = DateTime.Today.Add(time).ToString("h:mm tt", CultureInfo.InvariantCulture);
        if (days == null) return $"every day at {at}";
        return $"on {string.Join(", ", days.OrderBy(d => ((int)d + 6) % 7).Select(d => d.ToString()[..3]))} at {at}";
    }

    // ---- create / remove / list ------------------------------------------

    public static bool Create(string taskName, string layoutName, TimeSpan time, List<DayOfWeek>? days, out string error)
    {
        string xmlPath = Path.Combine(Path.GetTempPath(), $"dls-task-{Guid.NewGuid():N}.xml");
        try
        {
            // Task Scheduler wants UTF-16 for /XML input.
            File.WriteAllText(xmlPath, BuildXml(layoutName, time, days).ToString(), Encoding.Unicode);

            // /F replaces a task of the same name, so re-running "add" edits it.
            var run = RunSchtasks("/Create", "/TN", FullName(taskName), "/XML", xmlPath, "/F");
            error = run.ExitCode == 0 ? string.Empty : run.Output;
            return run.ExitCode == 0;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* temp file; harmless if it stays */ }
        }
    }

    public static bool Remove(string taskName, out string error)
    {
        var run = RunSchtasks("/Delete", "/TN", FullName(taskName), "/F");
        error = run.ExitCode == 0 ? string.Empty : run.Output;
        return run.ExitCode == 0;
    }

    public static List<ScheduledSwitch> List(out string error)
    {
        error = string.Empty;
        var result = new List<ScheduledSwitch>();

        var query = RunSchtasks("/Query", "/FO", "CSV", "/NH");
        if (query.ExitCode != 0)
        {
            error = query.Output;
            return result;
        }

        string prefix = "\\" + Folder + "\\";
        foreach (string line in query.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cols = SplitCsv(line.Trim());
            if (cols.Count < 3 || !cols[0].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            string name = cols[0][prefix.Length..];
            var (layout, when) = ReadDefinition(cols[0]);
            result.Add(new ScheduledSwitch(name, layout, when, cols[1], cols[2]));
        }
        return result;
    }

    // ---- internals --------------------------------------------------------

    private static XDocument BuildXml(string layoutName, TimeSpan time, List<DayOfWeek>? days)
    {
        // The start date is only where the recurrence begins; today is fine even when
        // the time has passed, the next occurrence is what counts.
        string start = DateTime.Today.Add(time).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

        XElement schedule = days == null
            ? new XElement(Ns + "ScheduleByDay", new XElement(Ns + "DaysInterval", 1))
            : new XElement(Ns + "ScheduleByWeek",
                new XElement(Ns + "DaysOfWeek", days.Select(d => new XElement(Ns + d.ToString()))),
                new XElement(Ns + "WeeksInterval", 1));

        string exe = Environment.ProcessPath ?? Application.ExecutablePath;
        string user = WindowsIdentity.GetCurrent().Name;

        return new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(Ns + "Task", new XAttribute("version", "1.4"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Author", AppInfo.Name),
                    new XElement(Ns + "Description", $"{AppInfo.Branded}: switch to the \"{layoutName}\" layout.")),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "CalendarTrigger",
                        new XElement(Ns + "StartBoundary", start),
                        new XElement(Ns + "Enabled", true),
                        schedule)),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", user),
                        new XElement(Ns + "LogonType", "InteractiveToken"),
                        new XElement(Ns + "RunLevel", "LeastPrivilege"))),
                new XElement(Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", false),
                    new XElement(Ns + "StopIfGoingOnBatteries", false),
                    new XElement(Ns + "StartWhenAvailable", false),
                    new XElement(Ns + "AllowHardTerminate", true),
                    new XElement(Ns + "AllowStartOnDemand", true),
                    new XElement(Ns + "Enabled", true),
                    new XElement(Ns + "ExecutionTimeLimit", "PT2M")),
                new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", exe),
                        new XElement(Ns + "Arguments", $"apply \"{layoutName}\" --quiet --log")))));
    }

    /// <summary>What a task does and when, read back from its own definition, so the
    /// listing reflects the task as Task Scheduler holds it — including edits made in
    /// its UI — rather than a copy of ours.</summary>
    private static (string Layout, string When) ReadDefinition(string fullTaskName)
    {
        var query = RunSchtasks("/Query", "/TN", fullTaskName, "/XML");
        if (query.ExitCode != 0) return ("?", "?");

        try
        {
            string text = query.Output;
            var doc = XDocument.Parse(text[text.IndexOf('<')..]);

            string args = doc.Descendants(Ns + "Arguments").FirstOrDefault()?.Value ?? string.Empty;
            string layout = "?";
            int open = args.IndexOf('"');
            int close = open >= 0 ? args.IndexOf('"', open + 1) : -1;
            if (close > open) layout = args[(open + 1)..close];

            var trigger = doc.Descendants(Ns + "CalendarTrigger").FirstOrDefault();
            if (trigger == null) return (layout, "on a custom trigger");

            DateTime.TryParse(trigger.Element(Ns + "StartBoundary")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime boundary);

            var week = trigger.Element(Ns + "ScheduleByWeek")?.Element(Ns + "DaysOfWeek");
            List<DayOfWeek>? days = week?.Elements()
                .Select(e => Enum.TryParse(e.Name.LocalName, out DayOfWeek d) ? (DayOfWeek?)d : null)
                .Where(d => d != null).Select(d => d!.Value).ToList();

            return (layout, Describe(days, boundary.TimeOfDay));
        }
        catch
        {
            return ("?", "?");
        }
    }

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }

    /// <summary>schtasks writes in the console's OEM code page, which .NET does not
    /// know until the code-page provider is registered.</summary>
    private static Encoding OemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    private static (int ExitCode, string Output) RunSchtasks(params string[] args)
    {
        var info = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = OemEncoding()
        };
        foreach (string a in args) info.ArgumentList.Add(a);

        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        string errors = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, (output + errors).Trim());
    }
}
