# Fast Monitor Layout Switching for Shared Windows 11 Monitors

## Concept

A Windows 11 utility for switching between saved monitor layouts almost instantly. It would make it easy to reassign shared monitors when multiple computers are used at the same time.

## Problem

Three monitors are shared across multiple computers. Computer A normally uses all three, but when Computer B is turned on, Computer A should use only the left monitor while Computer B uses the center and right monitors. Reconfiguring the display arrangement manually is too slow and disruptive.

## Desired outcome

Switch between predefined monitor-layout profiles with minimal interruption, so the shared monitors can be divided between computers quickly whenever the active setup changes.

## Initial requirements

- Run on Windows 11.
- Save and restore named monitor-layout profiles.
- Switch layouts extremely quickly.
- Support assigning different monitors to different computers by changing each computer's active layout.
- Handle the three-monitor scenario where Computer A changes from all three monitors to the left monitor only.
- Handle the corresponding state where Computer B uses the center and right monitors.

## Open questions

- Should switching be triggered by a global keyboard shortcut, a tray menu, or both?
- Should profiles be selected manually, or should the utility detect computer or monitor power/input changes?
- Does switching need to control monitor input selection as well as the Windows display arrangement?
- How should monitor identity be preserved if Windows changes display numbering or ordering?

## Next step

Prototype the local Computer A handoff with two named profiles, a global shortcut, monitor identity capture, and apply/verify behavior.

## Design status

The [concrete design review](.lavish/monitor-layout-switcher.html) proposes a local, profile-based, hotkey-first utility with a compact profile editor. Each profile has a configurable captured hotkey, display assignment, and name. The first cut changes the Windows desktop arrangement only and stops rather than guessing when a monitor identity is missing. Monitor input switching and coordination across both computers remain open decisions.
