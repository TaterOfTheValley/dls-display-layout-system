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

        Text = $"Update {AppInfo.Name}";
        Icon = AppIcon.Shared;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(500, 345);

        var title = new Label
        {
            Text = $"DLS {update.TargetFullRelease.Version} is available",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, 16)
        };
        Controls.Add(title);

        var notes = new TextBox
        {
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Text = string.IsNullOrWhiteSpace(update.TargetFullRelease.NotesMarkdown)
                ? "A new version is ready to install."
                : update.TargetFullRelease.NotesMarkdown,
            Location = new Point(16, 48),
            Size = new Size(468, 218),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
        };
        Controls.Add(notes);

        _status = new Label
        {
            Text = "Ready to download",
            AutoSize = true,
            Location = new Point(16, 277)
        };
        Controls.Add(_status);

        _progress = new ProgressBar
        {
            Location = new Point(16, 298),
            Size = new Size(270, 22)
        };
        Controls.Add(_progress);

        _installButton = new Button
        {
            Text = "Install and restart",
            Location = new Point(302, 296),
            Size = new Size(119, 27)
        };
        _installButton.Click += InstallClicked;
        Controls.Add(_installButton);

        var laterButton = new Button
        {
            Text = "Later",
            Location = new Point(427, 296),
            Size = new Size(57, 27)
        };
        laterButton.Click += (_, _) => Close();
        Controls.Add(laterButton);
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
