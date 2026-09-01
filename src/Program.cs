namespace MonitorLayoutSwitcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length > 0 && args[0].Equals("--test-identity", StringComparison.OrdinalIgnoreCase))
        {
            bool pass = true;

            // Same model, different physical port/instance (UID suffix differs) -> must NOT match.
            const string legacyA = @"MONITOR\DEL4090\5&26957f3d&0&UID4352_0";
            const string legacyB = @"MONITOR\DEL4090\5&26957f3d&0&UID4353_0";
            pass &= Check("Legacy MONITOR ids with different UID are distinct",
                !DisplayEngine.SameHardwareIdentity(legacyA, legacyB));

            // Same string compared to itself -> must match.
            pass &= Check("Legacy MONITOR id matches itself",
                DisplayEngine.SameHardwareIdentity(legacyA, legacyA));

            // Modern DISPLAYCONFIG device path form, different UID, same trailing GUID class -> must NOT match.
            const string modernA = @"\\?\DISPLAY#DEL4090#5&26957f3d&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
            const string modernB = @"\\?\DISPLAY#DEL4090#5&26957f3d&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
            pass &= Check("Modern DISPLAY# ids with different UID are distinct",
                !DisplayEngine.SameHardwareIdentity(modernA, modernB));

            pass &= Check("Modern DISPLAY# id matches itself",
                DisplayEngine.SameHardwareIdentity(modernA, modernA));

            // Same physical monitor described once in legacy form and once in modern form -> must match.
            pass &= Check("Legacy and modern ids for the same monitor+port match",
                DisplayEngine.SameHardwareIdentity(legacyA, modernA));

            // "PnP device instance id" style legacy ids (no UID token at all — what
            // EnumDisplayDevices actually returns on many real systems): same model,
            // different trailing instance number -> must NOT match. This is the exact
            // shape that originally collapsed onto a single "identity" for every
            // same-model monitor.
            const string instanceIdA = @"MONITOR\DELF13D\{4d36e96e-e325-11ce-bfc1-08002be10318}\0000";
            const string instanceIdB = @"MONITOR\DELF13D\{4d36e96e-e325-11ce-bfc1-08002be10318}\0002";
            pass &= Check("PnP-instance-id legacy ids with different instance numbers are distinct",
                !DisplayEngine.SameHardwareIdentity(instanceIdA, instanceIdB));

            pass &= Check("PnP-instance-id legacy id matches itself",
                DisplayEngine.SameHardwareIdentity(instanceIdA, instanceIdA));

            Console.WriteLine(pass ? "Identity self-test PASSED." : "Identity self-test FAILED.");
            Environment.ExitCode = pass ? 0 : 1;
            return;

            static bool Check(string name, bool condition)
            {
                Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
                return condition;
            }
        }

        if (args.Length > 0 && args[0].Equals("--trace-apply", StringComparison.OrdinalIgnoreCase))
        {
            var profiles = ProfileManager.LoadProfiles();
            string profileName = args.Length > 1 ? args[1] : profiles[0].Name;
            var target = profiles.FirstOrDefault(p => p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase)) ?? profiles[0];
            DisplayEngine.TraceApplyResolution(target);
            return;
        }

        if (args.Length > 0 && args[0].Equals("--test-primary", StringComparison.OrdinalIgnoreCase))
        {
            string deviceName = int.TryParse(args.Length > 1 ? args[1] : "1", out int n) ? $@"\\.\DISPLAY{n}" : args[1];
            DisplayEngine.TestSetPrimaryMinimal(deviceName);
            return;
        }

        if (args.Length > 0 && args[0].Equals("--test-reposition", StringComparison.OrdinalIgnoreCase))
        {
            string deviceName = int.TryParse(args.Length > 1 ? args[1] : "1", out int n) ? $@"\\.\DISPLAY{n}" : args[1];
            int x = args.Length > 2 ? int.Parse(args[2]) : 0;
            int y = args.Length > 3 ? int.Parse(args[3]) : 0;
            DisplayEngine.TestReposition(deviceName, x, y);
            return;
        }

        if (args.Length > 0 && args[0].Equals("--list-modes", StringComparison.OrdinalIgnoreCase))
        {
            string arg = args.Length > 1 ? args[1] : "1";
            string deviceName = int.TryParse(arg, out int n) ? $@"\\.\DISPLAY{n}" : arg;
            DisplayEngine.ListModes(deviceName);
            return;
        }

        if (args.Length > 0 && args[0].Equals("--test-disable", StringComparison.OrdinalIgnoreCase))
        {
            string arg = args.Length > 1 ? args[1] : "1";
            string deviceName = int.TryParse(arg, out int n) ? $@"\\.\DISPLAY{n}" : arg;
            DisplayEngine.TestDisableIsolated(deviceName);
            return;
        }

        if (args.Length > 0 && args[0].Equals("--apply", StringComparison.OrdinalIgnoreCase))
        {
            // Real, mutating apply — calls the exact same code path the UI's Apply
            // button uses (LayoutSafety.Apply), for reproducing bugs from the CLI
            // without needing to drive the GUI.
            var profiles = ProfileManager.LoadProfiles();
            string profileName = args.Length > 1 ? args[1] : profiles[0].Name;
            var target = profiles.FirstOrDefault(p => p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase)) ?? profiles[0];
            Console.WriteLine($"Applying profile: '{target.Name}'");
            bool ok = LayoutSafety.Apply(target, out string msg);
            Console.WriteLine(ok ? $"SUCCESS: {msg}" : $"FAILED: {msg}");
            return;
        }

        if (args.Length > 0 && args[0].Equals("--test-apply", StringComparison.OrdinalIgnoreCase))
        {
            var profiles = ProfileManager.LoadProfiles();
            string profileName = args.Length > 1 ? args[1] : profiles[0].Name;
            var target = profiles.FirstOrDefault(p => p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase)) ?? profiles[0];
            Console.WriteLine($"Validating profile: '{target.Name}' with {target.Displays.Count} displays ({target.Displays.Count(d => d.Enabled)} enabled)");
            bool ok = DisplayEngine.ValidateProfile(target, out string err);
            Console.WriteLine(ok ? "Validation SUCCESS (no display changes made)." : $"Validation FAILED: {err}");
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
