# Firefox Ghost UI

[Русский](README.ru.md) · [Download for Windows](https://github.com/DekrovDev/firefox-ghost-ui/releases/latest) · [Report an issue](https://github.com/DekrovDev/firefox-ghost-ui/issues)

A floating Firefox toolbar that stays out of the way, with native vertical tabs and optional Bonjourr animations.

- Hover at the top center: a small island appears, then opens after **725 ms**.
- **Ctrl+L** opens the address bar immediately.
- After you submit a search, the toolbar collapses when editing ends and suggestions close. A saved query no longer keeps it open.
- Menus, extension popups, and active typing keep the toolbar available.
- Native window buttons stay accessible independently of the toolbar.
- The sidebar expands on hover over the page, without shifting the page. Passive tab previews do not reveal the toolbar.
- The hidden toolbar passes clicks through, except for its 3-pixel activation strip at the top center.
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

## Optional extensions

The browser theme works without extensions. For the new-tab page and additional color/video effects:

- [Bonjourr](https://addons.mozilla.org/firefox/addon/bonjourr-startpage/) — new-tab page. After installing it, close Firefox and run **Install.cmd again** to apply its animations automatically. Enable Bonjourr's search widget in its own settings if you want central search.
- [Adaptive Tab Bar Colour](https://addons.mozilla.org/firefox/addon/adaptive-tab-bar-colour/) — toolbar colors follow the page.
- [Ambient Light for YouTube](https://addons.mozilla.org/firefox/addon/ambient-light-for-youtube/) — an optional YouTube effect.

Firefox asks you to confirm extension installation. Ghost UI does not bundle extensions or bypass that confirmation. Your existing Bonjourr settings are preserved; no personal wallpaper, location, account, or links are imported from this project.

## Update or uninstall

To update, download a newer release, extract it, close Firefox, and run **Install.cmd**. Reinstalling preserves the original pre-install backup and does not duplicate configuration blocks.

To remove Ghost UI, close Firefox and run **Uninstall.cmd**. It restores your original `userChrome.css`, `userContent.css`, `user.js`, and the previous values of the five Firefox preferences this installer manages. Other current preferences are retained. Backups remain inside the selected profile's `chrome/ghost-ui-backups/` folder.

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

- If Bonjourr's extension UUID exists in this profile, adds `userContent.css` scoped to that exact extension URL. No other pages or extensions receive these styles.

The installer does not write bookmark/history databases, replace the rest of your preferences, install add-ons, or terminate Firefox processes. Competing custom browser styles may need adjustment. Microsoft Store profiles or nonstandard locations can be selected explicitly with `-ProfilePath`.

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
./scripts/Build-Release.ps1 -Version 1.0.0
```

The tests use disposable profiles only and cover first install, updates, existing CSS and preferences, Bonjourr scoping, rollback, uninstall, profile selection, and protection of edited files. The release builder packages an explicit file list and generates SHA-256 checksums; profiles, personal backups, diagnostics, and Git metadata are excluded.

The browser behavior was also checked with real mouse/keyboard actions on Firefox 156.0.1: Ctrl+L, Enter, menus, extension popups, sidebar overlay, adaptive colors, fullscreen, customization, reduced motion, and native window commands at multiple window sizes.

MIT license. Firefox, Bonjourr, and the linked extensions are separate projects; this is an independent customization.
