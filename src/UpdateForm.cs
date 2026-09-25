using Velopack;

namespace DLS;

/// <summary>Lets the user review, download, and install an available update.</summary>
internal sealed class UpdateForm : Form
{
    private readonly UpdateManager _manager;
    private readonly UpdateInfo _update;
    private readonly Func<VelopackAsset, bool> _apply;
    private readonly Button _installButton;
    private readonly ProgressBar _progress;
    private readonly Label _status;
    private readonly CancellationTokenSource _closing = new();
    private bool _downloaded;

    public UpdateForm(UpdateManager manager, UpdateInfo update, Func<VelopackAsset, bool> apply)
    {
        _manager = manager;
        _update = update;
        _apply = apply;

        // Unlike every other window in DLS, this one is laid out by WinForms rather
        // than by hand: it is a plain dialog, and letting AutoScaleMode.Dpi plus
        // auto-sizing panels do the work keeps it right at any display scale. The font
        // is in points for the same reason — a point size is already DPI-relative, so
        // the text follows the monitor, and the panels follow the text. Windows' Text
        // size setting is the one thing WinForms does not know about, so that is
        // applied here, to the font, and the layout grows around it.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font(UiType.Family, 9f * UiScaling.TextScale, FontStyle.Regular, GraphicsUnit.Point);

        Text = $"Update {AppInfo.Name}";
        Icon = AppIcon.Shared;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        float grow = 1f + (UiScaling.TextScale - 1f) / 2f;
        ClientSize = new Size((int)Math.Round(500 * grow), (int)Math.Round(345 * grow));

        var title = new Label
        {
            Text = $"DLS {update.TargetFullRelease.Version} is available",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10)
        };

        var notes = new TextBox
        {
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            // A multi-line TextBox only breaks lines at CRLF; release notes arrive
            // with bare LFs and otherwise show as one run-on paragraph.
            Text = string.IsNullOrWhiteSpace(update.TargetFullRelease.NotesMarkdown)
                ? "A new version is ready to install."
                : update.TargetFullRelease.NotesMarkdown.ReplaceLineEndings("\r\n"),
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 10)
        };

        _status = new Label
        {
            Text = "Ready to download",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        };

        _progress = new ProgressBar
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 0, 12, 0)
        };

        _installButton = new Button
        {
            Text = "Install and restart",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 2, 10, 2),
            Margin = new Padding(0, 0, 6, 0)
        };
        _installButton.Click += InstallClicked;

        var laterButton = new Button
        {
            Text = "Later",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 2, 10, 2),
            Margin = new Padding(0),
            MinimumSize = new Size(75, 0)
        };
        laterButton.Click += (_, _) => Close();

        // Progress takes whatever the buttons leave; the buttons take what their
        // labels need.
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0)
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.Controls.Add(_progress, 0, 0);
        actions.Controls.Add(_installButton, 1, 0);
        actions.Controls.Add(laterButton, 2, 0);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(title, 0, 0);
        root.Controls.Add(notes, 0, 1);
        root.Controls.Add(_status, 0, 2);
        root.Controls.Add(actions, 0, 3);
        Controls.Add(root);
        ResumeLayout(performLayout: true);

        // Otherwise the notes box takes focus and opens with all of its text selected.
        ActiveControl = _installButton;
        AcceptButton = _installButton;
        CancelButton = laterButton;
        FormClosing += (_, _) => _closing.Cancel();
    }

    private async void InstallClicked(object? sender, EventArgs e)
    {
        _installButton.Enabled = false;
        try
        {
            if (!_downloaded)
            {
                _status.Text = "Downloading update...";
                await _manager.DownloadUpdatesAsync(_update, percent =>
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        try
                        {
                            BeginInvoke(() =>
                            {
                                if (!IsDisposed) _progress.Value = Math.Clamp(percent, 0, 100);
                            });
                        }
                        catch (InvalidOperationException) { }
                    }
                }, _closing.Token);
                _downloaded = true;
            }

            if (_closing.IsCancellationRequested || IsDisposed) return;
            _status.Text = "Installing update...";
            if (!_apply(_update.TargetFullRelease))
            {
                _status.Text = "Update is ready. Finish the current display change, then try again.";
                _installButton.Enabled = true;
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // Closing the dialog while downloading means "Later".
        }
        catch (Exception ex)
        {
            if (_closing.IsCancellationRequested || IsDisposed) return;
            _status.Text = "The update could not be installed.";
            _installButton.Enabled = true;
            MessageBox.Show(this, ex.Message, "DLS update failed", MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
