using System.Drawing.Drawing2D;
using Velopack;

namespace DLS;

public class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Control _uiDispatcher = new();
    private List<TrayPopup.Entry> _entries = new();
    private readonly HotkeyManager _hotkeyManager;
    private readonly FileSystemWatcher _profileWatcher;
    private readonly System.Windows.Forms.Timer _profileReloadTimer;
    private readonly System.Windows.Forms.Timer _hotkeyRecoveryTimer;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private readonly UpdateManager _updateManager = UpdateService.CreateManager();
    private List<DisplayProfile> _profiles;
    private DisplayProfile? _activeProfile;
    private ConfigForm? _configForm;
    private UpdateForm? _updateForm;
    private UpdateInfo? _availableUpdate;
    private bool _checkingUpdates;
    private bool _profileChangePending;
    private string _lastHandledProfileSignature = string.Empty;
    private bool _reloadingProfiles;
    private int _hotkeyRecoveryAttempt;

    private const int MaxHotkeyRecoveryAttempts = 3;

    public TrayContext()
    {
        // Keep a UI-thread handle alive even after the editor is closed. SystemEvents
        // callbacks arrive off-thread, and must be marshalled before touching the
        // tray context or its hotkey registrations.
        _ = _uiDispatcher.Handle;

        _profiles = ProfileManager.LoadProfiles();
        _hotkeyManager = new HotkeyManager();

        _notifyIcon = new NotifyIcon
        {
            Icon = AppIcon.Shared,
            Text = AppInfo.Name,
            Visible = true
        };

        _notifyIcon.DoubleClick += (s, e) => ShowConfigWindow();

        // The popup is a window we own, so it opens on the click rather than being
        // handed to NotifyIcon. Cursor position is the anchor, which lands correctly
        // whichever edge the taskbar is on.
        _notifyIcon.MouseUp += (s, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            RefreshMenuAndHotkeys();
            TrayPopup.Show(_entries, Cursor.Position, TrayScale());
        };

        _lastHandledProfileSignature = GetProfileSignature();
        // Watch wherever settings actually live now, not the executable's folder.
        _profileWatcher = new FileSystemWatcher(
            AppPaths.SettingsDirectory, Path.GetFileName(AppPaths.SettingsFile))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _profileWatcher.Changed += ProfileFileChanged;
        _profileWatcher.Created += ProfileFileChanged;
        _profileWatcher.Renamed += ProfileFileChanged;

        _profileReloadTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _profileReloadTimer.Tick += (_, _) =>
        {
            if (!_profileChangePending) return;
            _profileChangePending = false;
            ReloadProfilesFromDisk();
        };
        _profileReloadTimer.Start();

        _hotkeyRecoveryTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _hotkeyRecoveryTimer.Tick += (_, _) => TryRecoverHotkeys();

        // The first check waits until startup has finished. Subsequent checks are
        // infrequent, both for the user's sake and GitHub's anonymous API limit.
        _updateTimer = new System.Windows.Forms.Timer { Interval = 15000 };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = 12 * 60 * 60 * 1000;
            await CheckForUpdatesAsync(manual: false);
        };
        if (_updateManager.IsInstalled) _updateTimer.Start();

        RefreshMenuAndHotkeys();
        DetectActiveProfile();

        // The layout can change without this app doing it — Windows display settings,
        // a monitor plugged or unplugged, a driver event. Without this the tray shows
        // a checkmark against a profile that is no longer live.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;

        // Anything the settings load wants the user to know — an upgraded file format,
        // or a file written by a newer build that was backed up before use.
        if (!string.IsNullOrWhiteSpace(ProfileManager.LastNotice))
        {
            _notifyIcon.ShowBalloonTip(6000, AppInfo.Name, ProfileManager.LastNotice, ToolTipIcon.Info);
        }

        LayoutSafety.UndoStateChanged += (_, _) =>
        {
            if (_notifyIcon.Visible) BeginInvokeOnUi(RefreshMenuAndHotkeys);
        };
        ShowConfigWindow();
    }

    private void BeginInvokeOnUi(Action action)
    {
        if (_uiDispatcher.IsDisposed) return;
        if (!_uiDispatcher.InvokeRequired)
        {
            action();
            return;
        }

        try { _uiDispatcher.BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private void ProfileFileChanged(object? sender, FileSystemEventArgs e)
    {
        _profileChangePending = true;
    }

    private string GetProfileSignature()
    {
        try
        {
            if (!File.Exists(ProfileManager.ConfigPath)) return string.Empty;
            var info = new FileInfo(ProfileManager.ConfigPath);
            return $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
        }
        catch
        {
            return string.Empty;
        }
    }

    private void MarkProfileFileHandled() =>
        _lastHandledProfileSignature = GetProfileSignature();

    private void ReloadProfilesFromDisk()
    {
        if (_reloadingProfiles) return;
        string signature = GetProfileSignature();
        if (string.IsNullOrEmpty(signature) || signature == _lastHandledProfileSignature) return;

        _reloadingProfiles = true;
        var loaded = ProfileManager.LoadProfiles();
        if (loaded.Count == 0)
        {
            _reloadingProfiles = false;
            return;
        }

        // Edits save themselves, so "unsaved editor changes" now only means edits
        // still inside the debounce window. Write them out and re-check rather than
        // interrupting with a dialog — if that write is what the watcher saw, there
        // is nothing to reload at all.
        if (_configForm is { IsDisposed: false } && _configForm.HasUnsavedChanges)
        {
            _configForm.FlushPendingEdits();
            if (GetProfileSignature() == _lastHandledProfileSignature)
            {
                _reloadingProfiles = false;
                return;
            }
        }

        _profiles.Clear();
        _profiles.AddRange(loaded);
        _configForm?.ReloadProfilesFromDisk(_profiles);
        _lastHandledProfileSignature = signature;
        DetectActiveProfile();
        RefreshMenuAndHotkeys();
        _reloadingProfiles = false;
    }

    /// <summary>
    /// The DPI of the screen the tray is on. Read per-open rather than cached: the
    /// taskbar can sit on a display with a different scale factor than the one the
    /// app started on.
    /// </summary>
    private static float TrayScale()
    {
        var screen = Screen.FromPoint(Cursor.Position);
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        float dpi = g.DpiX <= 0 ? 96f : g.DpiX;
        _ = screen;
        return dpi / 96f;
    }

    /// <summary>
    /// Re-registers global hotkeys. Split out from the menu rebuild because it needs
    /// no display query: an editor autosave has to refresh hotkeys, and doing it
    /// through the full menu rebuild meant enumerating every display path each time
    /// the user paused typing.
    /// </summary>
    private bool RegisterHotkeys()
    {
        _hotkeyManager.UnregisterAll();
        bool allRegistered = true;
        foreach (var profile in _profiles)
        {
            var p = profile;
            if (p.NeedsRecapture || string.IsNullOrWhiteSpace(p.Hotkey)) continue;
            if (!_hotkeyManager.Register(p.Hotkey, () => SwitchToProfile(p)))
                allRegistered = false;
        }
        return allRegistered;
    }

    private void RefreshHotkeys()
    {
        if (RegisterHotkeys())
        {
            _hotkeyRecoveryTimer.Stop();
            _hotkeyRecoveryAttempt = 0;
        }
        else
        {
            ScheduleHotkeyRecovery();
        }
    }

    private void ScheduleHotkeyRecovery()
    {
        _hotkeyRecoveryAttempt = 0;
        _hotkeyRecoveryTimer.Stop();
        _hotkeyRecoveryTimer.Interval = 1000;
        _hotkeyRecoveryTimer.Start();
    }

    private void TryRecoverHotkeys()
    {
        _hotkeyRecoveryTimer.Stop();
        if (RegisterHotkeys())
        {
            _hotkeyRecoveryAttempt = 0;
            return;
        }

        _hotkeyRecoveryAttempt++;
        if (_hotkeyRecoveryAttempt >= MaxHotkeyRecoveryAttempts) return;

        _hotkeyRecoveryTimer.Interval = _hotkeyRecoveryAttempt == 1 ? 1500 : 4000;
        _hotkeyRecoveryTimer.Start();
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            BeginInvokeOnUi(ScheduleHotkeyRecovery);
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock
            or Microsoft.Win32.SessionSwitchReason.ConsoleConnect
            or Microsoft.Win32.SessionSwitchReason.RemoteConnect)
        {
            BeginInvokeOnUi(ScheduleHotkeyRecovery);
        }
    }

    private void RefreshMenuAndHotkeys()
    {
        RefreshHotkeys();

        var entries = new List<TrayPopup.Entry>();

        // One display query for the whole menu, not one per layout.
        var liveDisplays = DisplayEngine.GetCurrentDisplays();
        var liveMatches = _profiles.ToDictionary(p => p.Id, p => DisplayEngine.MatchesCurrent(p, liveDisplays));
        bool anyLive = liveMatches.Values.Any(v => v);

        if (_profiles.Count == 0)
        {
            entries.Add(new TrayPopup.HeadingEntry { Text = $"{AppInfo.Name} - NO LAYOUTS YET" });
            entries.Add(new TrayPopup.CommandEntry
            {
                Text = "Save your current arrangement",
                Emphasis = true,
                Invoke = CaptureCurrentLayout
            });
        }
        else
        {
            entries.Add(new TrayPopup.HeadingEntry { Text = $"{AppInfo.Name} - LAYOUTS" });
        }

        foreach (var profile in _profiles)
        {
            // Live means the desktop actually looks like this right now. _activeProfile
            // (the last one explicitly switched to) is only a fallback for when nothing
            // matches — e.g. the layout was changed outside this app.
            bool isLive = liveMatches[profile.Id] || (!anyLive && _activeProfile?.Id == profile.Id);
            var p = profile;

            entries.Add(new TrayPopup.LayoutEntry
            {
                Profile = p,
                IsLive = isLive,
                Enabled = !p.NeedsRecapture,
                Invoke = p.NeedsRecapture ? null : () => SwitchToProfile(p)
            });
        }

        entries.Add(new TrayPopup.SeparatorEntry());

        if (LayoutSafety.CanUndo)
        {
            entries.Add(new TrayPopup.CommandEntry
            {
                Text = "Undo last switch",
                Detail = $"{LayoutSafety.RemainingSeconds}s",
                Emphasis = true,
                Invoke = UndoLastSwitch
            });
        }

        if (_profiles.Count > 0)
        {
            entries.Add(new TrayPopup.CommandEntry
            {
                Text = "Save current arrangement as a layout",
                Invoke = CaptureCurrentLayout
            });
        }

        entries.Add(new TrayPopup.CommandEntry { Text = "Edit layouts\u2026", Invoke = ShowConfigWindow });
        entries.Add(new TrayPopup.SeparatorEntry());

        if (_availableUpdate != null)
        {
            entries.Add(new TrayPopup.CommandEntry
            {
                Text = $"Update to DLS {_availableUpdate.TargetFullRelease.Version}",
                Emphasis = true,
                Invoke = ShowUpdateForm
            });
        }

        entries.Add(new TrayPopup.CommandEntry
        {
            Text = "Check for updates\u2026",
            Detail = _checkingUpdates ? "checking" : null,
            Enabled = !_checkingUpdates,
            Invoke = () => _ = CheckForUpdatesAsync(manual: true)
        });

        var startup = StartupRegistration.Current;
        entries.Add(new TrayPopup.CommandEntry
        {
            Text = "Start with Windows",
            Checked = startup == StartupRegistration.State.On,
            Detail = startup == StartupRegistration.State.BlockedByWindows ? "blocked in Task Manager" : null,
            // A Task Manager "Disable" can only be undone in Task Manager — rewriting
            // the Run key would leave the tick on while Windows still refused to start
            // the app, which is worse than not offering the toggle.
            Enabled = startup != StartupRegistration.State.BlockedByWindows,
            Invoke = ToggleStartup
        });

        entries.Add(new TrayPopup.CommandEntry { Text = "Exit", Invoke = ExitThread });

        _entries = entries;
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (!_updateManager.IsInstalled)
        {
            if (manual && MessageBox.Show(
                    "This copy of DLS was run as a standalone EXE. Install DLS once with " +
                    "DLS-DisplayLayoutSystem-win-Setup.exe to enable in-app updates. Open the release page?",
                    "DLS updates", MessageBoxButtons.YesNo, MessageBoxIcon.Information)
                == DialogResult.Yes)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        UpdateService.RepositoryUrl + "/releases") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not open the release page: {ex.Message}",
                        "DLS updates", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            return;
        }

        if (_checkingUpdates) return;
        _checkingUpdates = true;
        RefreshMenuAndHotkeys();
        try
        {
            var found = await _updateManager.CheckForUpdatesAsync();
            bool newlyFound = found != null &&
                _availableUpdate?.TargetFullRelease.Version != found.TargetFullRelease.Version;
            _availableUpdate = found;
            RefreshMenuAndHotkeys();

            if (found == null)
            {
                if (manual) MessageBox.Show($"DLS {AppInfo.Version} is up to date.",
                    "DLS updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (manual)
            {
                ShowUpdateForm();
            }
            else if (newlyFound)
            {
                _notifyIcon.ShowBalloonTip(6000, "DLS update available",
                    $"Version {found.TargetFullRelease.Version} is ready. Open the DLS tray menu to install it.",
                    ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            if (manual) MessageBox.Show($"Could not check for updates: {ex.Message}",
                "DLS updates", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _checkingUpdates = false;
            RefreshMenuAndHotkeys();
        }
    }

    private void ShowUpdateForm()
    {
        if (_availableUpdate == null) return;
        if (_updateForm is { IsDisposed: false })
        {
            _updateForm.BringToFront();
            return;
        }

        _updateForm = new UpdateForm(_updateManager, _availableUpdate, ApplyUpdate);
        _updateForm.FormClosed += (_, _) => _updateForm = null;
        _updateForm.Show();
        _updateForm.BringToFront();
    }

    private bool ApplyUpdate(VelopackAsset release)
    {
        if (LayoutSafety.CanUndo)
        {
            MessageBox.Show("Keep or revert the current display change before updating.",
                "DLS updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        if (_configForm is { IsDisposed: false, HasUnsavedChanges: true })
        {
            _configForm.FlushPendingEdits();
            if (_configForm.HasUnsavedChanges)
            {
                MessageBox.Show("DLS could not save your layout edits. Resolve the save error before updating.",
                    "DLS updates", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        _updateManager.WaitExitThenApplyUpdates(release);
        ExitThread();
        return true;
    }

    private void ToggleStartup()
    {
        bool wanted = !StartupRegistration.IsEnabled;
        if (!StartupRegistration.Set(wanted, out string error))
        {
            _notifyIcon.ShowBalloonTip(4000, AppInfo.Name + " — Failed",
                $"Could not change the startup setting: {error}", ToolTipIcon.Error);
            return;
        }

        RefreshMenuAndHotkeys();
        _notifyIcon.ShowBalloonTip(2000, AppInfo.Name,
            wanted ? "Will start with Windows." : "Will no longer start with Windows.", ToolTipIcon.Info);
    }

    private void UndoLastSwitch()
    {
        bool ok = LayoutSafety.Undo(out string msg);
        if (ok)
        {
            DetectActiveProfile();
            _notifyIcon.Text = AppInfo.Name;
        }
        else
        {
            _notifyIcon.ShowBalloonTip(3000, AppInfo.Name + " — Failed", msg, ToolTipIcon.Error);
        }
        RefreshMenuAndHotkeys();
    }

    private void CaptureCurrentLayout()
    {
        var created = DisplayEngine.CaptureCurrentLayoutAsProfile($"Layout {_profiles.Count + 1}", string.Empty);
        _profiles.Add(created);
        if (!ProfileManager.TrySaveProfiles(_profiles, out string saveError))
        {
            _profiles.Remove(created);
            _notifyIcon.ShowBalloonTip(4000, AppInfo.Name + " — Failed", saveError, ToolTipIcon.Error);
            return;
        }

        MarkProfileFileHandled();
        RefreshMenuAndHotkeys();
        _notifyIcon.ShowBalloonTip(2000, AppInfo.Name, $"Saved {created.Name}", ToolTipIcon.Info);
    }

    /// <summary>Builds the tray entries for the --screenshot-menu diagnostic.</summary>
    internal static List<TrayPopup.Entry> BuildPreviewEntries(List<DisplayProfile> profiles)
    {
        var entries = new List<TrayPopup.Entry> { new TrayPopup.HeadingEntry { Text = $"{AppInfo.Name} - LAYOUTS" } };
        var live = DisplayEngine.GetCurrentDisplays();

        foreach (var p in profiles)
        {
            entries.Add(new TrayPopup.LayoutEntry
            {
                Profile = p,
                IsLive = DisplayEngine.MatchesCurrent(p, live),
                Enabled = !p.NeedsRecapture,
                Invoke = () => { }
            });
        }

        entries.Add(new TrayPopup.SeparatorEntry());
        entries.Add(new TrayPopup.CommandEntry { Text = "Undo last switch", Detail = "18s", Emphasis = true, Invoke = () => { } });
        entries.Add(new TrayPopup.CommandEntry { Text = "Save current arrangement as a layout", Invoke = () => { } });
        entries.Add(new TrayPopup.CommandEntry { Text = "Edit layouts\u2026", Invoke = () => { } });
        entries.Add(new TrayPopup.SeparatorEntry());
        entries.Add(new TrayPopup.CommandEntry
        {
            Text = "Start with Windows",
            Checked = StartupRegistration.IsEnabled,
            Invoke = () => { }
        });
        entries.Add(new TrayPopup.CommandEntry { Text = "Exit", Invoke = () => { } });
        return entries;
    }

    private void SwitchToProfile(DisplayProfile profile)
    {
        // No "this will turn off X" confirmation. The switch reverts by itself unless
        // you keep it, so a modal beforehand asked the user to predict a consequence
        // they are about to be shown directly — two confirmations for one action, and
        // a system dialog in the middle of what should be a one-keypress switch.
        // The editor dropped this already; the tray kept it, so the same action
        // behaved differently depending on where it was started.
        bool success = LayoutSafety.Apply(profile, interactive: true, out string error);
        if (success)
        {
            _activeProfile = profile;
            _notifyIcon.Text = $"{AppInfo.Name} · {profile.Name}";
            RefreshMenuAndHotkeys();
        }
        else
        {
            _notifyIcon.ShowBalloonTip(3000, AppInfo.Name + " — Failed", error, ToolTipIcon.Error);
        }
    }

    private void DetectActiveProfile()
    {
        _activeProfile = DisplayEngine.FindMatchingProfile(_profiles) ?? _activeProfile;
        if (_activeProfile != null)
        {
            _notifyIcon.Text = $"{AppInfo.Name}: {_activeProfile.Name}";
        }
    }

    private void ShowConfigWindow()
    {
        if (_configForm == null || _configForm.IsDisposed)
        {
            // Called on every autosave, so it stays cheap: hotkeys must follow an
            // edit immediately, but the menu is rebuilt when it opens anyway.
            _configForm = new ConfigForm(_profiles, () =>
            {
                RefreshHotkeys();
                MarkProfileFileHandled();
            });
        }

        _configForm.Show();
        _configForm.BringToFront();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        DisplayModes.Invalidate();
        CcdEngine.InvalidateCaches();
        BeginInvokeOnUi(() =>
        {
            RefreshMenuAndHotkeys();
            DetectActiveProfile();
        });
    }

    protected override void ExitThreadCore()
    {
        // SystemEvents holds a static, process-lifetime subscriber list; leaving this
        // attached keeps the whole TrayContext alive after exit.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;

        _profileReloadTimer.Stop();
        _profileReloadTimer.Dispose();
        _hotkeyRecoveryTimer.Stop();
        _hotkeyRecoveryTimer.Dispose();
        _updateTimer.Stop();
        _updateTimer.Dispose();
        _profileWatcher.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _hotkeyManager.Dispose();
        _uiDispatcher.Dispose();
        base.ExitThreadCore();
    }

}

public static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, int x, int y, int width, int height, int radius)
    {
        using var path = GetRoundedRectPath(x, y, width, height, radius);
        g.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics g, Pen pen, int x, int y, int width, int height, int radius)
    {
        using var path = GetRoundedRectPath(x, y, width, height, radius);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath GetRoundedRectPath(int x, int y, int width, int height, int radius)
    {
        var path = new GraphicsPath();
        if (width <= 0 || height <= 0)
        {
            path.AddRectangle(new Rectangle(x, y, Math.Max(1, width), Math.Max(1, height)));
            return path;
        }

        int d = Math.Max(2, Math.Min(radius * 2, Math.Min(width, height)));
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
