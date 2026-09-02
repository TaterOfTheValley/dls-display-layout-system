namespace DLS;

internal static class UiTheme
{
    public static readonly Color Bg = Color.FromArgb(14, 13, 11);
    public static readonly Color Panel = Color.FromArgb(22, 20, 17);
    public static readonly Color Card = Color.FromArgb(28, 25, 21);
    public static readonly Color CardActive = Color.FromArgb(46, 38, 25);
    public static readonly Color CardHover = Color.FromArgb(38, 33, 27);
    public static readonly Color Input = Color.FromArgb(18, 16, 14);
    public static readonly Color Gold = Color.FromArgb(232, 189, 99);
    public static readonly Color GoldHover = Color.FromArgb(248, 215, 135);
    public static readonly Color GoldDim = Color.FromArgb(141, 109, 50);
    public static readonly Color Muted = Color.FromArgb(170, 160, 140);
    public static readonly Color Line = Color.FromArgb(58, 50, 40);
    public static readonly Color Text = Color.FromArgb(245, 240, 230);
    public static readonly Color Ink = Color.FromArgb(14, 13, 11);
    public static readonly Color Danger = Color.FromArgb(232, 131, 117);
    public static readonly Color Ok = Color.FromArgb(139, 213, 160);

    public static Button MakeButton(string text, bool primary, float dpiScale = 1f)
    {
        var btn = new Button
        {
            Text = text,
            UseMnemonic = false,
            BackColor = primary ? Gold : Card,
            ForeColor = primary ? Ink : Text,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f * dpiScale, primary ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel),
            Cursor = Cursors.Hand,
            Height = (int)Math.Round(36 * dpiScale)
        };
        btn.FlatAppearance.BorderSize = primary ? 0 : 1;
        btn.FlatAppearance.BorderColor = Line;
        btn.FlatAppearance.MouseOverBackColor = primary ? GoldHover : CardHover;
        btn.EnabledChanged += (_, _) =>
        {
            btn.BackColor = btn.Enabled ? (primary ? Gold : Card) : Card;
            btn.ForeColor = btn.Enabled ? (primary ? Ink : Text) : Muted;
        };
        return btn;
    }

    public static Label MakeEyebrow(string text, float dpiScale = 1f) => new()
    {
        Text = text,
        ForeColor = Muted,
        Font = new Font("Segoe UI", 8.5f * dpiScale, FontStyle.Bold, GraphicsUnit.Pixel),
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft
    };
}
