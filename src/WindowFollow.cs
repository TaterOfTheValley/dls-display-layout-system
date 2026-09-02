namespace DLS;

/// <summary>
/// Moves this app's own windows onto whichever monitor a layout switch just made
/// primary.
///
/// Windows rescues a window from a monitor it removes, but it does not make windows
/// follow the primary — so a layout that promotes a different panel can leave the
/// editor sitting on a screen the user has stopped looking at. For a display tool
/// that is the one place its window must not be: the window that performed the
/// switch is also the window holding the undo.
///
/// Only a change of primary triggers this. A layout that merely moves monitors
/// around leaves windows where the user put them.
/// </summary>
internal static class WindowFollow
{
    /// <summary>
    /// How long to wait for the desktop to settle before moving anything.
    /// SetDisplayConfig returns before Windows has finished rearranging windows, and
    /// before WinForms' Screen cache — invalidated from the SystemEvents thread — has
    /// caught up. Repositioning inside that window is simply undone again.
    /// </summary>
    private const int SettleMs = 300;

    /// <summary>
    /// A second pass, once any WM_DPICHANGED caused by the first move has been
    /// handled. Landing on a monitor with a different scale factor makes Windows
    /// resize the window, and ConfigForm.OnDpiChanged adopts that suggested
    /// rectangle, so the centring has to be redone against the new size. The pass is
    /// idempotent: on a same-scale move it re-sets the identical bounds.
    /// </summary>
    private const int SettleAgainMs = 700;

    /// <summary>The monitor a saved or captured layout designates as primary.</summary>
    public static string PrimaryIdentityOf(DisplayProfile profile) =>
        profile.Displays.FirstOrDefault(d => d.Enabled && d.IsPrimary)?.MonitorDevicePath ?? string.Empty;

    /// <summary>The monitor Windows is treating as primary right now.</summary>
    public static string LivePrimaryIdentity() =>
        DisplayEngine.GetCurrentDisplays()
            .FirstOrDefault(d => d.IsAttached && d.IsPrimary)?.MonitorDevicePath ?? string.Empty;

    /// <summary>
    /// Called after a layout has been applied. Does nothing unless the primary is now
    /// a different physical panel than <paramref name="previousPrimary"/> — including
    /// when that argument is empty, i.e. the previous primary could not be
    /// established, since relocating on a guess would move the window for no visible
    /// reason.
    /// </summary>
    public static void AfterApply(string previousPrimary)
    {
        if (string.IsNullOrWhiteSpace(previousPrimary)) return;

        string now = LivePrimaryIdentity();
        if (string.IsNullOrWhiteSpace(now) || CcdEngine.SameMonitor(now, previousPrimary)) return;

        ScheduleMove();
    }

    private static void ScheduleMove()
    {
        // Any open form will do as a thread anchor — they all live on the UI thread.
        // None open means a CLI apply, where there is nothing to move.
        var host = Application.OpenForms.Cast<Form>()
            .FirstOrDefault(f => !f.IsDisposed && f.IsHandleCreated);
        if (host == null) return;

        try
        {
            host.BeginInvoke(() =>
            {
                RunAfter(SettleMs, MoveWindowsToPrimary);
                RunAfter(SettleAgainMs, MoveWindowsToPrimary);
            });
        }
        catch (ObjectDisposedException)
        {
            // The window closed between the check and the post. Nothing to move.
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void RunAfter(int milliseconds, Action action)
    {
        var timer = new System.Windows.Forms.Timer { Interval = milliseconds };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            action();
        };
        timer.Start();
    }

    private static void MoveWindowsToPrimary()
    {
        var work = PrimaryWorkingArea();
        if (work.Width <= 0 || work.Height <= 0) return;

        foreach (var form in Application.OpenForms.Cast<Form>().ToList())
        {
            if (form.IsDisposed || !form.IsHandleCreated) continue;

            switch (form)
            {
                // Places itself, and deliberately not at the centre.
                case KeepLayoutDialog dialog:
                    dialog.PlaceOnPrimary();
                    continue;

                // Anchored to the tray icon, and closed the moment focus moves.
                case TrayPopup:
                    continue;
            }

            CentreOn(form, work);
        }
    }

    private static void CentreOn(Form form, Rectangle work)
    {
        // A maximized window is maximized *on a monitor*, and the only way to change
        // which one is to restore it, move it, and maximize it again. Minimized needs
        // no such dance: WinForms routes a bounds change on a minimized form to its
        // restore rectangle, so it comes back on the right screen.
        bool maximized = form.WindowState == FormWindowState.Maximized;
        if (maximized) form.WindowState = FormWindowState.Normal;

        var size = form.WindowState == FormWindowState.Normal ? form.Size : form.RestoreBounds.Size;

        // Shrink to fit the new screen — the promoted monitor can be considerably
        // smaller than the one the window was sized on — but never below the form's
        // own minimum, which is the size its layout actually needs.
        int width = Math.Max(form.MinimumSize.Width, Math.Min(size.Width, work.Width));
        int height = Math.Max(form.MinimumSize.Height, Math.Min(size.Height, work.Height));

        form.SetBounds(
            work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2,
            width, height);

        if (maximized) form.WindowState = FormWindowState.Maximized;
    }

    /// <summary>
    /// The bounds of the monitor that is primary right now, read from the CCD
    /// topology rather than from Screen.PrimaryScreen: WinForms clears its screen
    /// cache from the SystemEvents thread, so immediately after a switch that static
    /// can still describe the previous arrangement.
    /// </summary>
    internal static Rectangle PrimaryBounds()
    {
        var primary = DisplayEngine.GetCurrentDisplays().FirstOrDefault(d => d.IsAttached && d.IsPrimary);
        if (primary != null && primary.Width > 0 && primary.Height > 0)
            return new Rectangle(primary.X, primary.Y, primary.Width, primary.Height);

        return Screen.PrimaryScreen?.Bounds ?? (Screen.AllScreens.Length > 0 ? Screen.AllScreens[0].Bounds : Rectangle.Empty);
    }

    /// <summary>
    /// As <see cref="PrimaryBounds"/>, less the taskbar. Only Screen knows where the
    /// taskbar is, so it is still consulted — but only for the monitor whose bounds
    /// match what CCD reports, which is what keeps a stale cache from placing the
    /// window on the wrong screen.
    /// </summary>
    private static Rectangle PrimaryWorkingArea()
    {
        var bounds = PrimaryBounds();
        var match = Screen.AllScreens.FirstOrDefault(s => s.Bounds == bounds);
        return match?.WorkingArea ?? bounds;
    }
}
