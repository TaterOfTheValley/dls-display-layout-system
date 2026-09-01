namespace MonitorLayoutSwitcher;

/// <summary>
/// The "Keep this layout?" prompt, modelled on Windows' own display-settings
/// confirmation and shown for the same reason: the failure mode of a layout switch
/// is that the monitor you would click on is now black. So the safe answer has to
/// be the one that needs no input — do nothing and the previous layout comes back.
///
/// Deliberately modeless and TopMost: the tray menu and config window stay usable,
/// and the prompt cannot be lost behind another window during the countdown.
/// It is placed on the PRIMARY display, which the layout just made active and is
/// therefore guaranteed to be visible.
/// </summary>
internal sealed class KeepLayoutDialog : Form
{
    private static KeepLayoutDialog? _open;

    private readonly Label _message = new();
    private readonly Label _countdown = new();
    private readonly System.Windows.Forms.Timer _tick = new();
    private readonly DateTime _expiresUtc;
    private bool _settled;

    public static void Show(string profileName, int seconds, Action onKeep, Action onRevert)
    {
        Dismiss();
        _open = new KeepLayoutDialog(profileName, seconds, onKeep, onRevert);
        _open.Show();
        _open.BringToFront();
    }

    /// <summary>Dismisses any open prompt without reverting. Named Dismiss rather
    /// than Close so it cannot be confused with the inherited Form.Close.</summary>
    public static void Dismiss()
    {
        if (_open is { IsDisposed: false })
        {
            _open.MarkSettled();
            _open.Close();
        }
        _open = null;
    }

    private void MarkSettled() => _settled = true;

    private KeepLayoutDialog(string profileName, int seconds, Action onKeep, Action onRevert)
    {
        _expiresUtc = DateTime.UtcNow.AddSeconds(seconds);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = UiTheme.Panel;
        AutoScaleMode = AutoScaleMode.Dpi;

        float scale = DeviceDpi / 96f;
        int S(int v) => (int)Math.Round(v * scale);

        Size = new Size(S(420), S(168));
        Padding = new Padding(S(20));

        _message.Text = $"Keep the “{profileName}” layout?";
        _message.ForeColor = UiTheme.Text;
        _message.Font = new Font("Segoe UI", 11f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        _message.AutoSize = false;
        _message.Bounds = new Rectangle(S(20), S(20), S(380), S(26));

        _countdown.ForeColor = UiTheme.Muted;
        _countdown.Font = new Font("Segoe UI", 9.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _countdown.AutoSize = false;
        _countdown.Bounds = new Rectangle(S(20), S(50), S(380), S(40));

        var keep = UiTheme.MakeButton("Keep this layout", primary: true, scale);
        keep.Bounds = new Rectangle(S(20), S(104), S(190), S(36));
        keep.Click += (_, _) =>
        {
            _settled = true;
            onKeep();
            Close();
        };

        var revert = UiTheme.MakeButton("Revert now", primary: false, scale);
        revert.Bounds = new Rectangle(S(220), S(104), S(180), S(36));
        revert.Click += (_, _) =>
        {
            _settled = true;
            onRevert();
            Close();
        };

        Controls.AddRange(new Control[] { _message, _countdown, keep, revert });
        AcceptButton = keep;

        UpdateCountdown();
        _tick.Interval = 250;
        _tick.Tick += (_, _) =>
        {
            UpdateCountdown();
            if (DateTime.UtcNow < _expiresUtc || _settled) return;

            // Timed out with no answer. Revert — the user may well be looking at a
            // monitor that this layout turned off.
            _settled = true;
            _tick.Stop();
            onRevert();
            Close();
        };
        _tick.Start();

        PlaceOnPrimary();
    }

    private void UpdateCountdown()
    {
        int left = Math.Max(0, (int)Math.Ceiling((_expiresUtc - DateTime.UtcNow).TotalSeconds));
        _countdown.Text = $"Reverting to the previous layout in {left}s unless you keep it.";
    }

    private void PlaceOnPrimary()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
        Location = new Point(
            area.X + (area.Width - Width) / 2,
            area.Y + (int)(area.Height * 0.62));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(UiTheme.Gold, 1);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tick.Stop();
        _tick.Dispose();
        if (ReferenceEquals(_open, this)) _open = null;
        base.OnFormClosed(e);
    }

    // Never steal focus from whatever the user is doing.
    protected override bool ShowWithoutActivation => true;
}
