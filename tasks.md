# Monitor Layout Switcher Tasks

- [x] Create implementation plan and tasks document
- [x] Inspect Windows CCD display query on CITADEL
- [x] Scaffold `MonitorLayoutSwitcher.csproj` (.NET 8 Windows Forms)
- [x] Implement `DisplayEngine.cs` using CCD / `ChangeDisplaySettingsEx`
- [x] Implement `ProfileManager.cs` for saving/loading profiles to `profiles.json`
- [x] Implement `HotkeyManager.cs` for Win32 `RegisterHotKey` global shortcuts

## Phase 2: UI & User Experience
- [x] Implement `TrayContext.cs` for tray icon, context menu, and active profile indicator
- [x] Implement interactive drag-and-drop `MonitorCanvas.cs` with shelf and magnetic snapping
- [x] Implement full High-DPI display scaling support across all controls and custom canvas
- [x] Filter out inactive GPU display adapters so only real connected monitors appear
- [x] Implement `ConfigForm.cs` (dark-themed profile editor, inspector, & hotkey manager)
- [x] Implement "Capture Current Layout" to make creating profiles frictionless
- [x] Implement multi-monitor on-screen "Identify" overlay banners

## Phase 4: Quality of life
- [x] Automatic 20-second layout undo after apply
- [x] Confirm before turning monitors off
- [x] Post-apply verification against the live Windows layout
- [x] Separate Save, Apply, and Save & Apply
- [x] Unsaved-change tracking and close prompt
- [x] Duplicate profile, refresh hardware, fit view, snap toggle
- [x] Live display-change refresh and current-layout indicator
- [x] Canvas zoom, arrow-key nudge, and connection status
- [x] Tray undo / previous layout command
- [x] Stable monitor PnP identity for display-number changes
- [x] Non-destructive `--test-apply` validation path
- [x] Save error reporting and exact-topology apply handling
- [x] Live `profiles.json` watcher with debounce and unsaved-change protection
