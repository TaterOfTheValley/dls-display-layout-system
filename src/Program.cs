using System.Runtime.InteropServices;
using Velopack;

namespace DLS;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    private const int AttachParentProcess = -1;

    /// <summary>
    /// This is a WinExe, so it starts with no console and every Console.Write from a
    /// CLI diagnostic is silently discarded. Attach to the launching terminal (or
    /// open one) and rebind stdout before any diagnostic runs. Also routes a copy to
    /// a file when the caller passes one, which is the reliable way to capture output
    /// from a GUI-subsystem process.
    /// </summary>
    private static void StartConsole(string? teeFile = null)
    {
        if (!AttachConsole(AttachParentProcess)) AllocConsole();

        var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        if (!string.IsNullOrWhiteSpace(teeFile))
        {
            var file = new StreamWriter(teeFile, append: false) { AutoFlush = true };
            Console.SetOut(new TeeWriter(writer, file));
        }
        else
        {
            Console.SetOut(writer);
        }
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _a, _b;
        public TeeWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
        public override System.Text.Encoding Encoding => _a.Encoding;
        public override void Write(char value) { _a.Write(value); _b.Write(value); }
        public override void Write(string? value) { _a.Write(value); _b.Write(value); }
        public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
        public override void Flush() { _a.Flush(); _b.Flush(); }
    }

    /// <summary>
    /// Runs a diagnostic with its exceptions printed rather than thrown. An unhandled
    /// exception in a WinExe raises a modal Windows Error Reporting dialog, which
    /// hangs a non-interactive run forever instead of failing.
    /// </summary>
    private static void RunDiagnostic(string[] args, Action body)
    {
        string? tee = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--out", StringComparison.OrdinalIgnoreCase)) tee = args[i + 1];
        }

        StartConsole(tee);
        try
        {
            body();
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Environment.ExitCode = 1;
        }
        finally
        {
            Console.Out.Flush();
        }
    }

    /// <summary>
    /// Pulls <c>--text-scale &lt;percent&gt;</c> out of the arguments, wherever it is,
    /// and applies it in place of Windows' Text size setting. Combined with the
    /// --screenshot switches, it is how each text size gets checked without changing
    /// the setting for everything else on the machine.
    /// </summary>
    private static string[] TakeTextScaleOverride(string[] args)
    {
        int at = Array.FindIndex(args, a => a.Equals("--text-scale", StringComparison.OrdinalIgnoreCase));
        if (at < 0) return args;

        if (at + 1 < args.Length && int.TryParse(args[at + 1], out int percent))
        {
            UiScaling.TextScaleOverride = percent / 100f;
            return args.Where((_, i) => i != at && i != at + 1).ToArray();
        }
        return args.Where((_, i) => i != at).ToArray();
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // The installer invokes this executable for fast install/update hooks. Handle
        // them before normal startup, which touches settings and display devices.
        VelopackApp.Build()
            // A downloaded package waits for the user's explicit Install choice.
            .SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => StartupRegistration.RemoveForUninstall())
            .Run();

        // Every path below reads or writes settings, so resolve their location first.
        AppPaths.Initialise();

        args = TakeTextScaleOverride(args);

        if (args.Length > 0 && args[0].Equals("--preflight", StringComparison.OrdinalIgnoreCase))
        {
            RunDiagnostic(args, () =>
            {
                Console.WriteLine($"{AppInfo.Branded} {AppInfo.Version}");
                Console.WriteLine();
                var results = Preflight.Run();
                Console.Write(Preflight.Format(results));
                Console.WriteLine();
                Console.WriteLine($"Start with Windows: {StartupRegistration.Current}");
                Environment.ExitCode = results.Any(c => !c.Passed && c.Fatal) ? 1 : 0;
            });
            return;
        }

        if (args.Length > 0 && args[0].Equals("--dump-config", StringComparison.OrdinalIgnoreCase))
        {
            // Non-destructive: prints the live CCD topology, including monitors that
            // are connected but currently disabled. This is the first thing to run
            // when a layout misbehaves.
            RunDiagnostic(args, () =>
            {
                CcdNative.AssertLayout();
                Console.WriteLine("CCD interop struct layout: OK");
                Console.WriteLine();
                Console.Write(DisplayEngine.DumpConfiguration());
            });
            return;
        }

        if (args.Length > 0 && args[0].Equals("--set-scale", StringComparison.OrdinalIgnoreCase))
        {
            RunDiagnostic(args, () =>
            {
                int percent = args.Length > 1 && int.TryParse(args[1], out int p) ? p : 100;
                Console.WriteLine(CcdEngine.TestScale(percent));
            });
            return;
        }

        if (args.Length > 0 && args[0].Equals("--set-refresh", StringComparison.OrdinalIgnoreCase))
        {
            // Captures the live layout, changes the primary's refresh rate the same way
            // the editor does, and applies it — so this exercises the real apply path.
            RunDiagnostic(args, () =>
            {
                int hz = args.Length > 1 && int.TryParse(args[1], out int h) ? h : 60;
                var profile = DisplayEngine.CaptureCurrentLayoutAsProfile("refresh test", string.Empty);
                var primary = profile.Displays.FirstOrDefault(d => d.IsPrimary && d.Enabled)
                              ?? profile.Displays.FirstOrDefault(d => d.Enabled);
                if (primary == null) { Console.WriteLine("No enabled display."); return; }

                Console.WriteLine($"{primary.MonitorId}: {primary.Width}x{primary.Height} @ {primary.RefreshRate}Hz -> requesting {hz}Hz");
                primary.HasTargetMode = false;
                primary.RefreshRate = hz;
                primary.RefreshNumerator = (uint)hz;
                primary.RefreshDenominator = 1;

                bool ok = DisplayEngine.ApplyProfile(profile, out string err);
                Console.WriteLine(ok ? "apply: OK" : $"apply FAILED: {err}");

                var now = DisplayEngine.GetCurrentDisplays()
                    .FirstOrDefault(d => DisplayEngine.SameHardwareIdentity(d.MonitorDevicePath, primary.MonitorDevicePath));
                Console.WriteLine($"now: {now?.Width}x{now?.Height} @ {now?.RefreshRate}Hz");
            });
            return;
        }

        if (args.Length > 0 && args[0].Equals("--capture", StringComparison.OrdinalIgnoreCase))
        {
            RunDiagnostic(args, () =>
            {
            string name = args.Length > 1 ? args[1] : "Captured layout";
            var captured = DisplayEngine.CaptureCurrentLayoutAsProfile(name, string.Empty);
            var all = ProfileManager.LoadProfiles();
            all.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            all.Add(captured);
            Console.WriteLine(ProfileManager.TrySaveProfiles(all, out string saveErr)
                ? $"Captured '{name}' with {captured.Displays.Count} monitor(s), " +
                  $"{captured.Displays.Count(d => d.Enabled)} enabled."
                : $"FAILED to save: {saveErr}");
            });
            return;
        }

        if (args.Length > 0 && args[0].Equals("--list-profiles", StringComparison.OrdinalIgnoreCase))
        {
            RunDiagnostic(args, () =>
            {
            var saved = ProfileManager.LoadProfiles();
            if (saved.Count == 0) Console.WriteLine("No layouts saved yet.");
            foreach (var p in saved)
            {
                Console.WriteLine($"{p.Name}{(p.NeedsRecapture ? "  (NEEDS RE-CAPTURE)" : "")}");
                foreach (var d in p.Displays)
                {
                    Console.WriteLine($"    {(d.Enabled ? "ON " : "off")} {d.MonitorId,-24} " +
                                      $"{d.Width}x{d.Height} @ ({d.X},{d.Y}){(d.IsPrimary ? " PRIMARY" : "")}");
                    Console.WriteLine($"        {d.MonitorDevicePath}");
                }
            }
            });
            return;
        }

        if (args.Length > 0 && args[0].Equals("--apply", StringComparison.OrdinalIgnoreCase))
        {
            RunDiagnostic(args, () =>
            {
            // Real, mutating apply — calls the exact same code path the UI's Apply
            // button uses (LayoutSafety.Apply), for reproducing bugs from the CLI
            // without needing to drive the GUI.
            var profiles = ProfileManager.LoadProfiles();
            if (profiles.Count == 0)
            {
                Console.WriteLine("No layouts saved yet. Capture one first: --capture \"My layout\"");
                return;
            }
            string profileName = args.Length > 1 ? args[1] : profiles[0].Name;
            var target = profiles.FirstOrDefault(p => p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase)) ?? profiles[0];
            Console.WriteLine($"Applying profile: '{target.Name}'");
            bool ok = LayoutSafety.Apply(target, out string msg);
            Console.WriteLine(ok ? $"SUCCESS: {msg}" : $"FAILED: {msg}");
            });
            return;
        }

        if (args.Length > 0 && args[0].Equals("--test-apply", StringComparison.OrdinalIgnoreCase))
        {
            RunDiagnostic(args, () =>
            {
            var profiles = ProfileManager.LoadProfiles();
            if (profiles.Count == 0)
            {
                Console.WriteLine("No layouts saved yet. Capture one first: --capture \"My layout\"");
                return;
            }
            string profileName = args.Length > 1 ? args[1] : profiles[0].Name;
            var target = profiles.FirstOrDefault(p => p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase)) ?? profiles[0];
            Console.WriteLine($"Validating profile: '{target.Name}' with {target.Displays.Count} displays ({target.Displays.Count(d => d.Enabled)} enabled)");
            // Whether the desktop already matches is what the editor's Apply button
            // gates on, so it belongs in the same report — as does the breakdown
            // behind it, which is what the editor's footer counts.
            var pending = DisplayEngine.Compare(target, DisplayEngine.GetCurrentDisplays());
            Console.WriteLine($"Already active (MatchesCurrent): {pending.Count == 0}");
            foreach (var difference in pending)
            {
                Console.WriteLine($"    pending: {difference}");
            }
            foreach (var d in target.Displays.Where(d => d.Enabled))
            {
                Console.WriteLine($"    wants {d.MonitorId}: {d.Width}x{d.Height} @ {d.RefreshRate}Hz" +
                                  $"{(d.ScalePercent > 0 ? $" · {d.ScalePercent}%" : "")}");
            }
            bool ok = DisplayEngine.ValidateProfile(target, out string err);
            Console.WriteLine(ok ? "Validation SUCCESS (no display changes made)." : $"Validation FAILED: {err}");
            });
            return;
        }

        // Everything below needs WinForms; the diagnostics above deliberately do not,
        // so they stay runnable even when UI initialisation would block.
        ApplicationConfiguration.Initialize();

        if (args.Length > 0 && args[0].Equals("--screenshot-hud", StringComparison.OrdinalIgnoreCase))
        {
            var profiles = ProfileManager.LoadProfiles();
            var target = profiles.FirstOrDefault(p => p.Displays.Count > 0) ?? profiles.FirstOrDefault();
            var displays = target?.Displays ?? DisplayEngine.CaptureTargets();
            using var hud = KeepLayoutDialog.CreateForCapture(target?.Name ?? "Preview", displays, LayoutSafety.UndoSeconds);
            using var bmp = new Bitmap(hud.Width, hud.Height);
            using (var g = Graphics.FromImage(bmp)) hud.Render(g);
            string outPath = args.Length > 1 ? args[1] : "hud-preview.png";
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"HUD preview saved to {outPath} ({hud.Width}x{hud.Height})");
            return;
        }

        if (args.Length > 0 && args[0].Equals("--screenshot-menu", StringComparison.OrdinalIgnoreCase))
        {
            // Renders the tray popup to a file. The popup is a window we draw
            // ourselves, so unlike a ContextMenuStrip it can be captured and checked.
            var profiles = ProfileManager.LoadProfiles();
            var entries = TrayContext.BuildPreviewEntries(profiles);
            using var popup = TrayPopup.CreateForCapture(entries, 1f);
            using var bmp = new Bitmap(popup.Width, popup.Height);
            using (var g = Graphics.FromImage(bmp)) popup.Render(g);
            string outPath = args.Length > 1 ? args[1] : "tray-menu-preview.png";
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"Tray menu preview saved to {outPath} ({popup.Width}x{popup.Height})");
            return;
        }

        if (args.Length > 0 && args[0].Equals("--screenshot-update", StringComparison.OrdinalIgnoreCase))
        {
            // Renders the update dialog for a made-up release. Nothing is downloaded:
            // the manager is never asked to do anything before the form is discarded.
            var asset = new VelopackAsset
            {
                PackageId = "DLS",
                Version = SemanticVersion.Parse("9.9.9"),
                NotesMarkdown = "## What's new\n\n- Text follows Windows' Text size setting.\n- The update dialog scales with your display."
            };
            var manager = new UpdateManager("https://example.invalid/releases");
            using var form = new UpdateForm(manager, new UpdateInfo(asset, false), _ => false);
            form.Show();
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            string outPath = args.Length > 1 ? args[1] : "update-preview.png";
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"Update dialog preview saved to {outPath} ({form.Width}x{form.Height})");
            return;
        }

        if (args.Length > 0 && args[0].Equals("--screenshot", StringComparison.OrdinalIgnoreCase))
        {
            var currentDisplays = DisplayEngine.GetCurrentDisplays();
            Console.WriteLine($"Found {currentDisplays.Count} current displays:");
            foreach (var d in currentDisplays)
            {
                Console.WriteLine($" - {d}");
            }

            var profiles = ProfileManager.LoadProfiles();
            using var form = new ConfigForm(profiles, () => { });
            form.Show();
            Console.WriteLine($"Form Size: {form.Size}, ClientSize: {form.ClientSize}, CurrentAutoScaleDimensions: {form.CurrentAutoScaleDimensions}, DeviceDpi: {form.DeviceDpi}");
            foreach (Control c in form.Controls)
            {
                Console.WriteLine($"TopControl: {c.Name} ({c.GetType().Name}) Loc={c.Location} Size={c.Size} Font={c.Font.Name} {c.Font.Size}pt");
                foreach (Control sub in c.Controls)
                {
                    Console.WriteLine($"   SubControl: {sub.Name} ({sub.GetType().Name} '{sub.Text}') Loc={sub.Location} Size={sub.Size} Font={sub.Font.Name} {sub.Font.Size}pt");
                }
            }
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            string outPath = args.Length > 1 ? args[1] : "config-form-preview.png";
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"Screenshot saved to {outPath} ({form.Width}x{form.Height})");
            return;
        }

        // Check if another instance is running
        using var mutex = new Mutex(true, AppInfo.SingleInstanceMutex, out bool isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show(
                $"{AppInfo.Branded} is already running in your system tray.",
                AppInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
            return;
        }

        // Settings location and any migration from an older install, before anything
        // tries to read a layout.
        AppPaths.Initialise();

        // Environment checks before anything touches the display configuration. A
        // fatal result stops here rather than failing confusingly later.
        var checks = Preflight.Run();
        bool canRun = Preflight.ReportProblems(checks);
        if (!canRun) return;

        if (Preflight.IsFirstRun)
        {
            // Starting with Windows is the default: a tray utility that only works
            // once you remember to launch it is a utility you stop using. It is a
            // ticked item in the tray menu, so it is visible and one click to undo.
            StartupRegistration.Set(true, out _);
            Preflight.MarkFirstRunComplete();
        }
        else
        {
            // Re-point a stale entry, or restore one that went missing, without ever
            // overriding a deliberate "off".
            StartupRegistration.Reconcile();
        }

        Application.Run(new TrayContext());
    }
}
