using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DLS;

/// <summary>
/// Remembers which exact layouts the user has already kept, so switching to one of
/// them again does not ask a question that has already been answered.
///
/// The keep-or-revert prompt exists for a layout that might leave the user with no
/// usable screen. A layout that was applied and kept once has proven it does not, so
/// asking again on every switch is friction without protection. A layout that has
/// never been kept — new, or edited since — still gets the prompt.
///
/// What is remembered is a fingerprint of the *result*, not the layout's name: the
/// name is a label the user can change freely, while the fingerprint changes whenever
/// anything that decides what ends up on screen does. That includes the set of
/// monitors that are plugged in, because "Laptop only" is a different experiment
/// docked than it is undocked.
///
/// Kept in its own file beside the settings rather than inside them. It is derived
/// state with no reason to travel with a copied settings file, and keeping it out of
/// the layouts leaves the settings format, the editor's autosave and the file watcher
/// alone.
/// </summary>
internal static class TrustedLayouts
{
    private const string FileName = "trusted-layouts.json";

    /// <summary>Oldest fingerprints are dropped past this, so the file cannot grow
    /// without bound as layouts are edited over the years. Losing one only costs a
    /// single extra prompt.</summary>
    private const int MaxRemembered = 300;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(AppPaths.SettingsDirectory, FileName);

    private sealed class Store
    {
        public List<string> Kept { get; set; } = new();
    }

    /// <summary>
    /// Fingerprint of applying <paramref name="profile"/> while
    /// <paramref name="connected"/> monitors are plugged in.
    /// </summary>
    public static string KeyFor(DisplayProfile profile, IEnumerable<DisplayInfo> connected)
    {
        var text = new StringBuilder();

        // Every connected monitor counts, enabled or not: a layout that switches a
        // monitor off behaves differently if that monitor is not there.
        foreach (string path in connected.Select(d => d.MonitorDevicePath.ToLowerInvariant()).OrderBy(p => p, StringComparer.Ordinal))
        {
            text.Append("hw|").Append(path).Append('\n');
        }

        foreach (var d in profile.Displays.OrderBy(d => d.MonitorDevicePath, StringComparer.OrdinalIgnoreCase))
        {
            text.Append("tgt|").Append(d.MonitorDevicePath.ToLowerInvariant())
                .Append('|').Append(d.Enabled ? 1 : 0);

            // A disabled monitor's stored geometry is stale leftovers and plays no part
            // in the result, so it must not make an unrelated edit look meaningful.
            if (d.Enabled)
            {
                text.Append('|').Append(d.X).Append(',').Append(d.Y)
                    .Append('|').Append(d.Width).Append('x').Append(d.Height)
                    .Append('|').Append(d.RefreshNumerator).Append('/').Append(d.RefreshDenominator)
                    .Append('|').Append(d.RefreshRate)
                    .Append('|').Append(d.IsPrimary ? 1 : 0)
                    .Append('|').Append(d.Rotation)
                    .Append('|').Append(d.ScalePercent);
            }
            text.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public static bool IsTrusted(string key)
    {
        lock (Gate) return Load().Kept.Contains(key, StringComparer.Ordinal);
    }

    /// <summary>Records that the user kept this result. Best effort: failing to write
    /// only means the prompt appears again next time.</summary>
    public static void Remember(string key)
    {
        lock (Gate)
        {
            var store = Load();
            store.Kept.Remove(key);
            store.Kept.Add(key);
            if (store.Kept.Count > MaxRemembered) store.Kept.RemoveRange(0, store.Kept.Count - MaxRemembered);
            Save(store);
        }
    }

    private static Store Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Store();
            return JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath), JsonOptions) ?? new Store();
        }
        catch
        {
            // Unreadable means nothing is trusted, which is the safe direction to fail.
            return new Store();
        }
    }

    private static void Save(Store store)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(store, JsonOptions));
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, destinationBackupFileName: null);
            else File.Move(temp, FilePath);
        }
        catch
        {
            // See Remember.
        }
    }
}
