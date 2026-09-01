namespace MonitorLayoutSwitcher;

internal static class LayoutSafety
{
    public const int UndoSeconds = 20;

    private static DisplayProfile? _snapshot;
    private static DateTime _expiresUtc;

    public static event EventHandler? UndoStateChanged;

    public static bool CanUndo => _snapshot != null && DateTime.UtcNow < _expiresUtc;

    public static int RemainingSeconds =>
        CanUndo ? Math.Max(0, (int)Math.Ceiling((_expiresUtc - DateTime.UtcNow).TotalSeconds)) : 0;

    public static bool Apply(DisplayProfile profile, out string message)
    {
        var snapshot = DisplayEngine.CaptureCurrentLayoutAsProfile("Previous layout", string.Empty);
        if (!DisplayEngine.ApplyProfile(profile, out message))
        {
            // Keep the raw "Failed to configure/disable display X (Code: N)" detail
            // visible alongside the friendly translation — without it there's no way
            // to tell which specific device/step failed from the UI alone.
            message = $"{DisplayEngine.FriendlyError(message)} [{message}]";
            return false;
        }

        _snapshot = snapshot;
        _expiresUtc = DateTime.UtcNow.AddSeconds(UndoSeconds);
        var verify = DisplayEngine.Verify(profile);
        message = verify.Matched
            ? $"Applied '{profile.Name}'."
            : $"Applied '{profile.Name}', but {verify.Summary}.";
        UndoStateChanged?.Invoke(null, EventArgs.Empty);
        return true;
    }

    public static bool Undo(out string message)
    {
        if (!CanUndo || _snapshot == null)
        {
            message = "Nothing to undo.";
            return false;
        }

        var snapshot = _snapshot;
        bool ok = DisplayEngine.ApplyProfile(snapshot, out message);
        if (ok)
        {
            _snapshot = null;
            _expiresUtc = DateTime.MinValue;
        }
        UndoStateChanged?.Invoke(null, EventArgs.Empty);
        if (ok) message = "Restored the previous layout.";
        else message = DisplayEngine.FriendlyError(message);
        return ok;
    }
}
