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
                    // Legacy profiles may lack HardwareId and retain a stale DISPLAYn
                    // number. Prefer stable identity, then a compatible device name,
                    // and finally a unique monitor description for renumbered outputs.
                    var currentHardware = DisplayEngine.GetCurrentDisplays();
                    foreach (var p in loaded)
                    {
                        p.Displays.RemoveAll(d => d.Width <= 0 || d.Height <= 0);

                        foreach (var target in p.Displays)
                        {
                            var matched = currentHardware.FirstOrDefault(h =>
                                !string.IsNullOrWhiteSpace(target.HardwareId) &&
                                DisplayEngine.SameHardwareIdentity(h.HardwareId, target.HardwareId));

                            if (matched == null)
                            {
                                matched = currentHardware.FirstOrDefault(h =>
                                    string.Equals(h.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase) &&
                                    (string.IsNullOrWhiteSpace(target.MonitorId) ||
                                     string.IsNullOrWhiteSpace(h.MonitorId) ||
                                     DisplayEngine.SameMonitorDescription(h.MonitorId, target.MonitorId)));
                            }

                            if (matched == null && !string.IsNullOrWhiteSpace(target.MonitorId))
                            {
                                var descriptionMatches = currentHardware.Where(h =>
                                    DisplayEngine.SameMonitorDescription(h.MonitorId, target.MonitorId)).ToList();
                                bool uniqueSavedDescription = p.Displays.Count(d =>
                                    DisplayEngine.SameMonitorDescription(d.MonitorId, target.MonitorId)) == 1;
                                if (descriptionMatches.Count == 1 && uniqueSavedDescription)
                                {
                                    matched = descriptionMatches[0];
                                }
                            }

                            if (matched == null) continue;

                            // DISPLAYn is transient; refresh it whenever the live
                            // identity match proves which output this target is.
                            target.DeviceName = matched.DeviceName;
                            if (string.IsNullOrWhiteSpace(target.MonitorId)) target.MonitorId = matched.MonitorId;
                            if (string.IsNullOrWhiteSpace(target.HardwareId)) target.HardwareId = matched.HardwareId;
                            if (string.IsNullOrWhiteSpace(target.RelativePosition)) target.RelativePosition = matched.RelativePosition;
                            if (target.Width <= 0) target.Width = matched.Width;
                            if (target.Height <= 0) target.Height = matched.Height;
                        }

                        // Remove duplicates. Prefer HardwareId (stable across DISPLAYn
                        // renumbering) over DeviceName so two distinct saved targets
                        // that briefly resolved to the same live device during a
                        // topology change don't get silently collapsed into one.
                        p.Displays = p.Displays
                            .GroupBy(
                                d => string.IsNullOrWhiteSpace(d.HardwareId) ? d.DeviceName : d.HardwareId,
                                StringComparer.OrdinalIgnoreCase)
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

        // Return default starting profile set if none exist
        return GetDefaultProfiles();
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

    public static List<DisplayProfile> GetDefaultProfiles()
    {
        var currentDisplays = DisplayEngine.GetCurrentDisplays();
        var attached = currentDisplays.Where(d => d.IsAttached).OrderBy(d => d.X).ToList();

        // 1. "Desk / all three" profile
        var deskProfile = DisplayEngine.CaptureCurrentLayoutAsProfile("Desk / all three", "Ctrl + Alt + 1");

        // 2. "Share / left only" profile
        var shareProfile = new DisplayProfile
        {
            Name = "Share / left only",
            Hotkey = "Ctrl + Alt + 2",
            Displays = new List<DisplayTargetConfig>()
        };

        foreach (var d in currentDisplays)
        {
            bool isLeft = string.Equals(d.RelativePosition, "Left", StringComparison.OrdinalIgnoreCase);
            shareProfile.Displays.Add(new DisplayTargetConfig
            {
                DeviceName = d.DeviceName,
                MonitorId = d.MonitorId,
                HardwareId = d.HardwareId,
                RelativePosition = d.RelativePosition,
                Enabled = isLeft, // Only left is enabled
                X = isLeft ? 0 : d.X,
                Y = isLeft ? 0 : d.Y,
                Width = d.Width,
                Height = d.Height,
                RefreshRate = d.RefreshRate,
                IsPrimary = isLeft
            });
        }

        var defaultList = new List<DisplayProfile> { deskProfile, shareProfile };
        SaveProfiles(defaultList);
        return defaultList;
    }
}
