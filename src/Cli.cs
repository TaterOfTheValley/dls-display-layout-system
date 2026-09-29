namespace DLS;

/// <summary>
/// The command line: <c>DLS.exe apply "Main monitor only"</c>, <c>DLS.exe schedule add ...</c>.
///
/// These are plain verbs with real exit codes, meant for scripts and for Task
/// Scheduler. The older <c>--apply</c>, <c>--capture</c> and friends in Program.cs are
/// diagnostics and are left exactly as they were; where the two overlap, the verbs
/// here are the stricter ones (an unknown layout name is an error, not "apply the
/// first one instead").
///
/// Exit codes: 0 done, 1 failed, 2 bad usage, 3 no such layout.
/// </summary>
internal static class Cli
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;
    public const int NoSuchLayout = 3;

    private static readonly string[] Verbs = { "list", "apply", "schedule", "help", "version" };

    /// <summary>True when the arguments are one of the verbs above rather than a
    /// legacy <c>--flag</c> diagnostic or a plain launch.</summary>
    public static bool Handles(string[] args) =>
        args.Length > 0 &&
        (Verbs.Contains(args[0], StringComparer.OrdinalIgnoreCase) ||
         args[0] is "--help" or "-h" or "-?" or "/?" or "--version");

    /// <summary>
    /// <c>--quiet</c> means no console window at all. A scheduled task has no terminal
    /// to attach to, and opening one would flash a window at the user every time it
    /// fires — the result goes to the log instead.
    /// </summary>
    public static bool WantsQuiet(string[] args) => HasFlag(args, "--quiet");

    public static int Run(string[] args)
    {
        var (positional, flags) = Split(args);
        if (positional.Count == 0) return Help();

        var options = Options(args);
        return positional[0].ToLowerInvariant() switch
        {
            "list" => List(),
            "apply" => Apply(positional, flags),
            "schedule" => ScheduleCommand(positional, options),
            "version" or "--version" => Version(),
            _ => Help()
        };
    }

    // ---- commands ---------------------------------------------------------

    private static int Version()
    {
        Console.WriteLine($"{AppInfo.Branded} {AppInfo.Version}");
        return Ok;
    }

    private static int Help()
    {
        Console.WriteLine($"{AppInfo.Branded} {AppInfo.Version}");
        Console.WriteLine();
        Console.WriteLine("  DLS.exe list                         Saved layouts, and which one is active");
        Console.WriteLine("  DLS.exe apply \"<layout>\"             Switch to a saved layout (no prompt)");
        Console.WriteLine("  DLS.exe schedule add \"<layout>\" --at <time> [--days <days>] [--name <task>]");
        Console.WriteLine("                                       Switch to a layout on a schedule (Task Scheduler)");
        Console.WriteLine("  DLS.exe schedule list                Scheduled switches");
        Console.WriteLine("  DLS.exe schedule remove \"<task>\"     Remove a scheduled switch");
        Console.WriteLine("  DLS.exe version");
        Console.WriteLine();
        Console.WriteLine("Options");
        Console.WriteLine("  --quiet      No console window. For scheduled runs; use with --log.");
        Console.WriteLine("  --log        Also append the result to " + Path.Combine(AppPaths.SettingsDirectory, RunLog.FileName));
        Console.WriteLine("  --out <file> Also write the output to a file");
        Console.WriteLine();
        Console.WriteLine("  --at    24-hour (22:30) or 12-hour (10:30pm)");
        Console.WriteLine("  --days  daily (default), weekdays, weekends, or a list like mon,wed,fri");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0 done, 1 failed, 2 bad usage, 3 no such layout.");
        Console.WriteLine();
        Console.WriteLine("This is a Windows GUI program, so a terminal does not wait for it. To wait and read");
        Console.WriteLine("the exit code:  Start-Process -Wait -PassThru DLS.exe -ArgumentList 'apply','name'");
        return Ok;
    }

    private static int List()
    {
        var layouts = ProfileManager.LoadProfiles();
        if (layouts.Count == 0)
        {
            Console.WriteLine("No layouts saved yet.");
            return Ok;
        }

        var active = DisplayEngine.FindMatchingProfile(layouts);
        int width = layouts.Max(p => p.Name.Length);
        foreach (var p in layouts)
        {
            int on = p.Displays.Count(d => d.Enabled);
            string note = p.NeedsRecapture ? "  (needs re-capture)" : string.Empty;
            Console.WriteLine($"{(ReferenceEquals(p, active) ? "*" : " ")} {p.Name.PadRight(width)}  " +
                              $"{on} of {p.Displays.Count} monitor(s) on{note}");
        }
        return Ok;
    }

    private static int Apply(List<string> positional, HashSet<string> flags)
    {
        if (positional.Count != 2)
        {
            Console.WriteLine("Usage: DLS.exe apply \"<layout>\"");
            return Usage;
        }

        string name = positional[1];
        var log = new RunLog(flags.Contains("--log"));

        var layouts = ProfileManager.LoadProfiles();
        var layout = FindLayout(layouts, name);
        if (layout == null)
        {
            // Never guess. The legacy --apply falls back to the first layout, which is
            // fine at a keyboard and wrong for an unattended job: a renamed layout
            // would switch the screens to something else entirely.
            log.Say($"apply \"{name}\": no layout with that name. " +
                    (layouts.Count == 0 ? "None are saved." : $"Saved: {string.Join(", ", layouts.Select(p => $"\"{p.Name}\""))}."));
            return NoSuchLayout;
        }

        // Already there is success: a schedule that fires when the layout is already
        // in place should not flip the screens off and on again to prove a point.
        if (DisplayEngine.MatchesCurrent(layout))
        {
            log.Say($"apply \"{layout.Name}\": already active.");
            return Ok;
        }

        // Non-interactive: no keep-or-revert prompt, nothing to click. That is the
        // point of running it from a schedule. The layout is one you saved yourself.
        bool ok = LayoutSafety.Apply(layout, out string message);
        log.Say($"apply \"{layout.Name}\": {(ok ? message : "FAILED. " + message)}");
        return ok ? Ok : Failed;
    }

    private static int ScheduleCommand(List<string> positional, Dictionary<string, string> options)
    {
        string? sub = positional.Count > 1 ? positional[1].ToLowerInvariant() : null;

        switch (sub)
        {
            case "add":
                if (positional.Count != 3)
                {
                    Console.WriteLine("Usage: DLS.exe schedule add \"<layout>\" --at <time> [--days <days>] [--name <task>]");
                    return Usage;
                }
                return ScheduleAdd(positional[2], options);

            case "list":
                return ScheduleList();

            case "remove":
                if (positional.Count != 3)
                {
                    Console.WriteLine("Usage: DLS.exe schedule remove \"<task>\"");
                    return Usage;
                }
                return ScheduleRemove(positional[2]);

            default:
                Console.WriteLine("Usage: DLS.exe schedule add|list|remove   (DLS.exe help for details)");
                return Usage;
        }
    }

    private static int ScheduleAdd(string layoutName, Dictionary<string, string> options)
    {
        if (!options.TryGetValue("--at", out string? at) || !ScheduleService.TryParseTime(at, out TimeSpan time))
        {
            Console.WriteLine(at == null
                ? "Missing --at. Example: --at 22:30"
                : $"Could not read '{at}' as a time. Try 22:30 or 10:30pm.");
            return Usage;
        }

        string daysText = options.GetValueOrDefault("--days", "daily");
        if (!ScheduleService.TryParseDays(daysText, out List<DayOfWeek>? days))
        {
            Console.WriteLine($"Could not read '{daysText}' as days. Use daily, weekdays, weekends, or e.g. mon,wed,fri.");
            return Usage;
        }

        // Checked now rather than at 22:30: a typo should fail while you are looking.
        var layout = FindLayout(ProfileManager.LoadProfiles(), layoutName);
        if (layout == null)
        {
            Console.WriteLine($"No saved layout named \"{layoutName}\". Run: DLS.exe list");
            return NoSuchLayout;
        }
        if (layout.Name.Contains('"'))
        {
            Console.WriteLine("Layouts with a double quote in the name cannot be scheduled. Rename it first.");
            return Failed;
        }

        string taskName = options.TryGetValue("--name", out string? given)
            ? given
            : $"{layout.Name} at {time:hh\\:mm}";

        if (!ScheduleService.Create(taskName, layout.Name, time, days, out string result))
        {
            Console.WriteLine($"Could not create the scheduled task: {result}");
            return Failed;
        }

        Console.WriteLine($"Scheduled \"{ScheduleService.Sanitise(taskName)}\": switch to \"{layout.Name}\" " +
                          $"{ScheduleService.Describe(days, time)}.");
        Console.WriteLine("It runs only if the computer is on and you are signed in at that time; a missed run is skipped.");
        Console.WriteLine("It is matched by layout name, so renaming the layout means updating the schedule.");
        return Ok;
    }

    private static int ScheduleList()
    {
        var tasks = ScheduleService.List(out string error);
        if (error.Length > 0)
        {
            Console.WriteLine(error);
            return Failed;
        }
        if (tasks.Count == 0)
        {
            Console.WriteLine("No scheduled switches. Add one: DLS.exe schedule add \"<layout>\" --at 22:30");
            return Ok;
        }

        foreach (var t in tasks)
        {
            Console.WriteLine(t.Name);
            Console.WriteLine($"    layout:  {t.Layout}");
            Console.WriteLine($"    when:    {t.When}");
            Console.WriteLine($"    next:    {t.NextRun}   ({t.Status})");
        }
        return Ok;
    }

    private static int ScheduleRemove(string taskName)
    {
        if (!ScheduleService.Remove(taskName, out string result))
        {
            Console.WriteLine($"Could not remove \"{taskName}\": {result}");
            return Failed;
        }
        Console.WriteLine($"Removed \"{ScheduleService.Sanitise(taskName)}\".");
        return Ok;
    }

    // ---- helpers ----------------------------------------------------------

    private static DisplayProfile? FindLayout(List<DisplayProfile> layouts, string name) =>
        layouts.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
        layouts.FirstOrDefault(p => p.Id.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Options that take a value, as {"--at": "22:30"}. Anything not in this
    /// list is a bare flag or a positional argument.</summary>
    private static readonly string[] ValueOptions = { "--at", "--days", "--name", "--out" };

    private static Dictionary<string, string> Options(string[] args)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (ValueOptions.Contains(args[i], StringComparer.OrdinalIgnoreCase)) found[args[i]] = args[i + 1];
        }
        return found;
    }

    /// <summary>Separates positional words (verb, layout name) from options, so
    /// option order does not matter and a value is never mistaken for a layout name.</summary>
    private static (List<string> Positional, HashSet<string> Flags) Split(string[] args)
    {
        var positional = new List<string>();
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (ValueOptions.Contains(a, StringComparer.OrdinalIgnoreCase)) { i++; continue; }
            if (a.Equals("--quiet", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--log", StringComparison.OrdinalIgnoreCase)) { flags.Add(a); continue; }
            positional.Add(a);
        }
        return (positional, flags);
    }
}
