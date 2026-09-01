# Monitor Layout Switcher

A Windows 11 tray utility for switching between saved monitor layouts with a
hotkey. Built for one specific problem: several monitors shared between two
computers, where turning two of them off on this PC — so the other PC can use
them — should take one keypress rather than a trip through display settings.

Windows has no built-in way to say "use only the left monitor" and then put the
other two back exactly as they were. That is what this does.

## What it does

- Save named layouts: which monitors are on, where they sit, at what resolution
  and scaling.
- Switch with a global hotkey or from the tray.
- Turn monitors off and back on again — the part Windows makes hardest.
- Revert automatically if you don't confirm, so a switch can't strand you
  looking at a monitor it just turned off.

## Requirements

Windows 10 1607 or later (Windows 11 recommended) and the
[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
The build is framework-dependent, so the runtime must be installed.

## Getting started

Run `dist\MonitorLayoutSwitcher.exe`. It lives in the tray; double-click the
icon to open the layout editor.

There are **no profiles to begin with** — the app won't guess at a layout for
hardware it hasn't been told about. Set your monitors up the way you want them
in Windows, then use **Capture Current Layout** from the tray menu. That records
the arrangement exactly as it stands, which is always correct by construction.

A typical pair of layouts:

1. Arrange all your monitors normally → capture as `Desk`.
2. In the editor, turn off the ones you want to hand over, and save that as
   `Share`.
3. Give each a hotkey.

## In the editor

Layouts run along the top as thumbnails — a layout is a picture, and you recognise
the one you want from its shape faster than from its name. Select one and the
canvas below shows that arrangement; drag monitors to rearrange, drag one off the
canvas to turn it off.

Edits save themselves. There is no Save button, and the only commit action is
**Apply layout**, which switches Windows to what you are looking at.

| Key | Does |
|---|---|
| `Ctrl` + `1`…`9` | Switch to that layout |
| `Ctrl` + `Enter` | Apply the selected layout |
| `Ctrl` + `N` | New layout |
| `Ctrl` + `Z` | Undo the last switch |
| `Esc` | Close (or stop recording a shortcut) |
| Arrow keys | Nudge the selected monitor |

## The confirmation prompt

After a switch, a prompt appears with a 20-second countdown. **Doing nothing
reverts to the previous layout.** You have to actively click *Keep this layout*
for the change to stick.

That is deliberate, and it is the opposite of a normal confirmation dialog. The
way a display switch fails is that the monitor you would click on is now black —
so recovery has to be the thing that needs no input.

Switching from the command line with `--apply` skips the prompt; nothing
auto-reverts there.

## Resolution, refresh rate and scaling

Each monitor in a layout shows its resolution and scaling, and resolution,
refresh rate and scaling can all be overridden in the editor.

Change the resolution and the **highest refresh rate the monitor supports at that
size** is selected automatically — dropping from 4K144 to 1440p and silently
landing on 60Hz is a bad surprise. Override it afterwards if you want something
else.

Modes come from the display driver for a monitor that is currently attached. For
one that is switched off, they are built from the panel's native mode instead: a
disabled output only advertises a generic low-resolution list, so asking Windows
what it supports would offer 1080p for a 4K panel. The native mode comes from the
monitor's EDID, which is readable whether it is on or not.

Scaling defaults to **Leave unchanged** — the layout has no opinion and switching
to it will not touch whatever scaling the monitor already has. Set an explicit
percentage and the layout will apply it after switching.

> Windows exposes no supported API for reading or setting per-monitor scaling.
> This uses the same undocumented calls the Settings app does. They have been
> stable since Windows 10 1607, but a scaling change that does not take will
> never fail the layout switch itself — the layout applies, the scale silently
> stays put.

## Where settings live

`profiles.json`, beside the executable. It is portable: copy the folder and your
layouts come with it.

It is **machine-specific**. Profiles identify monitors by their Windows device
path, which encodes the physical port a monitor is plugged into — so a profile
from another PC, or from a different set of ports, won't match anything. Delete
`profiles.json` to start over.

Profiles saved before the display engine was rewritten can't identify monitors
reliably. Those are flagged as needing re-capture rather than being migrated,
because the old identity data couldn't tell two same-model monitors apart.

## Command line

The app is a GUI executable, so it attaches to the calling console. Add
`--out <file>` to any of these to also write the output to a file.

| Command | What it does |
|---|---|
| `--dump-config` | Prints the live display topology. Non-destructive. **Start here when something misbehaves.** |
| `--list-profiles` | Lists saved layouts and the monitors in each. |
| `--capture "<name>"` | Saves the current arrangement under that name. |
| `--test-apply "<name>"` | Validates a layout without changing anything. |
| `--apply "<name>"` | Applies a layout. No confirmation prompt. |
| `--set-refresh <hz>` | Sets the primary display's refresh rate through the real apply path, and reports the result. |
| `--set-scale <percent>` | Sets the primary display's scaling and reports what actually changed. |
| `--screenshot-menu <file>` | Renders the tray menu to a PNG. Useful for checking it at your display scaling. |
| `--screenshot-hud <file>` | Renders the post-switch confirmation overlay to a PNG. |

`--dump-config` is the one worth knowing. It shows every connected monitor —
including ones that are currently disabled — with its identity, mode, and
`source:` line. Two *active* monitors sharing a source are cloned rather than
extended.

## Building

```
build.cmd
```

Publishes a single-file build to `dist\`. Or directly:

```
dotnet build src\MonitorLayoutSwitcher.csproj -c Release
```

`dist\` is checked into the repository on purpose — there is no CI or release
pipeline, so the committed binary is the distribution.

## How it works

Everything goes through the Windows **CCD** API (`QueryDisplayConfig` /
`SetDisplayConfig`) — the same one the Windows display settings page uses.

This matters more than it sounds. The obvious API for this job,
`ChangeDisplaySettingsEx`, has been a compatibility shim over CCD since Windows
7: it works one display at a time, has no real transaction, and reverse-maps
onto CCD lossily. Applying a multi-monitor change through it means staging
per-device calls in a hand-tuned order and hoping the driver agrees — which is
exactly where this project got stuck before the rewrite.

With CCD, a layout change is **one call** carrying the complete topology. Turning
one monitor on and two off is a single atomic transaction, so there is no window
where the desktop is half-configured.

Monitors are identified by `monitorDevicePath`:

```
\\?\DISPLAY#DELF13D#5&2b7bf8c&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}
```

That string is unique per physical port and stable across enable/disable cycles
and reboots. It is compared whole and never parsed. The older approach — reading
`\\.\DISPLAY1`-style names, or extracting a model code from a device ID — cannot
distinguish two identical monitors, and Windows renumbers those names on every
switch this app performs.

See `ccd-rewrite-plan.md` for the full design rationale.

## Known limitations

- **A monitor switched to another computer's input may disappear entirely.**
  Some monitors drop hotplug-detect when you change their input source. If that
  happens, Windows no longer sees the monitor at all and no software on this PC
  can re-enable it. Check with `--dump-config` while the monitor is showing the
  other computer: if it is still listed, you are fine. If not, the right tool is
  a DDC/CI input switch rather than a display-layout switch.
- Layouts are per-machine and not portable between PCs.
- The app changes the Windows desktop arrangement only. It does not switch
  monitor inputs.
