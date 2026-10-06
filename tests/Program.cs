using System.Reflection;
using DLS;

internal static class Program
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static object? Invoke(object owner, string name, params object[] args) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);

    [STAThread]
    private static void Main(string[] args)
    {
        var large = new Rectangle(0, 0, 3840, 2160);
        MonitorAlignment.Result Align(Rectangle small, bool snap = true, Rectangle[]? targets = null) =>
            MonitorAlignment.Calculate(small, targets ?? new[] { large }, 72, 24, snap);

        var above = Align(new Rectangle(975, -1080, 1920, 1080));
        Check(above.Position == new Point(960, -1080), "Unequal monitors should center above one another");
        Check(above.Guides.Any(g => g.Vertical && g.Center && g.Coordinate == 1920), "Guide must mark the shared center, not the left edge");
        var beside = Align(new Rectangle(3840, 552, 1920, 1080));
        Check(beside.Position == new Point(3840, 540), "Unequal monitors should center beside one another");
        Check(beside.Guides.Any(g => !g.Vertical && g.Center && g.Coordinate == 1080), "Horizontal center guide missing");
        Check(Align(new Rectangle(985, -1080, 1920, 1080)).Position.X == 985, "Center snap must release beyond its narrow threshold");
        Check(Align(new Rectangle(984, -1080, 1920, 1080)).Position.X == 960, "Center snap threshold boundary");
        Check(Align(new Rectangle(975, -1080, 1920, 1080), false).Position.X == 975, "Disabled snapping must preserve pointer position");
        Check(Align(new Rectangle(960, -1080, 1920, 1080), false).Guides.Any(g => g.Center), "Exact centers should be indicated even with snapping off");
        Check(Align(new Rectangle(30, -1080, 1920, 1080)).Position.X == 0, "Existing left-edge snapping regressed");
        var right = Align(new Rectangle(1940, -1080, 1920, 1080));
        Check(right.Position.X == 1920 && right.Guides.Any(g => g.Vertical && !g.Center && g.Coordinate == 3840), "Right-edge guide should follow the right edge");
        var negative = Align(new Rectangle(-2870, -2160, 1920, 1080), targets: new[] { new Rectangle(-3840, -1080, 3840, 2160) });
        Check(negative.Position.X == -2880, "Center snapping should handle negative desktop coordinates");
        var odd = Align(new Rectangle(959, -1080, 1920, 1080), targets: new[] { new Rectangle(0, 0, 3841, 2161) });
        Check(Math.Abs(odd.Position.X + 960 - 1920.5) <= 0.5, "Odd/even sizes should use the nearest possible center");
        Check(Align(new Rectangle(975, -1080, 1920, 1080), targets: Array.Empty<Rectangle>()).Guides.Count == 0, "A single monitor should have no alignment guides");
        var nearest = Align(new Rectangle(972, -1080, 1920, 1080), targets: new[] { large, new Rectangle(20, 0, 3840, 2160) });
        Check(nearest.Position.X == 980, "Nearest center should win across multiple monitors");

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        if (args.Length > 0)
        {
            var scaling = typeof(ConfigForm).Assembly.GetType("DLS.UiScaling")!;
            scaling.GetProperty("TextScaleOverride")!.SetValue(null, float.Parse(args[0]) / 100f);
        }
        var shared = new List<DisplayProfile> { new() { Name = "Test", Hotkey = "Ctrl + Alt + F8" } };
        using var form = new ConfigForm(shared, () => { });
        form.Show();
        var clear = Field<Button>(form, "_clearHotkeyBtn");
        Check(clear.Enabled, "Clear should be enabled for a saved hotkey");
        Invoke(form, "StartHotkeyCapture");
        clear.PerformClick();
        Check(Field<DisplayProfile>(form, "_selectedProfile").Hotkey == "", "Clear button should remove the draft hotkey");
        Check(form.HasUnsavedChanges && shared[0].Hotkey != "", "Clear must remain a draft until Save");
        Check(!Field<bool>(form, "_isCapturingHotkey") && !clear.Enabled, "Clear must end capture and disable itself");
        Invoke(form, "CancelEdits");
        Check(Field<DisplayProfile>(form, "_selectedProfile").Hotkey == shared[0].Hotkey && clear.Enabled, "Cancel should restore the shortcut and Clear button");
        clear.PerformClick();
        Invoke(form, "SetSelectedHotkey", "Ctrl + Alt + F9");
        Check(clear.Enabled, "Recording a replacement should re-enable Clear");
        clear.PerformClick();

        // Exercise the editor registration contract with actual Windows hotkeys.
        using (var manager = new HotkeyManager())
        using (var probe = new HotkeyManager())
        {
            string chord = "Ctrl + Alt + Shift + F11";
            var originals = new List<DisplayProfile>
            {
                new() { Name = "First", Hotkey = chord },
                new() { Name = "Second" }
            };
            using var editor = new ConfigForm(originals, () => { });
            editor.Show();
            void Refresh()
            {
                manager.UnregisterAll();
                if (editor.IsCapturingHotkey) return;
                foreach (var profile in originals)
                {
                    string shortcut = editor.ShortcutFor(profile.Id);
                    if (shortcut.Length > 0)
                        Check(manager.Register(shortcut, () => { }), "Draft shortcut should register");
                }
            }
            editor.ShortcutStateChanged += Refresh;
            Refresh();
            Check(!probe.Register(chord, () => { }), "Saved shortcut should be registered");
            Invoke(editor, "ClearSelectedHotkey");
            Check(probe.Register(chord, () => { }), "Clear should immediately release the Windows registration");
            probe.UnregisterAll();
            Invoke(editor, "CancelEdits");
            Check(!probe.Register(chord, () => { }), "Cancel should restore the Windows registration");
            var drafts = Field<List<DisplayProfile>>(editor, "_profiles");
            Invoke(editor, "SelectProfile", drafts[1]);
            Invoke(editor, "StartHotkeyCapture");
            Check(probe.Register(chord, () => { }), "Recording should release existing shortcuts so their keys reach the editor");
            probe.UnregisterAll();
            Invoke(editor, "HotkeyTextBox_KeyDown", editor, new KeyEventArgs(Keys.Control | Keys.Alt | Keys.Shift | Keys.F11));
            Check(drafts[0].Hotkey == "" && drafts[1].Hotkey == chord, "Recording an existing shortcut should transfer ownership");
            Check(!editor.IsCapturingHotkey && !probe.Register(chord, () => { }), "Completed capture should restore registration");
            Invoke(editor, "CancelEdits");
            editor.Close();
        }

        // Save only to a private temporary settings folder, never the user's layouts.
        var paths = typeof(ConfigForm).Assembly.GetType("DLS.AppPaths")!;
        var directory = paths.GetProperty("SettingsDirectory")!;
        string original = (string)directory.GetValue(null)!;
        string temporary = Path.Combine(Path.GetTempPath(), "DLS-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            directory.SetValue(null, temporary);
            Check((bool)Invoke(form, "SaveChanges")!, "Cleared shortcut should save successfully");
            Check(shared[0].Hotkey == "" && !form.HasUnsavedChanges, "Save should publish the cleared shortcut");
            Check(ProfileManager.LoadProfiles()[0].Hotkey == "", "Cleared shortcut should survive reload");
        }
        finally
        {
            directory.SetValue(null, original);
            foreach (var file in Directory.GetFiles(temporary)) File.Delete(file);
            Directory.Delete(temporary);
        }

        foreach (int width in new[] { 760, 960, 1160 })
        {
            form.Width = width;
            Application.DoEvents();
            var name = Field<TextBox>(form, "_nameTextBox");
            var hotkey = Field<TextBox>(form, "_hotkeyTextBox");
            Check(!name.Bounds.IntersectsWith(hotkey.Bounds), $"Name and shortcut overlap at width {width}");
            Check(!clear.Bounds.IntersectsWith(Field<Button>(form, "_captureCurrentLayoutBtn").Bounds), $"Clear overlaps layout actions at width {width}");
        }
        if (args.Length > 1)
        {
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
        }
        Console.WriteLine("Alignment, shortcut clear/cancel/save/reload, and editor layout checks passed.");
    }
}
