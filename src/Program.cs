using System.Runtime.InteropServices;

namespace MonitorLayoutSwitcher;

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

    [STAThread]
    private static void Main(string[] args)
    {
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
                Console.WriteLine($"{p.Name}  (schema v{p.SchemaVersion}" +
                                  $"{(p.NeedsRecapture ? ", NEEDS RE-CAPTURE" : "")})");
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
            bool ok = DisplayEngine.ValidateProfile(target, out string err);
            Console.WriteLine(ok ? "Validation SUCCESS (no display changes made)." : $"Validation FAILED: {err}");
            });
            return;
        }

        // Everything below needs WinForms; the diagnostics above deliberately do not,
        // so they stay runnable even when UI initialisation would block.
        ApplicationConfiguration.Initialize();

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
        using var mutex = new Mutex(true, "MonitorLayoutSwitcher_SingleInstance_Mutex", out bool isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show(
                "Monitor Layout Switcher is already running in your system tray.",
                "Monitor Layout Switcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
            return;
        }

        Application.Run(new TrayContext());
    }
}
