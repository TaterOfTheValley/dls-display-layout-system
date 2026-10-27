namespace DLS;

/// <summary>
/// Applies layout changes immediately and retains the previous layout for manual
/// Undo for <see cref="UndoSeconds"/>. Nothing reverts automatically.
/// </summary>
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
        // Capture BEFORE applying: this is the exact topology to come back to, and it
        // also records which monitor was primary, which is what tells us afterwards
        // whether the app's own windows need to follow.
        var snapshot = DisplayEngine.CaptureCurrentLayoutAsProfile("Previous layout", string.Empty);
        string previousPrimary = WindowFollow.PrimaryIdentityOf(snapshot);

        if (!DisplayEngine.ApplyProfile(profile, out message))
        {
            // Keep the raw "[tier apply -> ERROR_x]" detail alongside the friendly
            // translation — without it there's no way to tell which step failed.
            message = DisplayEngine.FriendlyError(message);
            return false;
        }

        _snapshot = snapshot;
        _expiresUtc = DateTime.UtcNow.AddSeconds(UndoSeconds);

        // If this switch promoted a different panel to primary, the editor must not be
        // left behind on the old one — it is holding the undo for a change the user
        // may no longer be able to see.
        WindowFollow.AfterApply(previousPrimary);

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
        string previousPrimary = WindowFollow.LivePrimaryIdentity();

        // Clear the snapshot first. If the restore itself fails there is nothing
        // useful to retry — re-applying a layout that was just rejected only
        // produces the same error, and leaving Undo enabled invites a loop.
        _snapshot = null;
        _expiresUtc = DateTime.MinValue;

        bool ok = DisplayEngine.ApplyProfile(snapshot, out message);
        message = ok ? "Restored the previous layout." : DisplayEngine.FriendlyError(message);

        // A revert can hand the primary back to the monitor it came from, so the
        // windows have to follow in that direction too.
        if (ok) WindowFollow.AfterApply(previousPrimary);

        UndoStateChanged?.Invoke(null, EventArgs.Empty);
        return ok;
    }
}
