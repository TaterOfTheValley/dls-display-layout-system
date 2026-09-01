# Monitor Identity Bugs — Analysis & Fix Plan

## Symptom

Profiles created in the drag-and-drop layout editor don't reliably map to the correct
physical monitors when applied — e.g. the wrong monitor ends up enabled/disabled or
positioned incorrectly.

## Root cause

Traced end-to-end through `DisplayEngine.cs`, `MonitorCanvas.cs`, `ProfileManager.cs`,
and `TrayContext.cs`. Two concrete bugs in how monitor identity is derived and used,
plus one compounding data-loss issue.

### Bug 1 — `ExtractMonitorKey` throws away the part of the ID that disambiguates monitors

`DisplayEngine.cs:440-461`

For a device ID like `MONITOR\DEL4090\5&26957f3d&0&UID4352_0`, this method returns only
`DEL4090` — the monitor *model* — and discards `5&26957f3d&0&UID4352`, the
instance/port-specific suffix. That suffix is present in both the legacy
(`MONITOR\...`) and modern (`DISPLAY#...#...#{GUID}`) ID formats and is exactly what
ties an identity to a specific physical port.

With three identical monitors (a normal 3-monitor desk setup), all three collapse to
the same "stable" key. Every place that calls `SameHardwareIdentity` — profile apply,
hardware-to-config matching in the canvas, load-time reconciliation — then uses
`FirstOrDefault`, so it can silently bind a saved "Left monitor" entry to whichever
identical unit Windows happens to enumerate first, not the one that was actually
dragged into that slot.

### Bug 2 — the built-in "Share / left only" default profile never sets `HardwareId`

`ProfileManager.cs:139-155`

`GetDefaultProfiles()` populates `DeviceName`, `MonitorId`, position, size, etc., but
never sets `HardwareId`. This is the exact scenario the user cares about (drop to one
monitor so the other computer can use the rest), and it falls back to matching by the
fragile `\\.\DISPLAYn` name alone. Windows renumbers those names whenever a monitor is
attached/detached/enabled/disabled — precisely what this app does on every switch. So
the second time this profile is applied after any topology change, it can easily
target the wrong physical output.

### Compounding issue — silent data loss on load

`ProfileManager.cs:78-82`

After resolving hardware identity, `LoadProfiles` dedups `p.Displays` via
`GroupBy(DeviceName).First()`. If Bug 1 causes two distinct saved monitor entries to
resolve to the same live `DeviceName`, one is silently dropped from the profile every
time it's loaded — the profile quietly loses a monitor with no error shown.

## Fix plan

1. **Fix `ExtractMonitorKey`** to preserve the instance-specific segment instead of
   stopping at the first `\` / `#` — keep everything up to (and including) the
   `UID####` token. This is the root fix: it makes identity unique per physical port
   even when monitor models are identical.
2. **Set `HardwareId` (and `RelativePosition`) in `GetDefaultProfiles`'s "Share / left
   only" branch**, the same way the "Desk / all three" profile already does via
   `CaptureCurrentLayoutAsProfile`.
3. **Replace the silent `GroupBy(DeviceName).First()` dedup** in `LoadProfiles` with a
   dedup keyed on `HardwareId` (falling back to `DeviceName` only when `HardwareId` is
   blank), or surface a warning instead of silently discarding a target.
4. **Add a regression check** — a small unit test (or documented manual test) for
   `SameHardwareIdentity` / `ExtractMonitorKey` using two synthetic identical-model
   device IDs that differ only in the UID suffix, asserting they are *not* treated as
   the same monitor.
5. **Verify with the existing "Identify on screen" overlay** (already implemented in
   `MonitorCanvas.cs`): capture a profile, apply it, and confirm the numbered overlays
   land on the physically correct monitors — including after disabling/re-enabling a
   monitor to trigger `DISPLAYn` renumbering.
