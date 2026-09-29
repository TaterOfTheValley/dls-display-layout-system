using System.Runtime.InteropServices;

namespace DLS;

public class HotkeyManager : IDisposable
{
    private class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private readonly Action<int> _callback;

        public HotkeyWindow(Action<int> callback)
        {
            _callback = callback;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                _callback?.Invoke(id);
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_NOREPEAT = 0x4000;

    private readonly HotkeyWindow _window;
    private readonly Dictionary<int, Action> _hotkeyActions = new();
    private int _currentId = 1;

    public HotkeyManager()
    {
        _window = new HotkeyWindow(OnHotkeyPressed);
    }

    private void OnHotkeyPressed(int id)
    {
        if (_hotkeyActions.TryGetValue(id, out var action))
        {
            action?.Invoke();
        }
    }

    /// <summary>
    /// Registers a global shortcut. Refuses one that <see cref="Hotkeys.Problem(string?)"/>
    /// rejects, whatever the caller checked: a bare Caps Lock or letter registered here
    /// would stop that key working in every program until DLS exits.
    /// </summary>
    public bool Register(string hotkeyString, Action action)
    {
        if (Hotkeys.Problem(hotkeyString) != null || !Hotkeys.TryParse(hotkeyString, out var combo)) return false;

        int id = _currentId++;
        bool success = RegisterHotKey(_window.Handle, id, combo.Modifiers | MOD_NOREPEAT, (uint)combo.Key);
        if (success)
        {
            _hotkeyActions[id] = action;
        }

        return success;
    }

    public void UnregisterAll()
    {
        foreach (var id in _hotkeyActions.Keys)
        {
            UnregisterHotKey(_window.Handle, id);
        }
        _hotkeyActions.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.Dispose();
    }
}
