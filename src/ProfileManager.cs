using System.Text.Json;

namespace MonitorLayoutSwitcher;

public static class ProfileManager
{
    private static readonly string LocalConfigPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "profiles.json"
    );

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string ConfigPath => LocalConfigPath;

    public static List<DisplayProfile> LoadProfiles()
    {
        try
        {
            string path = File.Exists(LocalConfigPath) ? LocalConfigPath : string.Empty;

            if (!string.IsNullOrEmpty(path))
            {
                string json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<List<DisplayProfile>>(json, JsonOptions);
                if (loaded != null && loaded.Count > 0)
                {
                    // With CCD identity there is nothing to reconcile: MonitorDevicePath
                    // is stable across enable/disable, renumbering and reboots, so a
                    // saved target already knows exactly which physical output it
                    // means. All that happens here is (a) tagging pre-CCD profiles for
                    // re-capture and (b) refreshing the cosmetic \\.\DISPLAYn label.
                    //
                    // The old code did three-tier fuzzy matching and then deduped on a
                    // collapsed key, which silently dropped a monitor from the profile
                    // whenever two targets resolved to the same live device. Both the
                    // fuzzy matching and the lossy dedup are gone.
                    var currentHardware = DisplayEngine.GetCurrentDisplays();
                    foreach (var p in loaded)
                    {
                        p.Displays.RemoveAll(d => d.Width <= 0 || d.Height <= 0);

                        // A profile written before the CCD rewrite has no device path.
                        // Its old identity key could not tell two same-model monitors
                        // apart, so it is not safe to migrate — flag it instead.
                        if (p.Displays.Any(d => string.IsNullOrWhiteSpace(d.MonitorDevicePath)))
                        {
                            p.SchemaVersion = 1;
                        }

                        foreach (var target in p.Displays)
                        {
                            var matched = currentHardware.FirstOrDefault(h =>
                                DisplayEngine.SameHardwareIdentity(h.MonitorDevicePath, target.MonitorDevicePath));
                            if (matched == null) continue;

                            // DISPLAYn is transient and shown in the UI only.
                            target.DeviceName = matched.DeviceName;
                            target.HardwareId = matched.MonitorDevicePath;
                            if (string.IsNullOrWhiteSpace(target.MonitorId)) target.MonitorId = matched.MonitorId;
                            if (string.IsNullOrWhiteSpace(target.RelativePosition)) target.RelativePosition = matched.RelativePosition;
                        }

                        // Identity is unique per physical port now, so a duplicate here
                        // means genuinely duplicated data rather than a collapsed key.
                        p.Displays = p.Displays
                            .GroupBy(d => d.MonitorDevicePath, StringComparer.OrdinalIgnoreCase)
                            .Select(g => g.First())
                            .ToList();
                    }

                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading profiles: {ex.Message}");
        }

        // No profiles yet. Start empty rather than inventing any.
        //
        // Deliberate: a generated starter profile is built from whatever monitors
        // this particular PC happens to have, so on anyone else's machine it is
        // either meaningless or actively wrong — and because it also writes
        // profiles.json on first run, it looks like deliberate configuration rather
        // than a guess. The UI prompts to capture the current layout instead, which
        // takes one click and is correct by construction.
        return new List<DisplayProfile>();
    }

    public static void SaveProfiles(List<DisplayProfile> profiles)
    {
        TrySaveProfiles(profiles, out _);
    }

    public static bool TrySaveProfiles(List<DisplayProfile> profiles, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            string json = JsonSerializer.Serialize(profiles, JsonOptions);

            // Keep the configuration beside the executable so the portable build
            // and the running binary always use the same profile data.
            File.WriteAllText(LocalConfigPath, json);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving profiles: {ex.Message}");
            errorMessage = $"Could not save profiles to {LocalConfigPath}: {ex.Message}";
            return false;
        }
    }

}
