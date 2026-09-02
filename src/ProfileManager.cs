using System.Reflection;
using System.Text.Json;

namespace MonitorLayoutSwitcher;

/// <summary>
/// The on-disk shape of the settings file.
///
/// Wrapping the layouts in an envelope rather than writing a bare array is what makes
/// the format upgradable: a version that lives beside the data can be read before the
/// data is interpreted, so a future build knows what it is looking at instead of
/// guessing from the contents.
/// </summary>
internal sealed class SettingsEnvelope
{
    /// <summary>
    /// MAJOR.MINOR, following semver's compatibility contract rather than a bare
    /// counter: MAJOR changes when existing fields change meaning or disappear, MINOR
    /// when fields are only added. That distinction is the whole point — it lets a
    /// build tell "newer but still readable" from "newer and not safe to touch",
    /// which a single incrementing number cannot express.
    ///
    /// PATCH is omitted deliberately: a file format has no bug-fix axis. A change
    /// either affects how the data reads or it does not.
    /// </summary>
    public string FormatVersion { get; set; } = ProfileManager.CurrentFormat.ToString(2);

    public string App { get; set; } = AppInfo.Name;

    /// <summary>Which build wrote this, for diagnostics. Compatibility decisions key
    /// off FormatVersion only — the app version and the format version move
    /// independently, which is exactly why they are separate fields.</summary>
    public string AppVersion { get; set; } = string.Empty;

    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
    public List<DisplayProfile> Layouts { get; set; } = new();
}

public static class ProfileManager
{
    /// <summary>
    /// The settings format this build writes.
    ///
    /// Bump MINOR when adding optional fields — older builds keep working. Bump MAJOR
    /// when an existing field changes meaning or goes away, and add a migration step.
    /// </summary>
    public static readonly Version CurrentFormat = new(1, 0);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Set when loading did something the user should know about — a
    /// migration, or a file from a newer build. Read once at startup.</summary>
    public static string? LastNotice { get; private set; }

    public static string ConfigPath => AppPaths.SettingsFile;

    public static List<DisplayProfile> LoadProfiles()
    {
        LastNotice = null;

        try
        {
            string path = AppPaths.SettingsFile;
            if (!File.Exists(path)) return new List<DisplayProfile>();

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new List<DisplayProfile>();

            var envelope = Parse(json, out Version found);
            if (envelope == null) return new List<DisplayProfile>();

            if (found.Major > CurrentFormat.Major)
            {
                // Breaking change: fields this build reads may mean something else
                // entirely. Refuse the data rather than misinterpret it — a layout
                // applied from a misread file drives real hardware.
                string backup = Backup(path, found);
                LastNotice =
                    $"Your settings use format {found.ToString(2)}, which this build of " +
                    $"{AppInfo.Name} ({AppInfo.Version}) cannot read. They were saved as " +
                    $"{Path.GetFileName(backup)} and this session starts empty.";
                return new List<DisplayProfile>();
            }

            if (found.Minor > CurrentFormat.Minor)
            {
                // Additive-only change: everything this build understands is still
                // correct, so load normally. But saving would drop the fields it does
                // not know about, so keep the original first.
                string backup = Backup(path, found);
                LastNotice =
                    $"Your settings were written by a newer {AppInfo.Name}. They work here, " +
                    $"but saving drops the newer settings — a copy is kept as {Path.GetFileName(backup)}.";
            }
            else if (found < CurrentFormat)
            {
                envelope = Migrate(envelope, found);
                LastNotice = $"Settings upgraded from format {found.ToString(2)} to {CurrentFormat.ToString(2)}.";

                // Persist immediately so the upgrade is not redone on every launch.
                TrySaveProfiles(envelope.Layouts, out _);
            }

            Reconcile(envelope.Layouts);
            return envelope.Layouts;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading settings: {ex.Message}");
            LastNotice = $"Your settings could not be read ({ex.GetType().Name}). Starting with no layouts.";
            return new List<DisplayProfile>();
        }
    }

    private static SettingsEnvelope? Parse(string json, out Version version)
    {
        version = CurrentFormat;

        var envelope = JsonSerializer.Deserialize<SettingsEnvelope>(json, JsonOptions);
        if (envelope == null) return null;

        // An unparseable or absent version is treated as current rather than as a
        // failure: the alternative is discarding a file that is probably fine.
        if (Version.TryParse(envelope.FormatVersion, out var parsed)) version = parsed;
        return envelope;
    }

    /// <summary>
    /// Walks the file forward one version at a time. Sequential steps rather than a
    /// jump straight to current, so a file three versions behind is handled by the
    /// same code that handled it one version behind.
    ///
    /// Empty today — v1 is the first shipped format, so nothing older exists. Each
    /// future format bump adds a `case n:` here that upgrades n to n+1.
    /// </summary>
    private static SettingsEnvelope Migrate(SettingsEnvelope envelope, Version from)
    {
        // Each future format bump adds its step here, in order:
        //   if (from < new Version(2, 0)) UpgradeTo2_0(envelope);
        //   if (from < new Version(2, 1)) UpgradeTo2_1(envelope);
        _ = from;

        envelope.FormatVersion = CurrentFormat.ToString(2);
        return envelope;
    }

    private static string Backup(string path, Version version)
    {
        string backup = $"{path}.v{version.ToString(2)}.bak";
        try
        {
            File.Copy(path, backup, overwrite: true);
        }
        catch
        {
            // Best effort; the notice still tells the user what happened.
        }
        return backup;
    }

    /// <summary>
    /// With CCD identity there is nothing to reconcile: MonitorDevicePath is stable
    /// across enable/disable, renumbering and reboots, so a saved target already knows
    /// exactly which physical output it means. All that happens here is (a) tagging
    /// pre-CCD layouts for re-capture and (b) refreshing the cosmetic \\.\DISPLAYn label.
    /// </summary>
    private static void Reconcile(List<DisplayProfile> layouts)
    {
        var currentHardware = DisplayEngine.GetCurrentDisplays();

        foreach (var p in layouts)
        {
            p.Displays.RemoveAll(d => d.Width <= 0 || d.Height <= 0);

            foreach (var target in p.Displays)
            {
                var matched = currentHardware.FirstOrDefault(h =>
                    DisplayEngine.SameHardwareIdentity(h.MonitorDevicePath, target.MonitorDevicePath));
                if (matched == null) continue;

                target.DeviceName = matched.DeviceName;
                target.HardwareId = matched.MonitorDevicePath;
                if (string.IsNullOrWhiteSpace(target.MonitorId)) target.MonitorId = matched.MonitorId;
                if (string.IsNullOrWhiteSpace(target.RelativePosition)) target.RelativePosition = matched.RelativePosition;
            }

            // Identity is unique per physical port, so a duplicate here means genuinely
            // duplicated data rather than a collapsed key.
            p.Displays = p.Displays
                .GroupBy(d => d.MonitorDevicePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }
    }

    public static void SaveProfiles(List<DisplayProfile> profiles) => TrySaveProfiles(profiles, out _);

    public static bool TrySaveProfiles(List<DisplayProfile> profiles, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);

            var envelope = new SettingsEnvelope
            {
                FormatVersion = CurrentFormat.ToString(2),
                App = AppInfo.Name,
                AppVersion = AppInfo.Version,
                SavedUtc = DateTime.UtcNow,
                Layouts = profiles
            };

            // Write to a temporary file and swap. A crash or a full disk mid-write
            // would otherwise leave a truncated settings file, which reads as "no
            // layouts" — the one outcome worse than failing to save at all.
            string target = AppPaths.SettingsFile;
            string temp = target + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(envelope, JsonOptions));

            if (File.Exists(target)) File.Replace(temp, target, destinationBackupFileName: null);
            else File.Move(temp, target);

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving settings: {ex.Message}");
            errorMessage = $"Could not save settings to {AppPaths.SettingsFile}: {ex.Message}";
            return false;
        }
    }
}
