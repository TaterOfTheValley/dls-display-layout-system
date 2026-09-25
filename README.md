# DLS — Display Layout System

A Windows 11 tray utility for switching between saved display layouts with a
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

## Starting up

DLS adds itself to Windows startup on first launch — a tray utility you have to
remember to start is one you stop using. It is a ticked item in the tray menu, so
it is visible and one click to turn off, and it uses the per-user `Run` key, so no
admin rights and it shows up in Task Manager's Startup tab like anything else.

If you disable it *in Task Manager*, the tray item greys out and says so rather
than pretending otherwise — Windows records that separately, and only Task Manager
can undo it.

On first launch DLS also checks its environment and tells you if something is
wrong: an unsupported Windows build, a display API that does not respond, no
detectable monitors, or a folder it cannot save layouts into. A healthy machine
sees nothing at all. Run `--preflight` any time to see the same report.

> The one dependency DLS cannot check for itself is the .NET runtime — if that is
> missing this app never starts, and Windows shows its own dialog with a download
> link. That only applies to the `requires-dotnet8` download; the default build
> bundles its own runtime and cannot hit this.

## Requirements

Windows 10 1607 or later. Windows 11 recommended.

## Install

Download from the [DLS releases](https://github.com/TaterOfTheValley/dls-display-layout-system/releases).
During the alpha period, use the releases list because GitHub's **Latest** link
excludes prereleases.

| Download | Use |
|---|---|
| `DLS-DisplayLayoutSystem-win-Setup.exe` | **Recommended.** Installs DLS once, adds shortcuts, and enables in-app updates. The .NET runtime is bundled. |
| `DLS-<version>-win-x64.exe` | Standalone EXE. Run it from a folder you choose; updates are manual. The .NET runtime is bundled. |
| `DLS-<version>-win-x64-requires-dotnet8.exe` | Small standalone EXE. Requires the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). Updates are manual. |

If you already run an older standalone DLS, **Exit** it from the tray before running
`DLS-DisplayLayoutSystem-win-Setup.exe` for the first time. Remove your old EXE or shortcut after the
installed copy opens. Both copies use the same layouts, so no export is needed.
The installer is per-user and needs no administrator rights. It installs under
`%LOCALAPPDATA%\DLS-DisplayLayoutSystem`; layouts remain under `%LOCALAPPDATA%\DLS`.

For manual standalone updates, put the EXE in a permanent folder as `DLS.exe`.
On each update, exit DLS, replace that file with the newly downloaded version
(renamed to `DLS.exe`), then launch it. The first launch after moving a copy repairs
the **Start with Windows** path if that setting is enabled.

### Updates

Installed copies check for updates quietly after startup and every 12 hours while
running. When one is available, DLS shows a tray notification. Choose **Check for
updates** or **Update to DLS ...** from the tray menu, review the release notes, then
select **Install and restart**. DLS downloads and verifies the package, saves pending
layout edits, exits, applies the update, and relaunches. You can choose **Later** and
keep working. A standalone EXE directs you to the installer when you choose **Check
for updates**.

Layouts and hotkeys live outside the installed application, so updates preserve them.
If a display change is waiting for confirmation, finish that choice before installing
an update.

> **Windows will warn you the first time.** The executable is not code-signed, so
> SmartScreen shows *"Windows protected your PC"*. Choose **More info → Run anyway**.
> Every release ships a `SHA256SUMS.txt` for the standalone downloads if you would
> rather verify one first: `Get-FileHash .\DLS-<version>-win-x64.exe`.

It lives in the tray; double-click the icon to open the layout editor.

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

`%LOCALAPPDATA%\DLS\.dls` — scoped to your Windows account, not to an app
version. Install an update, move a standalone executable, or re-download it and
it finds the same layouts.

Local rather than Roaming is deliberate: a layout identifies monitors by the
physical port they are plugged into, so roaming it to another machine would sync
data that cannot match anything there. Delete `.dls` to start over.

**Portable mode:** put an empty file called `.dls` next to a standalone `DLS.exe`
and settings live there instead, travelling with the folder. Use manual updates for
this mode.

### Versioning

Two versions, both semantic, moving independently:

- **App version** — `0.1.0-alpha`, set by the release tag (`DLS.csproj` holds the
  local-build default). Shown by `--preflight` and recorded in every settings file.
- **Settings format** — `MAJOR.MINOR` in the `.dls` file. MAJOR changes when a field
  changes meaning or disappears; MINOR when fields are only added. There is no PATCH:
  a file format has no bug-fix axis.

That split is what makes the compatibility rules meaningful rather than
all-or-nothing:

| File says | What happens |
|---|---|
| Same or older | Loaded. Older is migrated forward a step at a time and saved back, so the upgrade happens once. |
| **Newer MINOR** (e.g. 1.1 vs 1.0) | Loaded normally — additive changes are safe to read. Backed up to `.dls.v1.1.bak` first, because saving would drop the fields this build doesn't know. |
| **Newer MAJOR** (e.g. 2.0 vs 1.0) | **Refused.** Backed up and the session starts empty. Fields could mean something else entirely, and a layout applied from a misread file drives real hardware. |
| Unreadable | You start with no layouts and are told why. |

A layout that can't identify its monitors is flagged for re-capture. That's derived
from the data — a target with no device path — rather than stored as a number, so it
can't disagree with the layout it describes.

Layouts saved before the display engine was rewritten can't identify monitors
reliably. Those are flagged as needing re-capture rather than being migrated,
because the old identity data couldn't tell two same-model monitors apart.

## Command line

The app is a GUI executable, so it attaches to the calling console. Add
`--out <file>` to any of these to also write the output to a file.

| Command | What it does |
|---|---|
| `--preflight` | Runs the first-launch environment checks and prints the report. |
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
build.cmd                  fast framework-dependent build
build.cmd selfcontained    what a release actually ships (~68 MB)
```

Both publish a single file to `dist\`, which is gitignored. Or directly:

```
dotnet build src\DLS.csproj -c Release
```

## Releasing

There is no release branch. A release is a tag on `master`:

```
git tag v0.2.0
git push --tags
```

Before tagging, update `RELEASE_NOTES.md`; its text appears in the in-app update
dialog and on the GitHub release. `.github/workflows/release.yml` builds the two
standalone variants and a Velopack installer on a Windows runner. It derives versions
from the tag, writes `SHA256SUMS.txt`, and publishes all downloads and update packages
to this repository's release using the workflow's `GITHUB_TOKEN`. A tag with a
pre-release tail such as `v0.2.0-alpha` is marked as a pre-release.

The tag is the only place a release version is set. `DLS.csproj` holds a default for
local builds and is overridden by the workflow, so there is nothing to bump by hand.

## License

DLS itself is [MIT licensed](LICENSE).

The self-contained build embeds the .NET 8 runtime. That runtime, the Windows Desktop
runtime, `System.Text.Json`, and Velopack are MIT licensed. Their notices ship with
every release. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

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
