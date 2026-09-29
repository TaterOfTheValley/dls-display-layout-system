namespace DLS;

/// <summary>
/// What counts as a usable global shortcut, in one place.
///
/// A global hotkey is registered with Windows and swallows that key press everywhere,
/// for every program. That makes a bad one — Caps Lock on its own, a bare letter,
/// Backspace — far worse than a shortcut that does not work: it silently breaks the
/// key for the whole machine for as long as DLS runs. So the rules are enforced where a
/// shortcut is recorded, where the settings file is read, and where the hotkey is
/// registered, because a settings file can also arrive by hand or from an older build.
/// </summary>
internal static class Hotkeys
{
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;

    public readonly record struct Combo(uint Modifiers, Keys Key);

    /// <summary>Reads text such as "Ctrl + Shift + F1". False if it is not a shortcut at
    /// all — an unknown word, no key, or more than one key.</summary>
    public static bool TryParse(string? text, out Combo combo)
    {
        combo = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        uint modifiers = 0;
        Keys key = Keys.None;

        foreach (var token in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || token.Equals("Control", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModControl;
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModAlt;
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModShift;
            else if (token.Equals("Win", StringComparison.OrdinalIgnoreCase) || token.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModWin;
            else
            {
                if (key != Keys.None) return false;   // two keys is not a shortcut

                if (token.Length == 1 && char.IsDigit(token[0]))
                    key = (Keys)((int)Keys.D0 + (token[0] - '0'));
                else if (token.Length == 1 && char.IsLetter(token[0]))
                    key = (Keys)((int)Keys.A + (char.ToUpperInvariant(token[0]) - 'A'));
                else if (!token.Contains(',') && !int.TryParse(token, out _) &&
                         Enum.TryParse<Keys>(token, true, out var named) && (named & ~Keys.KeyCode) == 0)
                    key = named;   // numeric text and flag lists parse as Keys too; neither is a key name
                else
                    return false;
            }
        }

        if (key == Keys.None) return false;
        combo = new Combo(modifiers, key);
        return true;
    }

    /// <summary>Why this shortcut cannot be used, or null if it can. Empty text is
    /// "no shortcut", which is always fine.</summary>
    public static string? Problem(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!TryParse(text, out var combo)) return "it is not a valid shortcut";
        return Problem(combo);
    }

    public static string? Problem(Combo combo)
    {
        var key = combo.Key;

        if (key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
            or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
            or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin)
            return "a modifier key needs another key with it";

        if (key is Keys.Capital or Keys.NumLock or Keys.Scroll)
            return $"{KeyName(key)} can't be used — a shortcut would stop it working everywhere";

        // Ctrl, Alt or Win makes a combination nobody types by accident. Shift alone
        // does not: Shift+A is a capital letter, and a hotkey there would eat it.
        if ((combo.Modifiers & (ModControl | ModAlt | ModWin)) != 0) return null;

        // Function keys are the exception — Shift+F5 collides with nothing you type —
        // and F13–F24, which exist on macro keyboards precisely to be bound, may stand alone.
        if (key >= Keys.F13 && key <= Keys.F24) return null;
        if (key >= Keys.F1 && key <= Keys.F12 && combo.Modifiers != 0) return null;

        return $"{KeyName(key)} needs Ctrl, Alt or Win with it, or it would stop working in every program";
    }

    /// <summary>
    /// Clears every shortcut that cannot be used, and every one that a layout earlier in
    /// the list already owns — two layouts cannot both have a key, and the second would
    /// silently never fire. Returns what it removed, one sentence each, for the user.
    /// </summary>
    public static List<string> Clean(IEnumerable<DisplayProfile> layouts)
    {
        var notes = new List<string>();
        var owners = new Dictionary<Combo, string>();

        foreach (var layout in layouts)
        {
            if (string.IsNullOrWhiteSpace(layout.Hotkey))
            {
                layout.Hotkey = string.Empty;
                continue;
            }

            string? problem = Problem(layout.Hotkey);
            if (problem == null && TryParse(layout.Hotkey, out var combo))
            {
                if (!owners.TryGetValue(combo, out string? owner))
                {
                    owners[combo] = layout.Name;
                    continue;
                }
                problem = $"'{owner}' already uses it";
            }

            notes.Add($"Removed the shortcut \"{layout.Hotkey}\" from '{layout.Name}': {problem}.");
            layout.Hotkey = string.Empty;
        }

        return notes;
    }

    /// <summary>The layout in <paramref name="others"/> that already uses this shortcut, if any.</summary>
    public static DisplayProfile? OwnerOf(string hotkey, IEnumerable<DisplayProfile> others)
    {
        if (!TryParse(hotkey, out var wanted)) return null;
        return others.FirstOrDefault(p => TryParse(p.Hotkey, out var theirs) && theirs == wanted);
    }

    /// <summary>The name a person would use for a key, where the enum's is not it.</summary>
    public static string KeyName(Keys key) => key switch
    {
        Keys.Capital => "Caps Lock",
        Keys.NumLock => "Num Lock",
        Keys.Scroll => "Scroll Lock",
        Keys.Back => "Backspace",
        Keys.Return => "Enter",
        Keys.Next => "Page Down",
        Keys.Prior => "Page Up",
        _ => key.ToString()
    };
}
