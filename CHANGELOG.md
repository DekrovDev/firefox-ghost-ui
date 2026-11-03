# Changelog

English release notes for Firefox Ghost UI. Unreleased work is not an available update.

## [1.3.3] - 2026-11-03

### Summary
- Fit the update card to its actual viewport and avoid unnecessary scrollbars.
- Use a steady arrow cursor and a soft pointer-following border light.
- Present a recognized start page without an extra polling delay.

### Details
- Replace the fill sweep with a soft border light that follows the pointer's nearest edge.
- Reconcile the actual content viewport after resizing and keep horizontal scrolling at zero.
- Use more of the available Firefox height before enabling vertical overflow in small windows.
- Match necessary vertical scroll controls to the dark card instead of a white gutter.
- Refresh presentation immediately after profile and page identification completes.

## [1.3.2] - 2026-11-03

### Summary
- Show the update button only on Bonjourr and Firefox home or new-tab pages.
- Use a lighter, more transparent button with a smooth hover highlight.

### Details
- Hide the button on websites, blank pages, private pages and unidentified documents.
- Keep Ctrl+Alt+F8 available for manually opened update settings on other Firefox tabs.
- Identify start pages even when daily checks are disabled or no update is available.
- Sweep the button highlight from left to right and strengthen its border on hover.
- Stop hover motion when hidden and respect Windows animation and high-contrast preferences.

## [1.3.1] - 2026-11-03

### Summary
- Open update settings with a compact, neutral launcher that suits any wallpaper.
- Read complete update summaries in a smaller, clearer card.
- Keep update checks handy without an inactive Download button when already up to date.

### Details
- Use rounded native controls, neutral graphite surfaces and Windows system icons.
- Measure summary and status text before placing profile controls and actions.
- Preserve scrolling, explicit profile selection and separate download and installation consent.
- Keep Check now, Check daily and manual release links reachable in every update state.
- Restore the update card on the remaining Firefox window after its previous owner closes.

## [1.3.0] - 2026-11-02

### Summary
- Use all Ghost UI features and update controls with ordinary Firefox, without Bonjourr.
- Open update settings on any Firefox tab with the compact button or Ctrl+Alt+F8.
- Choose the update profile explicitly when a start page cannot identify it.

### Details
- Keep one installer and release channel; Bonjourr styling remains optional.
- Recognize native Firefox home and new-tab pages for automatic update cards.
- Discover installed profiles independently of optional stylesheets, including older installations.
- Preserve Firefox homepage settings, cached downloads, backups and explicit installation consent.
- Wait for a starting companion's stop signal before uninstalling, without force-closing it.
- Isolate malformed or inaccessible optional/profile metadata from healthy installations.
- Preserve Later reminders independently across Firefox processes and explicit profile choices on F8.
- Release the update shortcut outside eligible Firefox windows and require a new choice on ambiguity.
- Keep update controls inside the visible Firefox area when its window extends offscreen.
- Activate manual settings on a real launcher click while automatic cards stay nonactivating.
- Validate automatically selected profile paths before creating installation checkpoints.

## [1.2.1] - 2026-10-31

### Summary
- Recover a damaged update download without switching to manual installation.
- Keep update settings reachable when daily checks are disabled.
- Require updater and Firefox checks before publishing a release.

### Details
- Atomically replace a damaged cached package only after verifying its replacement; retain the old file.
- Show a small Ghost UI updates button on Bonjourr when daily checks are disabled.
- Open the full card from that button to check manually or enable daily checks again.
- Use Firefox's actual browser process ID in the foreground-window regression fixture.
- Publish the tested package only after the shared Windows and Linux checks succeed.

## [1.2.0] - 2026-10-31

### Summary
- Show update summaries and download controls inside the Firefox start page.
- Remind you about downloaded updates next session; install only after confirmation.
- Offer manual downloads alongside the built-in download and installation flow.

### Details
- Keep short update descriptions in this file and use them for GitHub Release notes.
- Require a versioned summary before publishing a release.
- Show a companion-owned card over positively identified Bonjourr pages without browser injection.
- Show the update card even when Firefox walks its accessibility tree slowly.
- Verify GitHub package digests, limit downloads and safely extract packages before installation.
- Keep profile selection, backups, managed-file conflict checks and rollback in the existing installer.
- Support long update-cache and staging paths without changing Windows system policy.
- Keep the update helper alive while the installer replaces the running companion.
- Restore the update card if installation stops before the companion exits.
- The update card and installer handoff were validated in real Firefox sessions.
- Enable TLS 1.2 explicitly alongside system protocols for GitHub update requests.

Historical release notes remain on [GitHub Releases](https://github.com/DekrovDev/firefox-ghost-ui/releases).
