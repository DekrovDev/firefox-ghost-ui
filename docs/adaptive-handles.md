# Adaptive window grips

Status: 1.1.0 development. On 2026-10-27 the user confirmed the recovery changes, the remaining checks and the final hover-only animated grips work, and authorized pushing the source changes. No release publication is implied.

## Behavior

- Foreground desktop Firefox windows receive hover grips. A partially offscreen last-active Firefox retains its reachable recovery grips after losing focus. Fully onscreen background windows, minimized windows, other virtual desktops, dialogs, other applications, maximized windows and fullscreen do not receive overlays.
- Grips use the search pill's dark #17171b fallback surface, a thin white rim and fully rounded semicircular ends. There is no cyan glow or inner light stripe. A supersampled per-pixel-alpha surface preserves smooth edges; no binary window Region clips the antialiasing. Firefox's live Adaptive Tab Bar Colour values and CSS backdrop blur are not read by the external helper.
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

The executable also explicitly enables native PerMonitorV2 awareness before creating any HWND. A real runtime check found that relying solely on the WinForms config in a standalone csc build left screen/window APIs virtualized while DWM bounds remained physical. The explicit initialization and a native awareness assertion prevent that mismatch; on this laptop diagnostics now report DPI 120 and a physical 1920×1080 work area.

`Install.cmd` installs theme and companion. `Install-Handles.ps1` adds only the companion to an initialized profile, allowing existing Firefox sessions to stay open. `Uninstall-Handles.ps1 -ProfilePath <profile>` removes only the companion registration and keeps the theme. Binaries are content-addressed by source/config/manifest in `%LOCALAPPDATA%/FirefoxGhostUI/handles`. HKCU Run contains one quoted executable path. Registrations track profiles; uninstalling the last registered profile stops the owned process and removes the matching startup entry. A foreign startup entry is neither overwritten nor deleted. Cached binaries are retained inertly; there is no recursive deletion based on a manifest.

## Evidence and remaining checks

`tests/Handles.Tests.ps1` compiles actual source and checks installation, repeat installation, multi-profile lifetime, quoted startup, removal and foreign-entry preservation in isolated folders and a unique test registry key. The compiled geometry tests cover left/right/top/bottom clipping, negative coordinates, mixed DPI, monitor gaps, complete loss, oversized windows and recovery containment. Theme installer tests remain separate.

These tests do **not** prove native mouse capture or successful movement in personal Firefox. On 2026-10-25 Computer Use refused Firefox because browser URL policy enforcement was unsupported. A separately created demo window was targetable but screenshot capture failed twice (`FrameArrived timed out`, then `window capture timed out`). No alternate browser input route was used.

The user then confirmed in personal Firefox that a grip appears, Firefox moves while the mouse button is held, and stops immediately on release. The user requested matching the search bar's style; the cyan design was replaced and the updated component applied without restarting Firefox.

The user also confirmed the left-hand grip remains reachable when Firefox is moved partly offscreen to the right, and Ctrl+Alt+F10 returns it. On further visual feedback, the hard-clipped outline was replaced with the current smooth, filled alpha capsule. Automated checks now include renderer alpha edges at several DPI sizes and a real `UpdateLayeredWindow` smoke check on an isolated offscreen fixture, without input injection.

Further user feedback identified the focus-loss escape problem: fully moving Firefox offscreen and clicking elsewhere hid all normal grips. The return capsule above is the implemented correction. Automated rescue-policy checks cover an active lost window, an inactive lost window, an inactive clipped window, and suppression for normal on-screen windows. Interactive confirmation of this correction is pending. The fill was also made slightly lighter at the user's request.

Native regression checks now additionally create an isolated transparent WinForms fixture outside the physical desktop and exercise the real controller while an unrelated window keeps focus. They verify the visible, correctly bound return capsule, physical work-area containment, unchanged foreground focus, actual native repositioning, disappearance after recovery, inactive partial clipping, and pause. These run without mouse/key injection, Firefox targeting, hotkey registration or tray notifications. Current totals: 324 compiled checks and 17 isolated installation checks. The end-to-end ZIP install/remove check also passes. This is stronger evidence for the component's focus-loss path; confirmation of the exact reported case in personal Firefox is still pending.

Runtime profiling also found an expensive .NET Framework `Process.ProcessName` identity query in each foreground poll. It is replaced with `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` plus `QueryFullProcessImageName`, and compositor queries are restricted to matching window classes. On the same active Firefox window the observed poll dropped from about 20 ms to 0.26 ms, with no render/present loop. Direct process identity and safe rejection of a missing process are covered by native checks. Optional diagnostics include tick/render/present counts and timings; normal installed startup does not write these diagnostics.

Original Windows pointer-interaction checklist (the user subsequently reported the remaining checks pass on 2026-10-27; no separate per-scenario results were recorded):

1. Reveal each grip, drag it, confirm Firefox's position changes and the helper does not take focus.
2. Click website buttons near all edges before/after reveal. No content is covered, shifted or intercepted.
3. Resize using native edges/corners and drag tabs; no unexpected grip capture.
4. Move Firefox partly beyond each screen edge. Use a remaining grip to retrieve it. Test a narrow visible sliver.
5. Move it entirely offscreen and use Ctrl+Alt+F10; check an oversized window too.
6. Check maximize/restore, F11, fullscreen video, minimize, switching to other applications, Firefox menus/dialogs, multiple Firefox windows and virtual desktops.
7. Test two monitors with different DPI, negative origins and a gap; disconnect the monitor hosting Firefox.
8. Finish/cancel a native drag, including Escape and Windows snapping. Ensure a grip never changes position under a held pointer.

`GhostHandles.exe --demo --diagnostics <path>` provides a separate, non-browser fixture with clipping buttons and a clickable page button. It uses the same placement and move code but cannot establish Firefox-specific behavior on its own.

## References

- [Windows Forms high DPI support](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/high-dpi-support-in-windows-forms)
- [WM_SYSCOMMAND](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand)
- [DwmGetWindowAttribute](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmgetwindowattribute)
- [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)
