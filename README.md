# Firefox Ghost UI

[Русский](README.ru.md) · [Download for Windows](https://github.com/DekrovDev/firefox-ghost-ui/releases/latest) · [Report an issue](https://github.com/DekrovDev/firefox-ghost-ui/issues)

A floating Firefox toolbar that stays out of the way, with native vertical tabs and optional Bonjourr animations.

**Extensions play a major role in the appearance.** Ghost UI provides the floating controls, sidebar layout and animations; the [extensions below](#extensions-and-the-complete-look) add the new-tab background and widgets, adaptive browser colors, and optional lighting around YouTube videos. Configure them alongside the theme to get the complete look.

![Firefox Ghost UI with a clean new tab, central search and a collapsed sidebar](docs/images/new-tab.png)

**A clean new tab.** The toolbar stays hidden while the central Bonjourr search remains available.

<details>
<summary>See the floating toolbar and expanded sidebar</summary>

**Search when you need it.** Press Ctrl+L to open the native address bar immediately.

![Firefox Ghost UI with the floating toolbar open and an active search](docs/images/floating-toolbar.png)

**Tabs within reach.** Hover over the sidebar to reveal tab titles without shifting the page or opening the toolbar.

![Firefox Ghost UI with an expanded sidebar and the navigation toolbar hidden](docs/images/sidebar.png)

</details>

Screenshots use an English demo profile with optional [Bonjourr](https://addons.mozilla.org/firefox/addon/bonjourr-startpage/). Its wallpaper, clock, weather and central search are configured separately; the installer preserves your own settings and language. The [background video](https://pixabay.com/videos/id-83880/) comes from Bonjourr's media library and is not bundled with the installer.

## Features

- Hover anywhere along the usable top edge, or over the visible center pill. Opening starts after **120 ms** and the controls appear within about half a second.
- **Ctrl+L** opens the address bar immediately.
- After you submit a search, the toolbar collapses when editing ends and suggestions close. A saved query no longer keeps it open.
- Menus, extension popups, and active typing keep the toolbar available.
- Moving away keeps all controls visible for **2 seconds**, then fades them together. Returning cancels closing. Submitting an address skips this wait and plays a short fade and collapse animation (about 0.3 seconds).
- A native bookmarks menu button opens your saved sites. Sites from the old bookmarks bar are under **Bookmarks Toolbar** in that menu.
- Downloads appear in a separate floating island. An arrow settles into its tray above a thin progress track; on completion, the same track smoothly folds into a check as the arrow fades away. It appears briefly when a download starts, retracts while the download continues, and appears again on completion; hovering or opening its list keeps it available. Click the island to view files without revealing the address bar. Download warnings remain accessible; reduced motion uses a static completion check.
- Native window buttons stay hidden until you hover over the top-right corner: a 16-pixel strip in a restored window, or 8 pixels when maximized. The larger restored-window strip extends below the native resize frame. They fade in independently of the toolbar, stay available while you use them, and hide after you leave. Hidden buttons pass clicks through to the page below the activation strip.
- The sidebar expands on hover over the page, without shifting the page. While a side tool is open, tabs stay in a compact icon rail so its controls remain visible and stationary. Closing the tool restores normal hover expansion. Passive tab previews do not reveal the toolbar.
- The hidden toolbar passes clicks through outside its visible center pill and 3-pixel top-edge activation strip. Window controls have a separate activation strip at the top right (16 pixels in a restored window, 8 when maximized).
- F11 and fullscreen video hide the floating controls and sidebar.
- Supports reduced motion and Adaptive Tab Bar Colour theme colors.
- Optional Bonjourr animations preserve its wallpaper, search, links, and settings.

## Install automatically on Windows

1. Install desktop Firefox and open it once to create a profile.
2. [Download the latest Windows ZIP](https://github.com/DekrovDev/firefox-ghost-ui/releases/latest), then **extract the entire archive**.
3. Double-click **Install.cmd**. If you have multiple profiles, choose the one you use.
4. Close Firefox normally when asked, then press Enter in the installer.
5. Start Firefox.

No administrator access, Git, Python, or additional PowerShell modules are needed. The installer uses Windows PowerShell 5.1 already included with Windows. It operates locally and saves backups before writing files. The CMD launcher permits this script for its own process only; it does not change the machine's execution policy.

**Compatibility:** designed and visually tested on Firefox **156.0.1 for Windows**. Installer checks run on Windows PowerShell 5.1 and PowerShell 7. Other Firefox versions and operating systems have not been verified.

## Extensions and the complete look

For a setup close to the screenshots, use **Bonjourr** for the new-tab page and **Adaptive Tab Bar Colour** for browser colors that blend with the current website. **Ambient Light for YouTube** adds an optional video effect. Ghost UI also works on its own; the extensions contribute these parts of the appearance:

- [Bonjourr](https://addons.mozilla.org/firefox/addon/bonjourr-startpage/) — the wallpaper or video background, clock, weather, greeting, quotes, quick links and central search on the new-tab page. These are configured in Bonjourr. After installing it, close Firefox and run **Install.cmd again** to apply Ghost UI's animations automatically. Enable its search widget if you want central search.
- [Adaptive Tab Bar Colour](https://addons.mozilla.org/firefox/addon/adaptive-tab-bar-colour/) — matches Firefox's theme colors to the current website. Ghost UI uses those colors for its surfaces, helping the floating toolbar and sidebar blend with the page. Recommended for the adaptive color effect.
- [Ambient Light for YouTube](https://addons.mozilla.org/firefox/addon/ambient-light-for-youtube/) — adds lighting around YouTube videos based on the image being played. It enhances the video page and is optional for the rest of the browser layout.

Firefox asks you to confirm extension installation. Ghost UI does not bundle extensions or bypass that confirmation. Your existing Bonjourr settings are preserved; no personal wallpaper, location, account, or links are imported from this project.

To show Bonjourr when Firefox starts or opens a new window, open **Settings → Home** and select **Bonjourr** under **Homepage and new windows** as well as **New tabs**. These are separate settings: choosing Bonjourr for new tabs alone can leave Firefox Home at startup, which looks empty if its widgets are disabled. The installer preserves your homepage and session-restore preferences.

## Bonjourr credits and official sources

[Bonjourr](https://bonjourr.fr/) is created by **[Tahoe Beetschen (morceaudebois)](https://github.com/morceaudebois)** and **[Victor Azevedo (victrme)](https://github.com/victrme)**, with help from its [contributors](https://github.com/victrme/Bonjourr/graphs/contributors). Thank you to its authors and community for the startpage used in this setup.

- [Official website](https://bonjourr.fr/)
- [Original source code — victrme/Bonjourr](https://github.com/victrme/Bonjourr)
- [Official documentation](https://bonjourr.fr/docs/)
- [Install Bonjourr for Firefox — Mozilla Add-ons](https://addons.mozilla.org/firefox/addon/bonjourr-startpage/)

Bonjourr provides the new-tab backgrounds, clock, weather, greeting, quotes, quick links and central search shown in this setup. Firefox Ghost UI adds its own optional CSS styling and animations for compatibility with that page. **Bonjourr is installed separately from the official source; the extension itself is not redistributed or bundled with the Ghost UI installer.**

Bonjourr has its own [GPL-3.0 license](https://github.com/victrme/Bonjourr/blob/master/LICENSE.md). The MIT license in this repository applies to Ghost UI's own code.

## Update or uninstall

To update, download a newer release, extract it, close Firefox, and run **Install.cmd**. Reinstalling preserves the original pre-install backup and does not duplicate configuration blocks.

To remove Ghost UI, close Firefox and run **Uninstall.cmd**. It restores your original `userChrome.css`, `userContent.css`, `user.js`, and the previous values of the seven Firefox preferences this installer manages. Other current preferences are retained. Backups remain inside the selected profile's `chrome/ghost-ui-backups/` folder.

If a managed file has been manually edited, update/uninstall stops before overwriting it. Save those edits or restore the managed file first; your original backups remain available for manual recovery.

## What the installer changes

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

## Advanced usage

```powershell
./Install.ps1 -ProfilePath 'D:\Firefox Profiles\Personal' -NoPrompt
./Uninstall.ps1 -ProfilePath 'D:\Firefox Profiles\Personal' -NoPrompt
```

Firefox must be closed first. Without an explicit path, `-NoPrompt` selects the only profile or a unique installation default; otherwise it stops rather than guessing.

To adjust the hover delay, edit `--ghost-open-wait` in `theme/userChrome.css` **before** installing. If you edit already installed files, save your changes before using the installer again.

## Development

```powershell
./tests/Installer.Tests.ps1
./scripts/Build-Release.ps1 -Version 1.0.9
```

The tests use disposable profiles only and cover first install, updates, existing CSS and preferences, Bonjourr scoping, rollback, uninstall, profile selection, and protection of edited files. The release builder packages an explicit file list and generates SHA-256 checksums; profiles, personal backups, diagnostics, and Git metadata are excluded.

The browser behavior was also checked with real mouse/keyboard actions on Firefox 156.0.1: Ctrl+L, Enter, menus, extension popups, sidebar overlay, adaptive colors, fullscreen, customization, reduced motion, and native window commands at multiple window sizes.

The download-island changes in **1.0.9** were additionally checked on **Firefox 157.0.1 for Windows**: real downloads, brief start/finish notices, file-list clicks, safety badges, reduced motion and alignment at 100%, 125% and 150% scaling. These are targeted download checks; the earlier full-interface checks above used 156.0.1.

MIT license. Firefox, Bonjourr, and the linked extensions are separate projects; this is an independent customization.
