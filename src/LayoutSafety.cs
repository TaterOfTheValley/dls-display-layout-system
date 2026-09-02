namespace DLS;

/// <summary>
/// Wraps every layout change in a revert-by-default safety net.
///
/// The important property: after an interactive switch, doing nothing restores the
/// previous layout. A display switch can leave the user staring at a monitor it
/// just turned off, unable to reach any button — so confirmation has to be the
/// action that requires input, not the recovery.
///
/// A non-interactive apply (the CLI, a hotkey handler with no message loop) keeps
/// the old opt-in behaviour: the snapshot is retained and Undo stays available for
/// <see cref="UndoSeconds"/>, but nothing reverts on its own.
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

    /// <summary>Non-interactive apply — no confirmation prompt, no auto-revert.</summary>
    public static bool Apply(DisplayProfile profile, out string message) =>
        Apply(profile, interactive: false, out message);

    public static bool Apply(DisplayProfile profile, bool interactive, out string message)
    {
        // Capture BEFORE applying: this is the exact topology to come back to, and it
        // also records which monitor was primary, which is what tells us afterwards
        // whether the app's own windows need to follow.
        var snapshot = DisplayEngine.CaptureCurrentLayoutAsProfile("Previous layout", string.Empty);
        string previousPrimary = WindowFollow.PrimaryIdentityOf(snapshot);

        // A pending prompt from an earlier switch is now stale — its snapshot
        // describes a layout two changes ago. Dismiss it without reverting.
        KeepLayoutDialog.Dismiss();

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

        if (interactive)
        {
            KeepLayoutDialog.Show(
                profile.Name,
                profile.Displays,
                UndoSeconds,
                onKeep: Confirm,
                onRevert: () => Undo(out _));
        }

        return true;
    }

    /// <summary>
    /// Accepts the current layout: drops the snapshot so nothing reverts and the
    /// undo affordances disappear.
    /// </summary>
    public static void Confirm()
    {
        if (_snapshot == null) return;
        _snapshot = null;
        _expiresUtc = DateTime.MinValue;
        UndoStateChanged?.Invoke(null, EventArgs.Empty);
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

        KeepLayoutDialog.Dismiss();

        bool ok = DisplayEngine.ApplyProfile(snapshot, out message);
        message = ok ? "Restored the previous layout." : DisplayEngine.FriendlyError(message);

        // A revert can hand the primary back to the monitor it came from, so the
        // windows have to follow in that direction too.
        if (ok) WindowFollow.AfterApply(previousPrimary);

        UndoStateChanged?.Invoke(null, EventArgs.Empty);
        return ok;
    }
}
