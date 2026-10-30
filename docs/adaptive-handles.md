# Adaptive window grips

[Back to README](../README.md) | [Technical reference](technical.md)

The Windows companion supplies external hover grips and offscreen recovery and is included in the stable distribution.

## Behavior


- Foreground desktop Firefox windows receive hover grips. A partially offscreen last-active Firefox retains its reachable recovery grips after losing focus. Fully onscreen background windows, minimized windows, other virtual desktops, dialogs, other applications, maximized windows and fullscreen do not receive overlays.
- Grips use the search pill's dark #17171b fallback surface, a thin white rim and fully rounded semicircular ends. There is no cyan glow or inner light stripe. A supersampled per-pixel-alpha surface preserves smooth edges; no binary window Region clips the antialiasing. Firefox's live theme colors and CSS backdrop blur are not read by the external helper.
- Approach a visible edge for 120 ms. A single 14-DIP by at most 128-DIP capsule appears near the pointer, outside the entire native frame including invisible resize margins. Its position freezes until hidden, including during fade-out. Time-based smoothstep opacity gives a gentle 210 ms reveal and 180 ms dismissal after a 220 ms grace period; reduced animation settings skip the fade. Native resize borders and page content stay free.
- Partially offscreen windows use the same hover-only behavior, including after losing focus: no persistent grips. Each grip is constrained to a monitor's working area and the visible part of its edge. A missing monitor or gap cannot host a grip. Corners remain free. Only one grip is shown at a time; a new position/edge is selected after the previous grip disappears.
- Left press releases the helper's capture, focuses the target in response to the user's click, and posts `WM_SYSCOMMAND / SC_MOVE|HTCAPTION`. Windows performs movement, capture, snapping and Escape cancellation. The helper hides until the drag ends.
- Ctrl+Alt+F9 starts keyboard movement; press an arrow and then use arrows/mouse, Enter to finish, Escape to cancel. A maximized window is restored first.
- Ctrl+Alt+F10 centers the last active Firefox window on the monitor containing the pointer and reduces an oversized window to that monitor's work area. If the window is completely offscreen, the shortcut still works. With no remembered window, the first eligible Firefox window in native Z order is used.
- A separate labelled **↩ Firefox** return capsule appears only for a completely lost window or a clipped window with no room for an ordinary grip. Eligibility uses the same full native frame, monitor working areas and DPI-aware placement as ordinary grips; focus loss alone never triggers it. Small fully onscreen windows stay quiet. Partial recovery waits for 500 ms of stable geometry with the mouse released; completely lost windows skip that delay. No return capsule appears while the left button is held. It disappears immediately once ordinary access is restored. Its click targets the exact captured window. This non-activating topmost control sits near the current monitor's top edge; fullscreen applications, minimized/maximized target windows and pause suppress it.
- Display-configuration changes bring completely lost, non-minimized eligible windows back without stealing focus. Merely dragging a window partway offscreen does not teleport it.
- Fullscreen is deliberately left intact. Exit F11/video fullscreen before moving or recovering. A borderless window exactly matching a display is treated as fullscreen.
- Normal grips are owned, non-activating tool windows, not topmost windows. The exceptional return capsule is non-activating and topmost so a lost Firefox does not need focus to be restored. The helper does not collect URLs, page content, titles or account data. Optional diagnostics contain only window handles, geometry, hotkey status and counters.
- A hotkey registration conflict is surfaced as a tray notification; move/recover remain available from the tray menu. Exit stops the process; pause hides grips. Automatic startup is per-user, without administrator rights.

## Architecture and installation


The CSS theme keeps `no-drag` on the hidden navigation rectangle. The companion uses Windows Forms and Win32, compiled locally by the .NET Framework C# compiler already present on supported Windows installations. `GhostHandles.exe.config` enables PerMonitorV2 DPI awareness. All native placement coordinates are physical desktop pixels; handle dimensions scale with the target's DPI.

The executable enables PerMonitorV2 awareness before creating HWNDs, avoiding virtualized API coordinates differing from physical DWM bounds. Working areas, negative origins and monitor gaps constrain placement.

`Install.cmd` installs theme and companion. `Install-Handles.ps1` adds only the companion to an initialized profile, allowing existing Firefox sessions to stay open. `Uninstall-Handles.ps1 -ProfilePath <profile>` removes only the companion registration and keeps the theme. Binaries are content-addressed by source/config/manifest in `%LOCALAPPDATA%/FirefoxGhostUI/handles`. HKCU Run contains one quoted executable path. Registrations track profiles; uninstalling the last registered profile stops the owned process and removes the matching startup entry. A foreign startup entry is neither overwritten nor deleted. Cached binaries are retained inertly; there is no recursive deletion based on a manifest.



The tray menu offers pause, move, return and exit. Exit stops the current process; uninstall from every registered profile to remove sign-in registration. Companion-only removal leaves the theme intact; full removal uses `Uninstall.cmd` after closing Firefox.

## Verification and limitations

Automated tests cover geometry, negative coordinates, mixed DPI, gaps, clipping, complete loss, oversized-window containment, alpha edges and isolated installer lifetime. Offscreen WinForms fixtures check layered rendering, return-capsule binding, containment, unchanged foreground focus, actual recovery, disappearance and pause. They do not target personal Firefox or inject input.

The user confirmed dragging and recovery, then reported the remaining pointer checklist and final hover animations working on 2026-10-27. No separate per-scenario results were recorded. Earlier failed browser automation/capture attempts are not successful native verification. Fixtures and aggregate feedback do not independently establish every Firefox mouse-capture, virtual-desktop, fullscreen or mixed-monitor case.

For additional native verification, use a disposable Firefox profile:

1. Reveal and drag each grip; verify release, Escape cancellation and Windows snapping.
2. Check website edge buttons, native resize corners and tab dragging before/after reveal.
3. Recover clipped slivers, completely lost and oversized windows after switching applications.
4. Check maximize/restore, minimize, F11/video fullscreen, menus/dialogs, multiple windows and virtual desktops.
5. Check mixed-DPI monitors, negative origins, gaps and disconnection of the hosting display.

`GhostHandles.exe --demo --diagnostics <path>` provides a separate non-browser fixture. Optional diagnostics contain window handles, geometry, hotkey status, counters and timings; normal sign-in startup does not write them. No URLs, titles, browser content or account data are collected. Process identity uses a limited-information native query; compositor queries are restricted to matching window classes.

## References



- [Windows Forms high DPI support](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/high-dpi-support-in-windows-forms)
- [WM_SYSCOMMAND](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand)
- [DwmGetWindowAttribute](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmgetwindowattribute)
- [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)
