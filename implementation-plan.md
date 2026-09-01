# Monitor Layout Switcher — Implementation Plan

## Goal
A native, lightweight Windows 11 system tray utility written in C# (.NET 8 Windows Forms/WPF/Win32) allowing instantaneous switching of monitor configurations between saved profiles ("Desk / all 3" vs "Share / left only") via configurable global hotkeys and a tray icon.

## Technical Approach

### 1. Windows Display Topology Engine (Win32 CCD APIs)
- Use Windows Connecting and Configuring Displays (CCD) APIs (`QueryDisplayConfig`, `SetDisplayConfig`, `DisplayConfigGetDeviceInfo`).
- CCD natively supports saving and restoring exact monitor clone/extend topologies, position coordinates `(X, Y)`, resolutions, refresh rates, and primary display flags, keyed by persistent GDI device names and EDID target IDs.
- Secondary fallback: `ChangeDisplaySettingsEx` / `EnumDisplayDevices` for GDI position coordinates and attachment flags (`CDS_UPDATEREGISTRY`).

### 2. Profile Storage
- JSON configuration file saved as `profiles.json` next to the executable for the current portable build.
- A debounced file watcher reloads external profile edits and prompts before replacing unsaved editor changes.
- Profile structure:
  - `Id`: string
  - `Name`: string ("Desk / all three", "Share / left only")
  - `Hotkey`: key combination string (e.g. `Ctrl+Alt+1`)
  - `Displays`: list of display descriptors (EDID ID / Device Path / Target ID, Position `(X, Y)`, Resolution `(W, H)`, RefreshRate, IsPrimary, IsActive).

### 3. User Interface
- Lightweight Windows system tray icon (`NotifyIcon`) with dark context menu.
- Quick tray popover / menu showing current active profile and quick switch commands.
- Interactive Canvas & Drag-and-Drop Profile Configuration Window:
  - Responsive High-DPI scaling (`DeviceDpi / 96f`) across all controls, fonts, and canvas elements.
  - Strictly filter active desktop monitors (`DISPLAY_DEVICE_ATTACHED_TO_DESKTOP`), discarding inactive adapter ports.
  - Interactive 2D drag-and-drop monitor placement with magnetic edge snapping.
  - Visual shelf for unused/disconnected monitors in each profile.
  - On-screen display identification overlay (`Identify`).
  - Profile list (+ Add, Delete, Rename).
  - Configurable hotkey input with key capture (`RegisterHotKey`).
  - "Capture Current Layout" button to save current running layout into a profile.
  - "Apply Layout" test button.

### 4. Hotkey System
- `RegisterHotKey` / `UnregisterHotKey` with a dedicated hidden message window (`NativeWindow`).
- Real-time hotkey updates when edited in configuration window.

## Project Structure inside `monitor-layout-switcher/`:
- `monitor-layout-switcher.md` — Project overview
- `implementation-plan.md` — Technical plan
- `tasks.md` — Development task checklist
- `src/` — .NET 8 C# project (`MonitorLayoutSwitcher.csproj`, `Program.cs`, `DisplayEngine.cs`, `ProfileManager.cs`, `HotkeyManager.cs`, `TrayContext.cs`, `ConfigForm.cs`)
- `build.cmd` — Quick compilation script
- `run.cmd` — Launch script
