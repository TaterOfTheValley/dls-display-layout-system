using System.Text.Json;

namespace MonitorLayoutSwitcher;

public static class ProfileManager
{
    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MonitorLayoutSwitcher"
    );

    private static readonly string ConfigPath = Path.Combine(ConfigDirectory, "profiles.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static List<DisplayProfile> LoadProfiles()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<List<DisplayProfile>>(json, JsonOptions);
                if (loaded != null && loaded.Count > 0)
                {
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
        try
        {
            if (!Directory.Exists(ConfigDirectory))
            {
                Directory.CreateDirectory(ConfigDirectory);
            }

            string json = JsonSerializer.Serialize(profiles, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving profiles: {ex.Message}");
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
