using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MonitorLayoutSwitcher;

public class ConfigForm : Form
{
    private readonly List<DisplayProfile> _profiles;
    private readonly Action _onSaveCallback;
    private DisplayProfile? _selectedProfile;

    // UI Controls
    private readonly FlowLayoutPanel _profileCardsPanel;
    private readonly TextBox _nameTextBox;
    private readonly TextBox _hotkeyTextBox;
    private readonly Button _captureHotkeyBtn;
    private readonly Label _hotkeyStatusLabel;
    private readonly FlowLayoutPanel _displayTogglesPanel;
    private readonly Button _captureCurrentLayoutBtn;
    private readonly Button _testApplyBtn;
    private readonly Button _saveBtn;
    private readonly Button _cancelBtn;
    private readonly Button _addProfileBtn;
    private readonly Button _deleteProfileBtn;
    private readonly Label _feedbackLabel;

    private bool _isCapturingHotkey = false;

    // Dark luxury theme palette
    private static readonly Color BgColor = Color.FromArgb(14, 13, 11);
    private static readonly Color PanelColor = Color.FromArgb(22, 20, 17);
    private static readonly Color CardBg = Color.FromArgb(28, 25, 21);
    private static readonly Color CardActiveBg = Color.FromArgb(46, 38, 25);
    private static readonly Color InputBg = Color.FromArgb(18, 16, 14);
    private static readonly Color GoldColor = Color.FromArgb(232, 189, 99);
    private static readonly Color GoldHover = Color.FromArgb(248, 215, 135);
    private static readonly Color MutedColor = Color.FromArgb(170, 160, 140);
    private static readonly Color LineColor = Color.FromArgb(58, 50, 40);
    private static readonly Color TextMain = Color.FromArgb(245, 240, 230);

    public ConfigForm(List<DisplayProfile> profiles, Action onSaveCallback)
    {
        _profiles = profiles;
        _onSaveCallback = onSaveCallback;

        // AutoScaleMode.Dpi with dynamic scaling factor
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;

        float dpiScale = DeviceDpi / 96f;
        int S(int val) => (int)Math.Round(val * dpiScale);

        ClientSize = new Size(S(1020), S(680));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgColor;
        ForeColor = TextMain;
        Font = new Font("Segoe UI", 9.5f * dpiScale, FontStyle.Regular, GraphicsUnit.Pixel);
        DoubleBuffered = true;

        // ------------------ 1. HEADER ------------------
        var headerPanel = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(S(1020), S(75)),
            BackColor = PanelColor
        };
        headerPanel.Paint += (s, e) =>
        {
            using var pen = new Pen(LineColor, 1);
            e.Graphics.DrawLine(pen, 0, headerPanel.Height - 1, headerPanel.Width, headerPanel.Height - 1);
        };

        var titleLabel = new Label
        {
            Text = "MONITOR LAYOUT SWITCHER",
            Font = new Font("Segoe UI", 12f * dpiScale, FontStyle.Bold, GraphicsUnit.Pixel),
            ForeColor = GoldColor,
            Location = new Point(S(28), S(12)),
            Size = new Size(S(500), S(26)),
            AutoSize = false
        };

        var subtitleLabel = new Label
        {
            Text = "Configure monitor display arrangements and global shortcuts for instant layout switching",
            Font = new Font("Segoe UI", 8.5f * dpiScale, FontStyle.Regular, GraphicsUnit.Pixel),
            ForeColor = MutedColor,
            Location = new Point(S(28), S(40)),
            Size = new Size(S(800), S(22)),
            AutoSize = false
        };

        headerPanel.Controls.Add(titleLabel);
        headerPanel.Controls.Add(subtitleLabel);

        // ------------------ 2. LEFT SIDEBAR: PROFILES ------------------
        var sidebarGroup = new GroupBox
        {
            Text = " PROFILES ",
            Location = new Point(S(28), S(90)),
            Size = new Size(S(280), S(490)),
            ForeColor = GoldColor,
            Font = new Font("Segoe UI", 9f * dpiScale, FontStyle.Bold, GraphicsUnit.Pixel),
            BackColor = PanelColor
        };

        _profileCardsPanel = new FlowLayoutPanel
        {
            Location = new Point(S(14), S(28)),
            Size = new Size(S(252), S(400)),
            BackColor = BgColor,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(S(4))
        };

        _addProfileBtn = CreateButton("+ Add New", S(122), S(38), false, dpiScale);
        _addProfileBtn.Location = new Point(S(14), S(438));
        _addProfileBtn.Click += AddProfileBtn_Click;

        _deleteProfileBtn = CreateButton("Delete", S(122), S(38), false, dpiScale);
        _deleteProfileBtn.Location = new Point(S(144), S(438));
        _deleteProfileBtn.Click += DeleteProfileBtn_Click;

        sidebarGroup.Controls.Add(_profileCardsPanel);
        sidebarGroup.Controls.Add(_addProfileBtn);
        sidebarGroup.Controls.Add(_deleteProfileBtn);

        // ------------------ 3. RIGHT EDITOR: SETTINGS ------------------
        var editorGroup = new GroupBox
        {
            Text = " PROFILE SETTINGS ",
            Location = new Point(S(325), S(90)),
            Size = new Size(S(665), S(490)),
            ForeColor = GoldColor,
            Font = new Font("Segoe UI", 9f * dpiScale, FontStyle.Bold, GraphicsUnit.Pixel),
            BackColor = PanelColor
        };

        // Field 1: Name
        var lblName = new Label
        {
            Text = "PROFILE NAME",
            ForeColor = MutedColor,
            Font = new Font("Segoe UI", 8.5f * dpiScale, FontStyle.Bold, GraphicsUnit.Pixel),
            Location = new Point(S(24), S(28)),
            Size = new Size(S(300), S(20)),
            AutoSize = false
        };
        _nameTextBox = new TextBox
        {
            Location = new Point(S(24), S(50)),
            Size = new Size(S(615), S(30)),
            BackColor = InputBg,
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10f * dpiScale, FontStyle.Regular, GraphicsUnit.Pixel)
        };
        _nameTextBox.TextChanged += (s, e) =>
        {
            if (_selectedProfile != null)
            {
                _selectedProfile.Name = _nameTextBox.Text;
                RefreshProfileCards(dpiScale);
            }
        };

        // Field 2: Hotkey
        var lblHotkey = new Label
        {
            Text = "GLOBAL ACTIVATION SHORTCUT",
            ForeColor = MutedColor,
            Font = new Font("Segoe UI", 8.5f * dpiScale, FontStyle.Bold, GraphicsUnit.Pixel),
            Location = new Point(S(24), S(90)),
            Size = new Size(S(300), S(20)),
            AutoSize = false
        };

        _hotkeyTextBox = new TextBox
        {
            Location = new Point(S(24), S(112)),
            Size = new Size(S(425), S(32)),
            BackColor = InputBg,
            ForeColor = GoldColor,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 11f * dpiScale, FontStyle.Bold, GraphicsUnit.Pixel)
        };
        _hotkeyTextBox.KeyDown += HotkeyTextBox_KeyDown;

        _captureHotkeyBtn = CreateButton("Capture Hotkey", S(175), S(32), false, dpiScale);
        _captureHotkeyBtn.Location = new Point(S(464), S(112));
        _captureHotkeyBtn.Click += CaptureHotkeyBtn_Click;

        _hotkeyStatusLabel = new Label
        {
            Text = "Click 'Capture Hotkey' then press key combination (e.g. Ctrl + Alt + 1)",
            ForeColor = MutedColor,
            Font = new Font("Segoe UI", 8f * dpiScale, FontStyle.Regular, GraphicsUnit.Pixel),
            Location = new Point(S(24), S(148)),
            Size = new Size(S(615), S(20)),
            AutoSize = false
        };

        // Field 3: Displays Toggles
        var lblDisplays = new Label
        {
            Text = "ACTIVE MONITORS IN THIS PROFILE",
            ForeColor = MutedColor,
            Font = new Font("Segoe UI", 8.5f * dpiScale, FontStyle.Bold, GraphicsUnit.Pixel),
            Location = new Point(S(24), S(178)),
            Size = new Size(S(400), S(20)),
            AutoSize = false
        };

        _displayTogglesPanel = new FlowLayoutPanel
        {
            Location = new Point(S(24), S(200)),
            Size = new Size(S(615), S(215)),
            BackColor = InputBg,
            BorderStyle = BorderStyle.FixedSingle,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(S(6))
        };

        // Field 4: Action Buttons
        _captureCurrentLayoutBtn = CreateButton("Capture Current Screen Layout", S(270), S(38), false, dpiScale);
        _captureCurrentLayoutBtn.Location = new Point(S(24), S(430));
        _captureCurrentLayoutBtn.Click += CaptureCurrentLayoutBtn_Click;

        _testApplyBtn = CreateButton("Test Apply Layout", S(180), S(38), false, dpiScale);
        _testApplyBtn.Location = new Point(S(306), S(430));
        _testApplyBtn.Click += TestApplyBtn_Click;

        editorGroup.Controls.Add(lblName);
        editorGroup.Controls.Add(_nameTextBox);
        editorGroup.Controls.Add(lblHotkey);
        editorGroup.Controls.Add(_hotkeyTextBox);
        editorGroup.Controls.Add(_captureHotkeyBtn);
        editorGroup.Controls.Add(_hotkeyStatusLabel);
        editorGroup.Controls.Add(lblDisplays);
        editorGroup.Controls.Add(_displayTogglesPanel);
        editorGroup.Controls.Add(_captureCurrentLayoutBtn);
        editorGroup.Controls.Add(_testApplyBtn);

        // ------------------ 4. FOOTER ------------------
        var footerPanel = new Panel
        {
            Location = new Point(0, S(595)),
            Size = new Size(S(1020), S(85)),
            BackColor = PanelColor
        };
        footerPanel.Paint += (s, e) =>
        {
            using var pen = new Pen(LineColor, 1);
            e.Graphics.DrawLine(pen, 0, 0, footerPanel.Width, 0);
        };

        _feedbackLabel = new Label
        {
            Location = new Point(S(28), S(24)),
            Size = new Size(S(610), S(32)),
            ForeColor = GoldColor,
            Font = new Font("Segoe UI", 9.5f * dpiScale, FontStyle.Italic, GraphicsUnit.Pixel),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            AutoSize = false
        };

        _cancelBtn = CreateButton("Cancel", S(140), S(42), false, dpiScale);
        _cancelBtn.Location = new Point(S(670), S(20));
        _cancelBtn.Click += (s, e) => Close();

        _saveBtn = CreateButton("Save & Apply", S(165), S(42), true, dpiScale);
        _saveBtn.Location = new Point(S(825), S(20));
        _saveBtn.Click += SaveBtn_Click;

        footerPanel.Controls.Add(_feedbackLabel);
        footerPanel.Controls.Add(_cancelBtn);
        footerPanel.Controls.Add(_saveBtn);

        // Add all top-level sections
        Controls.Add(headerPanel);
        Controls.Add(sidebarGroup);
        Controls.Add(editorGroup);
        Controls.Add(footerPanel);

        if (_profiles.Count > 0)
        {
            _selectedProfile = _profiles[0];
        }

        RefreshProfileCards(dpiScale);
        LoadSelectedProfileIntoEditor(dpiScale);
    }

    private Button CreateButton(string text, int width, int height, bool isPrimary, float dpiScale)
    {
        var btn = new Button
        {
            Text = text,
            UseMnemonic = false,
            Size = new Size(width, height),
            BackColor = isPrimary ? GoldColor : CardBg,
            ForeColor = isPrimary ? Color.FromArgb(14, 13, 11) : TextMain,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f * dpiScale, isPrimary ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = isPrimary ? 0 : 1;
        btn.FlatAppearance.BorderColor = LineColor;
        btn.FlatAppearance.MouseOverBackColor = isPrimary ? GoldHover : Color.FromArgb(50, 44, 36);
        return btn;
    }

    private void RefreshProfileCards(float dpiScale = 1.0f)
    {
        if (dpiScale <= 0.01f) dpiScale = DeviceDpi / 96f;
        int S(int val) => (int)Math.Round(val * dpiScale);

        _profileCardsPanel.SuspendLayout();
        _profileCardsPanel.Controls.Clear();

        foreach (var profile in _profiles)
        {
            bool isSelected = (_selectedProfile?.Id == profile.Id);

            var card = new Panel
            {
                Size = new Size(S(224), S(64)),
                BackColor = isSelected ? CardActiveBg : CardBg,
                Margin = new Padding(0, 0, 0, S(6)),
                Cursor = Cursors.Hand,
                Padding = new Padding(S(10), S(8), S(10), S(8))
            };

            var p = profile;
            card.Paint += (s, e) =>
            {
                using var pen = new Pen(isSelected ? GoldColor : LineColor, isSelected ? 2 : 1);
                e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            var lblTitle = new Label
            {
                Text = p.Name,
                Font = new Font("Segoe UI", 10f * dpiScale, FontStyle.Bold, GraphicsUnit.Pixel),
                ForeColor = isSelected ? GoldColor : TextMain,
                Location = new Point(S(10), S(8)),
                Size = new Size(S(200), S(22)),
                AutoSize = false
            };

            string hotkeyText = string.IsNullOrWhiteSpace(p.Hotkey) ? "No shortcut set" : p.Hotkey;
            var lblSub = new Label
            {
                Text = hotkeyText,
                Font = new Font("Segoe UI", 8.5f * dpiScale, FontStyle.Regular, GraphicsUnit.Pixel),
                ForeColor = MutedColor,
                Location = new Point(S(10), S(34)),
                Size = new Size(S(200), S(20)),
                AutoSize = false
            };

            void SelectThisCard(object? sender, EventArgs e)
            {
                _selectedProfile = p;
                RefreshProfileCards(dpiScale);
                LoadSelectedProfileIntoEditor(dpiScale);
            }

            card.Click += SelectThisCard;
            lblTitle.Click += SelectThisCard;
            lblSub.Click += SelectThisCard;

            card.Controls.Add(lblTitle);
            card.Controls.Add(lblSub);

            _profileCardsPanel.Controls.Add(card);
        }

        _profileCardsPanel.ResumeLayout();
    }

    private void LoadSelectedProfileIntoEditor(float dpiScale = 1.0f)
    {
        if (dpiScale <= 0.01f) dpiScale = DeviceDpi / 96f;
        int S(int val) => (int)Math.Round(val * dpiScale);

        if (_selectedProfile == null) return;

        _nameTextBox.Text = _selectedProfile.Name;
        _hotkeyTextBox.Text = _selectedProfile.Hotkey;

        _displayTogglesPanel.SuspendLayout();
        _displayTogglesPanel.Controls.Clear();

        var currentDisplays = DisplayEngine.GetCurrentDisplays();

        foreach (var disp in currentDisplays)
        {
            var target = _selectedProfile.Displays.FirstOrDefault(d =>
                string.Equals(d.DeviceName, disp.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.RelativePosition, disp.RelativePosition, StringComparison.OrdinalIgnoreCase));

            bool isChecked = target?.Enabled ?? disp.IsAttached;

            var toggleRow = new CheckBox
            {
                Text = $"  {disp.RelativePosition} [{disp.DeviceName}] — {disp.MonitorId} ({disp.Width} × {disp.Height})",
                Checked = isChecked,
                Font = new Font("Segoe UI", 9.5f * dpiScale, FontStyle.Regular, GraphicsUnit.Pixel),
                ForeColor = TextMain,
                BackColor = InputBg,
                AutoSize = false,
                Size = new Size(S(585), S(42)),
                Margin = new Padding(0, 0, 0, S(4)),
                Padding = new Padding(S(8), 0, 0, 0),
                Cursor = Cursors.Hand
            };

            var d = disp;
            toggleRow.CheckedChanged += (s, e) =>
            {
                if (_selectedProfile == null) return;
                var matched = _selectedProfile.Displays.FirstOrDefault(x =>
                    string.Equals(x.DeviceName, d.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.RelativePosition, d.RelativePosition, StringComparison.OrdinalIgnoreCase));

                if (matched != null)
                {
                    matched.Enabled = toggleRow.Checked;
                }
                else
                {
                    _selectedProfile.Displays.Add(new DisplayTargetConfig
                    {
                        DeviceName = d.DeviceName,
                        MonitorId = d.MonitorId,
                        RelativePosition = d.RelativePosition,
                        Enabled = toggleRow.Checked,
                        X = d.X,
                        Y = d.Y,
                        Width = d.Width,
                        Height = d.Height,
                        RefreshRate = d.RefreshRate,
                        IsPrimary = d.IsPrimary
                    });
                }
            };

            _displayTogglesPanel.Controls.Add(toggleRow);
        }

        _displayTogglesPanel.ResumeLayout();
    }

    private void CaptureHotkeyBtn_Click(object? sender, EventArgs e)
    {
        _isCapturingHotkey = true;
        _hotkeyTextBox.Focus();
        _hotkeyStatusLabel.Text = "Listening... Press any key combination (Ctrl, Alt, Shift, Win + Key)";
        _hotkeyStatusLabel.ForeColor = GoldColor;
    }

    private void HotkeyTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isCapturingHotkey) return;

        if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.Menu || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin)
        {
            return;
        }

        var parts = new List<string>();
        if (e.Control) parts.Add("Ctrl");
        if (e.Alt) parts.Add("Alt");
        if (e.Shift) parts.Add("Shift");
        parts.Add(e.KeyCode.ToString());

        string captured = string.Join(" + ", parts);
        _hotkeyTextBox.Text = captured;
        if (_selectedProfile != null)
        {
            _selectedProfile.Hotkey = captured;
            RefreshProfileCards();
        }

        _isCapturingHotkey = false;
        _hotkeyStatusLabel.Text = $"Captured shortcut: {captured}";
        _hotkeyStatusLabel.ForeColor = Color.LightGreen;
        e.SuppressKeyPress = true;
    }

    private void AddProfileBtn_Click(object? sender, EventArgs e)
    {
        var newProfile = DisplayEngine.CaptureCurrentLayoutAsProfile($"Profile {_profiles.Count + 1}", string.Empty);
        _profiles.Add(newProfile);
        _selectedProfile = newProfile;
        RefreshProfileCards();
        LoadSelectedProfileIntoEditor();
        _feedbackLabel.Text = $"Created new profile '{newProfile.Name}'";
    }

    private void DeleteProfileBtn_Click(object? sender, EventArgs e)
    {
        if (_selectedProfile != null && _profiles.Count > 1)
        {
            string name = _selectedProfile.Name;
            _profiles.Remove(_selectedProfile);
            _selectedProfile = _profiles[0];
            RefreshProfileCards();
            LoadSelectedProfileIntoEditor();
            _feedbackLabel.Text = $"Deleted profile '{name}'";
        }
        else
        {
            _feedbackLabel.Text = "Cannot delete the only profile.";
        }
    }

    private void CaptureCurrentLayoutBtn_Click(object? sender, EventArgs e)
    {
        if (_selectedProfile == null) return;
        var captured = DisplayEngine.CaptureCurrentLayoutAsProfile(_selectedProfile.Name, _selectedProfile.Hotkey);
        _selectedProfile.Displays = captured.Displays;
        LoadSelectedProfileIntoEditor();
        _feedbackLabel.Text = $"Successfully captured current screen layout into '{_selectedProfile.Name}'!";
    }

    private void TestApplyBtn_Click(object? sender, EventArgs e)
    {
        if (_selectedProfile == null) return;
        bool ok = DisplayEngine.ApplyProfile(_selectedProfile, out string err);
        _feedbackLabel.Text = ok ? $"Applied '{_selectedProfile.Name}' successfully!" : $"Error: {err}";
    }

    private void SaveBtn_Click(object? sender, EventArgs e)
    {
        ProfileManager.SaveProfiles(_profiles);
        _onSaveCallback?.Invoke();
        _feedbackLabel.Text = "Saved profiles and re-registered hotkeys.";
        Close();
    }
}
