namespace MonitorLayoutSwitcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length > 0 && args[0].Equals("--screenshot", StringComparison.OrdinalIgnoreCase))
        {
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
