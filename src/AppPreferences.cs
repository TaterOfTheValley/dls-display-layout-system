using System.Text.Json;

namespace DLS;

/// <summary>
/// App-wide choices that are not layouts.
///
/// Kept in its own file beside the settings rather than inside them: these belong to
/// the person and the machine, not to a layout, and keeping them out of the settings
/// file leaves its format, the editor's saving and the file watcher alone.
/// </summary>
internal static class AppPreferences
{
    private const string FileName = "preferences.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static Store? _store;

    private static string FilePath => Path.Combine(AppPaths.SettingsDirectory, FileName);

    private sealed class Store
    {
        public bool ConfirmSwitches { get; set; } = true;
    }

    /// <summary>
    /// Whether a switch made from the tray, a hotkey or the editor asks "keep this
    /// layout?" and reverts on its own if nobody answers. On by default: the way a
    /// display switch fails is a black monitor with the button on it, so the safe
    /// behaviour is the one a person has to turn off. Off applies straight away and
    /// leaves only the 20-second Undo.
    /// </summary>
    public static bool ConfirmSwitches
    {
        get { lock (Gate) return Current().ConfirmSwitches; }
        set
        {
            lock (Gate)
            {
                var store = Current();
                store.ConfirmSwitches = value;
                Save(store);
            }
        }
    }

    private static Store Current() => _store ??= Load();

    private static Store Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Store();
            return JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath), JsonOptions) ?? new Store();
        }
        catch
        {
            // Unreadable means the defaults, which ask — the safe direction to fail.
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
            // Best effort: the choice still holds for this session.
        }
    }
}
