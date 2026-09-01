# Monitor Layout Switcher — Bug Fix Plan

**Status: implemented (all three bugs) and build-verified.** This doc is kept
as a record of the root-cause analysis. One correction made during
implementation, worth knowing if you touch this code again: this machine's
real `EnumDisplayDevices` output does **not** use the `UID####`-suffixed
format `fixes.md` assumed (e.g. `MONITOR\DEL4090\5&26957f3d&0&UID4352_0`).
The actual format observed here is the "PnP device instance id" style:
`MONITOR\DELF13D\{4d36e96e-e325-11ce-bfc1-08002be10318}\0002` — model, a
shared device-interface-class GUID, then a plain incrementing instance
number. `ExtractMonitorKey` (`DisplayEngine.cs`) now handles both shapes:
it prefers a `UID####` token when present (and normalizes legacy `\` vs
modern `#` separators so a legacy id and a modern id for the same monitor
still compare equal), and falls back to keeping every segment except the
bracketed GUID — in particular the trailing instance number — when there's
no UID token. `--test-identity` covers both shapes.

Three bugs, three independent root causes, ordered by implementation priority
(fix Bug C first — Bug B partly depends on it; Bug A is fully independent and
can be done in parallel).

Do not do speculative refactoring beyond what's listed. Build after each bug
with `dotnet build src/MonitorLayoutSwitcher.csproj -c Release` before moving
to the next one.

---

## Bug C — Wrong monitor identity (4K Dell detected as the 1440p monitor)

**This was already root-caused in `fixes.md` in this repo by an earlier
analysis pass, but the fix was never applied to the code.** Verified against
the current `src/DisplayEngine.cs` — the bug is still present exactly as
described there. Implement the fix plan from `fixes.md`:

### C1. Fix `ExtractMonitorKey` (`src/DisplayEngine.cs`, method `ExtractMonitorKey`, ~line 440)

Current code, for the `MONITOR\` branch:
```csharp
start += "MONITOR\\".Length;
int end = normalized.IndexOf('\\', start);
return end > start ? normalized[start..end] : normalized[start..];
```
For a device ID like `MONITOR\DEL4090\5&26957f3d&0&UID4352_0`, this returns
only `DEL4090` — the monitor **model number**, stopping at the *first*
backslash. It throws away `5&26957f3d&0&UID4352_0`, which is the
instance/port-specific suffix that actually distinguishes one physical
monitor from another of the same or similar model. Every place that calls
`SameHardwareIdentity` (profile apply, `MatchesCurrent`, the canvas, profile
load) uses `FirstOrDefault` against this collapsed key, so it can silently
bind a saved "Left monitor" target to whichever matching-model unit Windows
happens to enumerate first — this is why the app reports the 1440p monitor
as the Dell.

**Fix:** keep the full instance-specific segment instead of stopping at the
first separator. Take everything from after `MONITOR\` up through the
`UID####` instance token, not just up to the first `\`. Apply the same fix
to the `DISPLAY#` branch just below it (currently stops at the first `#`
after `DISPLAY#` — same bug, same fix shape: keep the instance/target
segment, don't truncate at the first delimiter).

Do not just remove the truncation entirely (i.e. don't return the whole
raw string) — some device ID formats have a trailing `\{GUID-class}` suffix
that should still be stripped so two representations of the *same* physical
monitor still compare equal. The fix is "stop truncating too early", not
"stop truncating."

### C2. Set `HardwareId` in the default "Share / left only" profile (`src/ProfileManager.cs`, method `GetDefaultProfiles`, ~line 132–155)

This is the exact profile the user actually uses (drop to left monitor only
so another PC can take the rest). It currently builds each
`DisplayTargetConfig` with `DeviceName`, `MonitorId`, position, size,
`RefreshRate`, `IsPrimary` — but **never sets `HardwareId`**. Compare to the
"Desk / all three" profile a few lines above it, which gets `HardwareId` for
free via `CaptureCurrentLayoutAsProfile`.

Without `HardwareId`, this profile falls back to matching by the raw
`\\.\DISPLAYn` device name, which Windows **renumbers** every time a
monitor is attached/detached/enabled/disabled — i.e. every single time this
app switches layouts. So the second time "Share / left only" is applied
after any topology change, it can target the wrong physical output.

**Fix:** in the `foreach (var d in currentDisplays)` loop that builds
`shareProfile.Displays`, add `HardwareId = d.HardwareId` to the
`DisplayTargetConfig` initializer (same line where `MonitorId = d.MonitorId`
is set).

### C3. Replace the silent dedup in `ProfileManager.LoadProfiles` (~line 78–82)

```csharp
p.Displays = p.Displays
    .GroupBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
    .Select(g => g.First())
    .ToList();
```
If C1 isn't fixed (or for any legacy profile saved before the fix), two
distinct saved monitor entries can resolve to the same live `DeviceName`,
and this silently drops one of them from the profile on every load — no
error, the profile just quietly loses a monitor.

**Fix:** key the dedup on `HardwareId` when present, falling back to
`DeviceName` only when `HardwareId` is blank:
```csharp
p.Displays = p.Displays
    .GroupBy(d => string.IsNullOrWhiteSpace(d.HardwareId) ? d.DeviceName : d.HardwareId,
              StringComparer.OrdinalIgnoreCase)
    .Select(g => g.First())
    .ToList();
```

### C4. Add a regression check

Add a `--test-identity` branch in `src/Program.cs`, following the exact
pattern of the existing `--test-apply` and `--screenshot` branches (see top
of `Main`). It should:
- Build two synthetic device ID strings that differ only in their UID
  suffix, e.g. `MONITOR\DEL4090\5&26957f3d&0&UID4352_0` and
  `MONITOR\DEL4090\5&26957f3d&0&UID4353_0`.
- Assert `DisplayEngine.SameHardwareIdentity(a, b)` is `false` for that pair.
- Assert `DisplayEngine.SameHardwareIdentity(a, a)` is `true`.
- Print `PASS`/`FAIL` and a nonzero exit code on failure, matching the style
  of the other CLI test switches.

This needs `SameHardwareIdentity` to stay `internal static` (it already is)
— no visibility change needed since `Program.cs` is in the same assembly.

### C5. Verify

Use the existing "Identify" overlay (`MonitorCanvas.cs` /
`IdentifyOverlays`): capture a fresh profile, apply "Share / left only", and
confirm the on-screen numbered overlays land on the physically correct
monitors. Then disable/re-enable a monitor once (to force Windows to
renumber `DISPLAYn`) and re-apply — confirm it's still correct. That
renumbering scenario is the whole reason `HardwareId` matters over
`DeviceName`.

### C6. Note for whoever tests this

Any `profiles.json` already on disk (`dist/profiles.json`,
`src/bin/Debug/net8.0-windows/profiles.json`, etc.) was captured under the
*old*, buggy identity logic and may still carry collapsed/model-only
`HardwareId` values. After the fix ships, delete those files (or use
"Capture current layout" in the UI to regenerate the profiles) — otherwise
it will look like the bug is "still there" when it's actually just stale
saved data.

---

## Bug B — "Apply" / "Save & Apply" doesn't change anything

Two independent contributing defects. Fix both.

### B1. `ChangeDisplaySettingsEx` calls are never committed as one batch (`src/DisplayEngine.cs`, method `ApplyProfile`, the two `foreach` loops around line 554 and line 587)

Each monitor is changed with an immediate, individual call:
```csharp
int res = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
```
with no `CDS_NORESET` flag, and there is no final "commit" call. Windows'
documented pattern for changing more than one display's settings together
is: stage every device's change with `CDS_NORESET` (so it doesn't take
effect immediately), then make exactly one trailing call with a `null`
device name to commit everything as a single atomic topology change. The
no-argument overload for that commit call already exists in this file:
```csharp
[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);
```
(declared right after the `ref DEVMODE` overload, currently unused).

Without staging + a single commit, each individual `ChangeDisplaySettingsEx`
call can report `DISP_CHANGE_SUCCESSFUL` while the *aggregate* topology
change is rejected or only half-applied by the driver — because an
intermediate state (e.g. a monitor positioned to overlap another monitor
that hasn't been moved/disabled yet in this same loop) can be invalid. This
matches the symptom exactly: no error is shown, but nothing visibly
changes.

**Fix:**
1. Add `private const uint CDS_NORESET = 0x10000000;` next to the other
   `CDS_*` constants (~line 121).
2. In both `foreach` loops in `ApplyProfile` (the "configure enabled
   displays" loop and the "detach disabled displays" loop), OR
   `CDS_NORESET` into the `flags`/`0` value passed to
   `ChangeDisplaySettingsEx` for every per-device call.
3. After both loops finish (i.e. right before `return true;` at the end of
   `ApplyProfile`), add exactly one commit call:
   ```csharp
   ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
   ```
   using the `IntPtr`-overload declared above. Check its return value the
   same way as the other calls and populate `errorMessage` /
   `return false` on failure, so a commit failure is no longer silent.

### B2. `ApplySelected` can silently no-op ("already active") on a stale/incorrect identity read (`src/ConfigForm.cs`, method `ApplySelected`, ~line 926–941)

```csharp
if (DisplayEngine.MatchesCurrent(_selectedProfile))
{
    if (saveFirst) { SaveProfilesOnly(); }
    else { _feedbackLabel.Text = $"'{_selectedProfile.Name}' is already active."; }
    return;
}
```
If `MatchesCurrent` (`DisplayEngine.cs`, ~line 698) reports a false
positive, clicking Apply/Save & Apply does nothing except show a misleading
"already active" message — `LayoutSafety.Apply` (the code path that
actually calls `DisplayEngine.ApplyProfile`) is never reached.
`MatchesCurrent` depends on `SameHardwareIdentity`, i.e. on `Bug C`. This
should mostly self-resolve once **Bug C (C1) is fixed**, since identity
comparisons stop collapsing distinct monitors together.

**Fix:** implement Bug C first. Then, as a verification step (not a code
change unless the false positive persists after C1): repeatedly toggle
between "Desk / all three" and "Share / left only" several times in a row
and confirm the "already active" message never appears when the live
layout genuinely doesn't match the selected profile. If it still does after
C1 is fixed, that's a sign `MatchesCurrent`'s position/size slack
tolerances (`slack = 96` px, width/height tolerance `8` px, ~line 698) are
too loose for the specific hardware and need tightening — but don't
preemptively change those tolerances; confirm there's an actual repro
first.

### Verification

Real display hardware is required to verify this bug — it cannot be
confirmed by a build or by an LLM. Use the existing
`MonitorLayoutSwitcher.exe --test-apply "<profile name>"` CLI switch (see
`Program.cs`) for a non-destructive dry-run check (it only validates, it
doesn't apply), then have the user physically confirm monitors turn
on/off when clicking Apply / Save & Apply in the UI.

---

## Bug A — Text/UI doesn't scale with Windows display scaling

Root cause has two parts; fix both.

### A1. Manual DPI scaling fights WinForms' automatic DPI scaling

`ConfigForm`'s constructor (`src/ConfigForm.cs`, ~line 63) sets:
```csharp
AutoScaleMode = AutoScaleMode.Dpi;
```
while *also* manually multiplying every font size and control dimension by
a hand-rolled `DpiScale` (`private float DpiScale => DeviceDpi / 96f;`,
~line 55) via the `S(int)` helper and by passing `DpiScale` into
`UiTheme.MakeButton(...)` etc. `MonitorCanvas.cs` does the same thing with
its own `DeviceDpi`/`DpiScale`. Having WinForms' automatic scaling system
active *and* doing 100% manual scaling everywhere is a well-known WinForms
anti-pattern — the two systems compute overlapping/conflicting scale
factors and compound unpredictably instead of cooperating.

**Fix:** the app already does thorough manual scaling everywhere (every
`Font` and every dimension already goes through `DpiScale`/`S()`), so keep
that and disable the automatic system instead of the reverse. Change:
```csharp
AutoScaleMode = AutoScaleMode.Dpi;
```
to
```csharp
AutoScaleMode = AutoScaleMode.None;
```
in `ConfigForm`'s constructor. If `MonitorCanvas` (a `UserControl`) sets
`AutoScaleMode` anywhere in its own constructor, set it to `None` there too
for consistency.

### A2. DPI is read once, before the window handle exists, and never re-read

`ConfigForm`'s `DpiScale` (and every `Font`/size derived from it) is
computed in the **constructor**, which runs before the window's handle is
created and before Windows has told the process which monitor the window
will actually appear on. `Control.DeviceDpi` is not reliable until after
`HandleCreated` — reading it earlier reflects a process-startup/primary
default, not necessarily the monitor the window opens on (the window is
positioned with `StartPosition = FormStartPosition.CenterScreen`, decided
later, at `Show()` time — unrelated to the DPI value already baked into
every `Font` by then).

Worse: `TrayContext.cs` (~line 270–281) creates `ConfigForm` exactly **once**
and caches it in `_configForm`, reusing that same instance for the entire
life of the app (`_configForm.Show()` is called on every subsequent open,
never `new ConfigForm(...)` again). So whatever DPI value was captured on
that very first construction is locked in permanently — including across
multiple tray-menu "Configure" clicks.

There is **no `OnDpiChanged` override anywhere in the codebase** (confirmed
by search — zero matches). Even though the project is correctly configured
for per-monitor DPI awareness (`<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>`
in `MonitorLayoutSwitcher.csproj`), which means Windows *does* send a DPI-change
notification when the window moves to a differently-scaled monitor or when
the user changes a monitor's scaling live, nothing in the app listens for
it. Fonts and sizes are computed exactly once and never revisited — this is
why the text "stays small" instead of tracking the display's actual scaling.

**Fix:**
1. In `ConfigForm.cs`, extract the body of the constructor that builds
   controls, sets fonts, and sets sizes (basically everything that uses
   `DpiScale` or `S(...)`) into a single method, e.g. `private void
   BuildOrRescaleUi()`. Call it once from the constructor (as today).
2. Add:
   ```csharp
   protected override void OnDpiChanged(DpiChangedEventArgs e)
   {
       base.OnDpiChanged(e);
       BuildOrRescaleUi();
   }
   ```
   If a full rebuild-in-place of all child controls is too invasive to do
   safely as a first pass, the minimum acceptable version is: iterate
   `Controls` recursively and reassign each control's `Font` scaled by the
   ratio `e.DeviceDpiNew / (float)e.DeviceDpiOld`, and rescale `Size` /
   `Location` / `Padding` / `MinimumSize` / `ClientSize` the same way. But a
   real rebuild via the same code path used at construction time is more
   reliable and should be preferred if it doesn't visibly break existing
   layout logic — try that first, fall back to the ratio-rescale approach
   only if rebuild causes stacked/duplicate controls.
3. `MonitorCanvas.cs` already recomputes its **painted** fonts (monitor
   numbers/names/badges) on every `Paint` call using its live `DpiScale`
   property (confirmed at multiple `using var font = new Font(...,
   DpiScale, ...)` call sites inside paint methods) — those are already
   correct and just need an `Invalidate(true)` after a DPI change, which a
   parent-form re-layout will trigger anyway. The only things that need an
   explicit rebuild in `MonitorCanvas` are the few controls whose `Font` is
   set once in its constructor and never touched again (e.g. the labels
   around line 915–932) — apply the same "extract to a rebuild method,
   call it again on DPI change" approach there.
4. Do not leave `MinimumSize`/`ClientSize` (`ConfigForm.cs` ~line 67–68)
   as fixed absolute pixel values set once — make sure the rebuild step
   recomputes them from `S(...)` using the *current* `DpiScale` each time,
   not just on first construction.

### Verification

1. Set the monitor the config window opens on to 100% scaling, launch the
   tray app, open "Configure" — layout/text should look normal (not
   oversized, not doubled-up from A1's fix).
2. While the window is open, change that monitor's Windows scaling to
   150% or 200% (Windows applies this live) — text and buttons should
   resize immediately, without restarting the app.
3. Close the window (don't kill the app), change the monitor's scaling
   again, reopen "Configure" from the tray — it should open already
   correctly sized on the very first show, not just after a later change
   event. This specifically tests that the cached/reused `_configForm`
   instance (`TrayContext.cs`) no longer carries a stale DPI value from
   its original construction.
4. If a second monitor with different scaling is available, drag the
   config window from one to the other and confirm it rescales live.

---

## Suggested order of work

1. Bug C (C1 → C2 → C3 → C4 → C5) — highest priority, user-facing safety
   issue (wrong monitor could get disabled), and Bug B2 likely depends on it.
2. Bug B (B1, independent of C; then re-check B2 after C1 lands).
3. Bug A (fully independent, can be done anytime, even in parallel with 1–2).

Build after each numbered sub-item with:
```
dotnet build src/MonitorLayoutSwitcher.csproj -c Release
```
and smoke-test with the existing CLI switches before touching the UI:
```
MonitorLayoutSwitcher.exe --test-apply "Desk / all three"
MonitorLayoutSwitcher.exe --test-apply "Share / left only"
MonitorLayoutSwitcher.exe --test-identity   (new, from C4)
```
Final verification for all three bugs requires the actual Windows machine
with the real monitors attached — none of this can be fully confirmed by
build/CI alone.
