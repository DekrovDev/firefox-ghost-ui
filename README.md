# Firefox Ghost UI

A floating Firefox toolbar, hover sidebar and adaptive Windows window grips that keep browser controls within reach while leaving more room for the page.

[Download Latest](https://github.com/DekrovDev/firefox-ghost-ui/releases/latest) | [Documentation](docs/technical.md) | [Report Issue](https://github.com/DekrovDev/firefox-ghost-ui/issues)

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

- Floating navigation toolbar: hover at the top edge or pill, or press **Ctrl+L** to search immediately.
- Native vertical tabs in a sidebar that expands over the page without shifting it.
- A separate download island with progress, completion feedback and Firefox's file list.
- Native window buttons revealed by hovering at the top-right corner.
- External window grips for native dragging; **Ctrl+Alt+F9** starts keyboard movement and **Ctrl+Alt+F10** recovers an offscreen window.
- Controls hide in F11/video fullscreen; reduced motion is supported. Exit fullscreen before window recovery.
- Optional Bonjourr styling and animations that preserve your startpage settings.

## Quick install

**Windows only as verified.** Normal installation needs no administrator access, Git, Python or extra PowerShell modules. See [tested versions and limits](docs/technical.md#verification-scope).

1. Install desktop Firefox and open it once to create a profile.
2. [Download the latest Windows ZIP](https://github.com/DekrovDev/firefox-ghost-ui/releases/latest) and **extract the entire archive**.
3. Double-click **Install.cmd** and choose your Firefox profile if asked.
4. Close Firefox normally when requested, press Enter in the installer, then start Firefox again.

The installer saves backups and preserves existing settings. It installs a per-user Windows companion that starts at sign-in, supplies grips and recovery shortcuts, and offers pause, move, return and exit in its tray menu. It does not read browser content or inject code into pages. [Companion setup and limitations](docs/adaptive-handles.md).

## Update and uninstall

The companion can show an English update card inside your Bonjourr start page, including a short list of changes. **Download** prepares a package; **Install update** and its confirmation approve installation into the selected profile. Close Firefox normally when instructed. A downloaded update is offered again next Firefox session after **Later**; it is never installed merely because Firefox or Windows starts. **Download manually** opens the official release page for the ZIP method below. See [update behavior, trust and verification limits](docs/updates.md).

To update, extract the newer release, close Firefox and run **Install.cmd**. The original pre-install backup is preserved; managed configuration blocks are not duplicated.

To uninstall, close Firefox and run **Uninstall.cmd**. Original managed files and preference values are restored while other current preferences are retained; backups remain in the profile's `chrome/ghost-ui-backups/` folder. Managed file edits block update/uninstall: save your edits or restore the managed file first. [Profile safety](docs/technical.md#profile-safety).

## Bonjourr integration

[Bonjourr](https://bonjourr.fr/) is created by **[Tahoe Beetschen (morceaudebois)](https://github.com/morceaudebois)** and **[Victor Azevedo (victrme)](https://github.com/victrme)**. Tahoe gave permission for this compatibility integration, with attribution, separate installation and removal on request. Thank you for supporting the project! [Original Bonjourr source](https://github.com/victrme/Bonjourr).

Bonjourr is optional and **installed separately** from its official source; it is not bundled with Ghost UI.

- [Bonjourr](https://addons.mozilla.org/firefox/addon/bonjourr-startpage/) provides the screenshot's wallpaper, clock, weather and central search. Configure it yourself, then close Firefox and rerun **Install.cmd** for Ghost UI animations. Existing settings are preserved.

For Bonjourr at startup, choose it under both **Homepage and new windows** and **New tabs** in **Settings > Home**. The installer preserves homepage and session-restore preferences.

## Troubleshooting and advanced docs

- [Extensions panel closes immediately: optional workaround](docs/extensions-panel-workaround.md)
- [Window grips, recovery, DPI and tray controls](docs/adaptive-handles.md)
- [Technical reference: profiles, preferences, toolbar and development](docs/technical.md)
- [Update card, manual installation and release notes](docs/updates.md)
- [Release history](https://github.com/DekrovDev/firefox-ghost-ui/releases)

## License and credits

[MIT](LICENSE) for Ghost UI's own code. This is an independent Firefox customization. [Bonjourr](https://bonjourr.fr/) is a separate project with its own license; see [authors and sources](docs/technical.md#third-party-credits).
