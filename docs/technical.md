# Technical reference

[Back to README](../README.md) | [Window companion](adaptive-handles.md) | [Extensions workaround](extensions-panel-workaround.md)

## CSS architecture and interaction

`theme/userChrome.css` styles Firefox chrome; `theme/bonjourr.css` is scoped to the installed Bonjourr extension UUID. The glass sits behind stationary native control hit areas and popup anchors. The navigation rectangle explicitly disables native window dragging: pointer-event transparency alone does not disable the titlebar drag region. Submission collapse preserves a minimum 120-pixel address field in narrow windows and yields optional controls to overflow; customization/fullscreen disable submission animations.

- Hover anywhere along the usable top edge, or over the visible center pill. Opening starts after **120 ms** and the controls appear within about half a second.
- Briefly crossing the edge on the way to a page header does not enable an invisible toolbar. After deliberate hovering, a growing hit area follows the visible glass so you can move into it while it opens. Controls become clickable as they appear; the full toolbar area activates after opening completes. The activation strip and center pill keep their size.
- **Ctrl+L** opens the address bar immediately.
- After you submit a search, the toolbar collapses when editing ends and suggestions close. A saved query no longer keeps it open.
- Menus, extension popups, and active typing keep the toolbar available. Button hit areas and popup anchors stay stationary during animations; only their artwork moves. Opening a popup cancels the icon stagger immediately.
- Moving away keeps all controls visible for **2 seconds**, then fades them together. Returning cancels closing. Submitting an address skips this wait and plays a short fade and collapse animation (about 0.3 seconds).
- A native bookmarks menu button opens your saved sites. Sites from the old bookmarks bar are under **Bookmarks Toolbar** in that menu.
- Downloads appear in a separate floating island. An arrow settles into its tray above a thin progress track; on completion, the same track smoothly folds into a check as the arrow fades away. It appears briefly when a download starts, retracts while the download continues, and appears again on completion; hovering or opening its list keeps it available. Click the island to view files without revealing the address bar. Download warnings remain accessible; reduced motion uses a static completion check.
- Native window buttons stay hidden until you hover over the top-right corner: a 16-pixel strip in a restored window, or 8 pixels when maximized. The larger restored-window strip extends below the native resize frame. They fade in independently of the toolbar, stay available while you use them, and hide after you leave. Hidden buttons pass clicks through to the page below the activation strip.
- The sidebar expands on hover over the page, without shifting the page. While a side tool is open, tabs stay in a compact icon rail so its controls remain visible and stationary. Closing the tool restores normal hover expansion. Passive tab previews do not reveal the toolbar.
- The hidden toolbar passes clicks through outside its visible center pill and 3-pixel top-edge activation strip. Window controls have a separate activation strip at the top right (16 pixels in a restored window, 8 when maximized).
- The floating navigation rectangle is excluded from native window dragging, including when transparent. Its empty space no longer serves as a window-drag handle.
- F11 and fullscreen video hide the floating controls and sidebar.
- Supports reduced motion and Firefox theme colors.
- Optional Bonjourr animations preserve its wallpaper, search, links, and settings.

## Controlled preferences and files

- Copies the theme into `chrome/ghost-ui/` in the selected profile.
- Uses CSS import wrappers, keeping existing custom CSS in the same `chrome/` directory so relative assets and imports still resolve. A previous manually installed Ghost UI is backed up and replaced instead of loaded twice.
- Adds a managed block to `user.js`:

| Preference | Installed value |
| --- | --- |
| `toolkit.legacyUserProfileCustomizations.stylesheets` | `true` |
| `sidebar.revamp` | `true` |
| `sidebar.verticalTabs` | `true` |
| `sidebar.visibility` | `expand-on-hover` |
| `browser.tabs.inTitlebar` | `1` |
| `browser.download.alwaysOpenPanel` | `false` |
| `browser.download.panel.shown` | `true` |

- If Bonjourr's extension UUID exists in this profile, adds `userContent.css` scoped to that exact extension URL. No other pages or extensions receive these styles.

The installer adds the native bookmarks menu button if it is absent, preserving existing toolbar placements. Uninstall removes only this addition and keeps later toolbar changes. A preexisting button or a layout enforced through `user.js` is left as configured; you can add **Bookmarks Menu** manually through Customize Toolbar.

The installer does not write bookmark/history databases, replace the rest of your preferences, install add-ons, or terminate Firefox processes. Competing custom browser styles may need adjustment. Microsoft Store profiles or nonstandard locations can be selected explicitly with `-ProfilePath`.


The download island uses Firefox’s native **Downloads** button in the navigation toolbar. If you moved it into the overflow menu, move it back using **Customize Toolbar**. The installer disables automatic opening of the file list (including the first-download introduction); uninstall restores both previous download-panel settings.

## Profile safety

Update preserves the original pre-install backup. Uninstall restores original `userChrome.css`, `userContent.css`, `user.js` and previous values of the seven managed preferences, retaining unrelated current preferences. Backups remain in `chrome/ghost-ui-backups/`. Managed file edits block update/uninstall; save edits separately or restore managed files first. Close Firefox normally before theme changes. The CMD launcher sets execution policy only for its own process.

## Commands and customization

```powershell
./Install.ps1 -ProfilePath 'D:\Firefox Profiles\Personal' -NoPrompt
./Uninstall.ps1 -ProfilePath 'D:\Firefox Profiles\Personal' -NoPrompt
```

Firefox must be closed first. Without an explicit path, `-NoPrompt` selects the only profile or a unique installation default; otherwise it stops rather than guessing.

To adjust the hover delay, edit `--ghost-open-wait` in `theme/userChrome.css` **before** installing. If you edit already installed files, save your changes before using the installer again.

## Troubleshooting

### Extensions panel flashes or does not open from the menu

If **☰ → Extensions** closes the menu but does not open a lasting extensions panel, [this optional CSS workaround](extensions-panel-workaround.md) may help. The affected user confirmed it on Firefox 158.0.1 for Windows with the extensions button set to auto-hide. The guide includes the exact rule, backup and rollback steps, and its verification limits. Apply it only if you encounter the symptom.

Companion-only installation can run without restarting Firefox:

```powershell
./Install-Handles.ps1 -ProfilePath 'D:\Firefox Profiles\Personal'
./Uninstall-Handles.ps1 -ProfilePath 'D:\Firefox Profiles\Personal'
```

## Verification scope

Recorded visual checks used Firefox 156.0.1 for Windows; targeted download checks used 157.0.1 at 100%, 125% and 150% scaling; disposable headless toolbar checks used 158.0. These are observations, not a compatibility guarantee for every Firefox version or other operating systems.

The toolbar harness checks coordinate hit targets, menus, extension popups, subviews, settings/add-ons tabs, navigation, keyboard focus, find, sidebar tools, narrow windows, fullscreen, reduced motion and hidden-toolbar click-through. Brief edge crossings and entry into the growing glass are covered. Direct element clicks alone missed interception bugs; native XUL popup items still use element clicks because their separate widget is absent from the main document's headless hit test.

Headless checks do not establish Windows non-client hit testing, personal extension configuration, sign-in or OS dialogs. The menu-to-extensions-popup transition with an automatically hidden button has a separate [workaround guide](extensions-panel-workaround.md). Companion evidence and remaining cases are in the [window guide](adaptive-handles.md#verification-and-limitations).

## Development and packaging

Run from a source checkout; developer tests/build scripts are not shipped in the user ZIP:

```powershell
./tests/Installer.Tests.ps1
./tests/Handles.Tests.ps1
./tests/Package.Tests.ps1
./scripts/Build-Release.ps1
# Optional browser checks in a disposable profile:
python ./tests/Toolbar.Tests.py --firefox 'C:/Program Files/Mozilla Firefox/firefox.exe'
```

Installer tests use disposable profiles and cover first install, updates, custom CSS/preferences, Bonjourr scoping, rollback, uninstall, profile selection and edit protection. Companion tests use isolated folders and a unique test registry key. Package tests compile, install and remove shipped source in isolation.

`VERSION` is the tracked numeric stable SemVer source. The builder reads it by default; `-Version` permits an explicit numeric override for local builds. The explicit whitelist includes linked guides and screenshots, excludes profiles, backups, diagnostics and Git metadata, and produces `firefox-ghost-ui-<version>-windows.zip` plus `SHA256SUMS.txt`.

Publishing requires an intentional real `vX.Y.Z` tag matching `VERSION`. The release workflow runs Windows tests and verifies SHA-256; it refuses to replace an existing release. Main/documentation pushes never publish releases. Historical notes remain in [GitHub Releases](https://github.com/DekrovDev/firefox-ghost-ui/releases).

## Third-party credits

[Bonjourr](https://bonjourr.fr/) is created by **[Tahoe Beetschen (morceaudebois)](https://github.com/morceaudebois)** and **[Victor Azevedo (victrme)](https://github.com/victrme)**, with help from its [contributors](https://github.com/victrme/Bonjourr/graphs/contributors). Thank you to its authors and community for the startpage used in this setup.

Tahoe gave permission for the Ghost UI compatibility integration on the basis of author attribution and official links, separate installation without redistributing Bonjourr, and removal of the integration on request. This permission does not imply an official partnership or change Bonjourr's license.

- [Official website](https://bonjourr.fr/)
- [Original source code — victrme/Bonjourr](https://github.com/victrme/Bonjourr)
- [Official documentation](https://bonjourr.fr/docs/)
- [Install Bonjourr for Firefox — Mozilla Add-ons](https://addons.mozilla.org/firefox/addon/bonjourr-startpage/)

Bonjourr provides the new-tab backgrounds, clock, weather, greeting, quotes, quick links and central search shown in this setup. Firefox Ghost UI adds its own optional CSS styling and animations for compatibility with that page. **Bonjourr is installed separately from the official source; the extension itself is not redistributed or bundled with the Ghost UI installer.**

Bonjourr has its own [GPL-3.0 license](https://github.com/victrme/Bonjourr/blob/master/LICENSE.md). The MIT license in this repository applies to Ghost UI's own code.
