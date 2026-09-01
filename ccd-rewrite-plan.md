# Monitor Layout Switcher — CCD Rewrite Plan

**Supersedes:** `fixes.md`, `bugfix-plan.md`, and the apply/identity half of
`implementation-plan.md`. Those documents diagnose real symptoms but treat them as
independent bugs. They are all downstream of one architectural choice.

---

## 1. Why the current app struggles

`DisplayEngine.ApplyProfile` is built on **`ChangeDisplaySettingsEx` (CDS)**, the
Windows 3.1-era display API. Since Windows 7, CDS is not a real API — it is a
compatibility shim that Windows reverse-maps onto the modern **CCD** (Connecting
and Configuring Displays) subsystem. The mapping is lossy, per-adapter, and
driver-dependent.

The proof is in the code's own comments. Every one of these is a workaround for
the shim, not for a bug in this app:

| `DisplayEngine.cs` comment | What it really means |
|---|---|
| "This driver does not defer NORESET-staged changes… it validates each staged call as though it were immediate" | CDS has no real transaction. The shim commits per-device. |
| "the display becoming primary goes LAST — claiming (0,0) needs the origin actually vacated first" | Hand-sequencing a topology change one device at a time. |
| "a detach is rejected whenever `CDS_UPDATEREGISTRY` is present… `flags=0` is the only combination that succeeds" | Detach cannot be persisted through CDS, so it must run *after* commit. |
| "a bare `ChangeDisplaySettingsEx(NULL,…)` commit resyncs from the registry, which was observed to silently revert an un-persisted disable" | The disable and the rest of the layout are two separate transactions fighting each other. |
| "`EnumDisplaySettings` reports the true native mode list only once it's actually attached… has to be woken at a reachable mode first" | A whole extra pre-pass exists because disabled outputs lie about their modes. |
| "`EnumDisplayDevices`' per-adapter sub-query was observed to become unreliable once several outputs are simultaneously disabled — returning the same stale monitor identity for multiple different `DISPLAYn` adapters" | The identity source is unreliable in exactly the state this app creates. |

That is a four-phase apply (wake → configure → commit → detach) with ordering
rules discovered by trial and error, and it is *still* non-atomic. Windows shows
a black screen between phases and can half-apply. This is why "disable two of
three monitors" — which should be one call — is unreliable.

**The second problem is identity.** `ExtractMonitorKey` does string surgery on
`EnumDisplayDevices` IDs to build a stable per-port key. `fixes.md` fixed it,
then `bugfix-plan.md` had to correct `fixes.md` because the assumed ID format
(`…&UID4352_0`) didn't match the real one on this machine
(`MONITOR\DELF13D\{4d36e96e-…}\0002`). Both formats now need handling. This
entire class of problem does not exist under CCD — see §2.

**Conclusion: stop fixing the apply path. Replace the API underneath it.**

---

## 2. The right API: `QueryDisplayConfig` / `SetDisplayConfig`

This is what the Windows Settings > Display page itself uses. Four properties
make it the correct tool for this specific app:

1. **Atomic whole-topology apply.** `SetDisplayConfig` takes the *entire*
   desired state — every path and every mode — as two arrays, in one call.
   There is no staging, no per-device commit, no ordering. Enabling one monitor
   and disabling two others is a single transaction. Phases 1–4 of the current
   apply collapse into one call.

2. **Enable/disable is a flag, not a fake mode.** A path is on when
   `DISPLAYCONFIG_PATH_ACTIVE` is set in `pathInfo.flags`. Disabling is clearing
   that bit and setting both `modeInfoIdx` fields to
   `DISPLAYCONFIG_PATH_MODE_IDX_INVALID` (`0xFFFFFFFF`). No `0x0` DEVMODE hack,
   no detach-after-commit dance.

3. **Free, exact, stable identity.** `DisplayConfigGetDeviceInfo` with
   `DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME` returns **`monitorDevicePath`**:

   ```
   \\?\DISPLAY#DELF13D#5&2b7bf8c&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}
   ```

   This is unique per *physical port*, stable across enable/disable cycles and
   reboots, and identical for two identical monitors only if they are on the
   same port (i.e. never). **Compare it as a whole string with
   `OrdinalIgnoreCase`. Do not parse it.** `ExtractMonitorKey`,
   `SameHardwareIdentity`, `NormalizeMonitorDescription`, and the `--test-identity`
   regression check all get deleted.

4. **Real validation.** `SDC_VALIDATE` checks the *complete proposed topology*
   at once and tells you whether it is achievable. `ValidateProfile`'s current
   one-device-at-a-time `CDS_TEST` produces false negatives (its own comment says
   so, which is why `ApplyProfile` doesn't call it). Under CCD, validation
   becomes trustworthy enough to gate the apply on.

The code is already *half* here: it calls `QueryDisplayConfig` in
`GetActiveDisplayConfigTargets` — but only to read friendly names, using raw
pointer arithmetic (`IntPtr.Add(path, 20)`, `Marshal.ReadInt32(path, 28)`)
instead of real structs, and it never imports `SetDisplayConfig` at all. The
missing half is the half that matters.

---

## 3. Phase 0 — Prove it on this hardware before writing any C#

Do not start the rewrite until this passes. ~10 minutes, no build required.

```powershell
Install-Module DisplayConfig -Scope CurrentUser   # MartinGC94/DisplayConfig, CCD-based
Get-DisplayInfo
Disable-Display -DisplayId <center>,<right>       # the "Share / left only" case
Enable-Display  -DisplayId <center>,<right>
```

Three things to confirm and write down:

- **Does the 3→1 switch work cleanly and instantly?** If yes, CCD is the answer
  and the rest of this plan is mechanical.
- **Do the other two monitors still appear in `Get-DisplayInfo` while disabled?**
  They must, or profiles can't re-enable them.
- **The KVM question — the real risk in this setup.** When a shared monitor is
  switched to Computer B's input, does it stay electrically present to Computer
  A's GPU (hotplug-detect asserted), or does it vanish entirely? Check
  `Get-DisplayInfo` on Computer A while the monitor is showing Computer B.
  - *Stays present* → everything in this plan works as written.
  - *Vanishes* → no software on Computer A can re-enable it, because there is
    nothing to enable. See §7.

---

## 4. Target design

### 4.1 Profile format (breaking change — bump a `SchemaVersion` field)

A profile stops being a hand-rolled list of positions and becomes **a captured
CCD topology**, keyed by monitor device path:

```csharp
sealed class DisplayProfile {
    string Id, Name, Hotkey;
    int SchemaVersion;                  // 2
    List<PathEntry> Paths;
}

sealed class PathEntry {
    string MonitorDevicePath;           // the identity. whole-string compare.
    string FriendlyName;                // "DELL U2723QE" — display only, never matched on
    bool   Active;
    int    X, Y, Width, Height;         // source mode
    uint   RefreshNumerator, RefreshDenominator;   // exact RATIONAL, not a rounded int
    uint   Rotation, Scaling;           // DISPLAYCONFIG_ROTATION / _SCALING
    bool   IsPrimary;                   // == (X == 0 && Y == 0)
}
```

Note `RefreshNumerator/Denominator`: CCD carries refresh as a rational
(`143.998Hz` = `2400000/16667`). Storing a rounded `int` — as the current code
does — is a real source of "mode not found" failures on high-refresh displays.

Existing v1 profiles: on load, if `SchemaVersion < 2`, don't attempt migration.
Mark the profile "needs re-capture" in the UI and disable its Apply button. The
old `HardwareId` data isn't reliable enough to migrate from, and re-capturing is
one click.

### 4.2 New `DisplayEngine` surface

Replaces ~700 lines of apply/identity logic with roughly 300.

```csharp
// Query
List<LivePath> QueryPaths(bool includeInactive);  // QDC_ALL_PATHS vs QDC_ONLY_ACTIVE_PATHS
DisplayProfile CaptureCurrentLayout(string name, string hotkey);

// Apply
ApplyResult Validate(DisplayProfile p);           // SDC_VALIDATE
ApplyResult Apply(DisplayProfile p);              // SDC_APPLY
bool MatchesCurrent(DisplayProfile p);            // for tray checkmarks
```

**Delete outright:** `ExtractMonitorKey`, `SameHardwareIdentity`,
`SameMonitorDescription`, `NormalizeMonitorDescription`, `TryFindSupportedMode`,
`TryFindBestAvailableMode`, `TestDisableIsolated`, `TestReposition`,
`ListModes`, `GetEnumeratedDevices`, the wake pre-pass, and every
`ChangeDisplaySettingsEx` / `EnumDisplayDevices` / `EnumDisplaySettings` import.
The `--test-identity`, `--test-disable`, `--test-reposition`, and `--list-modes`
CLI branches in `Program.cs` go with them — they exist only to debug CDS.

### 4.3 The apply algorithm, in full

```
1. QueryDisplayConfig(QDC_ALL_PATHS | QDC_VIRTUAL_MODE_AWARE) -> paths[], modes[]
   (loop on ERROR_INSUFFICIENT_BUFFER — path count changes between the
    GetDisplayConfigBufferSizes call and the query)

2. For each path, resolve monitorDevicePath via DisplayConfigGetDeviceInfo.
   Build: devicePath -> list of candidate path indices.
   (A monitor can appear on several paths — different source/target pairings.
    Prefer one already ACTIVE; else the first with targetAvailable set.)

3. Reject early, with a clear message, if any profile entry with Active=true
   has no live path. "Monitor 'DELL U2723QE' is not connected." Do not
   silently apply a partial layout.

4. Build the submission arrays:
     - Start from the queried paths[] and modes[] (do NOT construct from scratch —
       adapterId/id pairs must come from a live query).
     - For each profile entry that is Active:
         append a SOURCE mode  {width, height, position, pixelFormat=32bpp}
         append a TARGET mode  (copy the live target mode; override refresh
                                from the stored RATIONAL)
         set path.sourceInfo.modeInfoIdx / targetInfo.modeInfoIdx to those indices
         set path.flags |= DISPLAYCONFIG_PATH_ACTIVE
         set targetInfo.rotation / .scaling from the profile
     - For every other path (including monitors present but not in the profile):
         path.flags &= ~DISPLAYCONFIG_PATH_ACTIVE
         sourceInfo.modeInfoIdx = targetInfo.modeInfoIdx = 0xFFFFFFFF

   Primary is implicit: exactly one active source mode must sit at (0,0).
   Assert this before submitting — it is the single most common cause of
   ERROR_INVALID_PARAMETER from SetDisplayConfig.

5. SetDisplayConfig(paths, modes,
       SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_VALIDATE | SDC_VIRTUAL_MODE_AWARE)
   -> if it fails, retry validate with | SDC_ALLOW_CHANGES, which lets Windows
      adjust an almost-right config. Report which one succeeded.

6. SetDisplayConfig(paths, modes,
       SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_APPLY | SDC_SAVE_TO_DATABASE
       | SDC_VIRTUAL_MODE_AWARE  [| SDC_ALLOW_CHANGES if step 5 needed it])
```

`SDC_SAVE_TO_DATABASE` matters: it writes the result into Windows' own
per-topology config database, so Windows itself restores this arrangement on the
next hotplug. It makes the app cooperate with Windows rather than fight it.

### 4.4 Error handling

`SetDisplayConfig` returns a Win32 code directly, not a HRESULT. Map the three
that actually occur:

| Code | Meaning | Message to show |
|---|---|---|
| `ERROR_INVALID_PARAMETER` (87) | Malformed config — usually no source at (0,0), or a bad `modeInfoIdx` | "This layout is not valid. Re-capture it." — and log the full path/mode array |
| `ERROR_NOT_SUPPORTED` (50) | GPU can't drive this combination | "Your graphics adapter can't run this monitor combination." |
| `ERROR_ACCESS_DENIED` (5) | Another process is mid-change | Retry once after 500 ms, then report. |

Log the serialized path/mode arrays on any failure. With CCD there is exactly
one call to inspect, so a failure is diagnosable from one log line — unlike the
current four-phase apply.

---

## 5. What stays

Everything above `DisplayEngine`. This is a surgical replacement of the bottom
layer, not a rewrite of the app.

- `ConfigForm.cs`, `MonitorCanvas.cs` — the drag-and-drop editor and the
  "Identify on screen" overlay are unaffected; they consume a display list and
  produce positions. Rebind their identity field from `HardwareId` to
  `MonitorDevicePath` and they work as-is.
- `TrayContext.cs`, `HotkeyManager.cs`, `UiTheme.cs` — untouched.
- `LayoutSafety.cs` — **keep, and it gets better.** The 20-second
  capture-snapshot / apply / undo-on-timeout pattern is exactly right. Under CCD
  the snapshot is a literal path+mode array, so the undo is byte-for-byte the
  previous topology rather than a reconstruction. Two changes: capture the
  snapshot via `CaptureCurrentLayout` (§4.2), and make undo the *default* —
  require an explicit "Keep this layout" click, so a switch that blanks the
  wrong monitor recovers by itself.
- `ProfileManager.cs` — keep the file/JSON handling. Delete the load-time
  reconciliation and the `GroupBy(...).First()` dedup (`bugfix-plan.md` C3):
  with unique device paths there are no collisions to dedup, and silently
  dropping entries was masking the identity bug.

---

## 6. Execution order

Build with `dotnet build src/MonitorLayoutSwitcher.csproj -c Release` after each
step. Do not proceed to the next step on a red build.

| # | Step | Done when |
|---|---|---|
| 0 | Phase 0 hardware check (§3) | 3→1 switch confirmed working via PowerShell; KVM behavior recorded |
| 1 | New `CcdInterop.cs`: real structs + `SetDisplayConfig` import. Nothing else changes. | Compiles; struct sizes assert 72 / 64 bytes at startup in Debug |
| 2 | `--dump-config` CLI: query all paths, print device path / friendly name / active / mode for each | Output matches the physical desk, including disabled monitors |
| 3 | `CaptureCurrentLayout` + v2 profile schema | Capture → JSON round-trips with correct device paths |
| 4 | `Apply` (§4.3), behind a `--apply-ccd <profile>` CLI flag. Old path still intact. | "Share / left only" works from the CLI, repeatedly, in both directions |
| 5 | Switch `LayoutSafety.Apply` to the new engine; make undo default-on | Tray + hotkey switching works; wrong layout self-recovers after 20s |
| 6 | Delete the CDS code and its CLI diagnostics (§4.2) | `grep -c ChangeDisplaySettingsEx src/` returns 0 |
| 7 | Rebind `MonitorCanvas` / `ConfigForm` to `MonitorDevicePath` | Editor round-trips; "Identify on screen" numbers land on the right physical monitors |
| 8 | v1 profile "needs re-capture" handling | Old profile shows the prompt instead of applying wrong |

**Step 4 is the whole bet.** If the 3→1 switch is reliable there, the rest is
cleanup. If it isn't, stop and re-read §7 before writing more code.

### Acceptance test — run at step 5 and again at step 8

1. All three enabled. Hotkey → left only. **One black flash, under a second.**
2. Hotkey → all three. Positions and refresh rates exactly as captured.
3. Repeat 1–2 five times without touching anything. All five identical.
4. Reboot. Repeat 1–2. Still correct — this is where `DISPLAYn` renumbering
   used to break things, and where device-path identity proves itself.
5. Apply a layout, don't click "Keep". After 20 s the previous layout returns.

---

## 7. If Phase 0 shows monitors vanish when switched to Computer B

Then the problem is not solvable in software on Computer A alone, and no amount
of work on this app will fix it. A monitor that has dropped hotplug-detect is
not "disabled" — it is *gone*, and there is no path to re-activate.

Options, in order of preference:

1. **A DDC/CI input switch instead of a Windows layout switch.** If the monitors
   stay connected to both machines and only their *input selection* changes, the
   right operation is "tell the monitor to switch input" via DDC/CI VCP code
   `0x60`. `ControlMyMonitor` (NirSoft) does this from the command line; test it
   first. This is arguably the *better* design for this problem regardless —
   the desktop arrangement on each machine never changes at all, so there is no
   window reshuffling on either side. Worth testing at Phase 0 even if
   enable/disable works.
2. **Both, coordinated.** Computer A drops to one monitor *and* sends the DDC/CI
   input-switch command, so the handoff is one keypress.
3. **A KVM/switch with a documented hotkey**, driven by the same tray action.

Record the Phase 0 answer in this file before continuing — it decides whether
this app is a display-topology tool or a monitor-input tool.

---

## 8. Reference

- `SetDisplayConfig` — learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setdisplayconfig
- `DISPLAYCONFIG_PATH_INFO` / `DISPLAYCONFIG_MODE_INFO` — winuser.h; sizes are
  72 and 64 bytes on x64, worth asserting at startup
- `MartinGC94/DisplayConfig` (PowerShell) — the clearest CCD reference
  implementation; use it for Phase 0 and as a check when a call is rejected
