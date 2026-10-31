# Changelog

English release notes for Firefox Ghost UI. Unreleased work is not an available update.

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
