# Monitor Layout Switcher Tasks

- [x] Create implementation plan and tasks document
- [x] Inspect Windows CCD display query on CITADEL
- [x] Scaffold `MonitorLayoutSwitcher.csproj` (.NET 8 Windows Forms)
- [x] Implement `DisplayEngine.cs` using CCD / `ChangeDisplaySettingsEx`
- [x] Implement `ProfileManager.cs` for saving/loading profiles to `profiles.json`
- [x] Implement `HotkeyManager.cs` for Win32 `RegisterHotKey` global shortcuts

## Phase 2: UI & User Experience
- [x] Implement `TrayContext.cs` for tray icon, context menu, and active profile indicator
- [x] Implement `ConfigForm.cs` (compact luxury dark-themed profile & hotkey editor)
- [x] Implement "Capture Current Layout" to make creating profiles frictionless

## Phase 3: Verification & Distribution
- [x] Test layout queries and capture on current CITADEL hardware
- [x] Test switching between simulated / saved layouts
- [x] Create `build.cmd` and `run.cmd`
- [x] Verify clean execution and hotkey registration
