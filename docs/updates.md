# Updates

Ghost UI checks the public [GitHub Releases](https://github.com/DekrovDev/firefox-ghost-ui/releases) API after the companion starts and at most once a day after a successful check. Failed requests back off. No GitHub account or token is needed. GitHub receives the usual network request information, including your IP address; no profile paths or browser content are sent.

## Where the card appears

The Windows companion displays an owned, non-taskbar update card in the lower-right corner of an identified Bonjourr start page in the foreground Firefox window. It is a native overlay, not a modification of Bonjourr. It does not use Windows update notifications or add an update item to the tray. The card hides when the page cannot be positively identified, the window is too small, Firefox is minimized, another application is active, or the browser is fullscreen.

The companion reads accessibility roles/states and a document URL to match the Bonjourr origin already recorded in the installed profile's scoped stylesheet. It does not read page text, inject scripts or modify another extension. Accessibility availability and current Firefox behavior must be verified on supported Windows/Firefox builds; if identification fails, the card stays hidden. Tab transitions may have a brief polling delay. Other start pages are not currently supported.

The card shows the new and installed versions, a short English summary, a profile selector, and these actions:

- **Download** downloads and verifies the Windows ZIP. It does not change the installed version.
- **Download manually** opens the official release page. Download and extract the whole Windows ZIP, close Firefox normally, and run `Install.cmd`.
- **All changes** opens the full release notes on GitHub. If you skipped several releases, this summary describes the offered release; consult the full release history for earlier changes.
- **Later** or the close button hides the card for the current Firefox process session. It reappears when you next start Firefox and open the Bonjourr page. Opening another new tab in the same session does not repeat a dismissed card.
- **Check daily** controls automatic checks. **Check now** requests a check when the API cooldown permits it.

When daily checks are disabled, a small **Ghost UI updates** button remains on the identified Bonjourr page after the card is dismissed or the profile is up to date. Click it to open settings, use **Check now**, or enable **Check daily** again. It follows the same Firefox-only visibility rules as the card.

## Install a downloaded update

A valid cached package is detected and verified after restarting the companion. The card then says **Ready to install** and offers **Install update** instead of Download. Missing or invalid packages are not offered for installation. Retrying **Download** verifies the new file before atomically replacing a damaged cache; the damaged file is retained with an `.invalid` suffix for diagnosis.

1. Select the registered profile to update. The selector makes the destination explicit; only that profile is updated.
2. Click **Install update**, then **Confirm install** after reviewing the destination and the instruction to close Firefox.
3. Close all Firefox windows normally within two minutes. The approved installation proceeds while the companion helper remains running. **Cancel**, **Later**, expiry, or exiting the companion revokes this approval. Firefox is never forcefully closed. Approval is not saved as an install-on-startup request.
4. Start Firefox manually after installation. Other registered profiles retain their installed versions until separately updated.

Installation uses the existing installer, with its backups, managed-file edit protection and rollback handling. The helper uses the same process-scoped PowerShell execution-policy argument as `Install.cmd`; it does not change machine/user policy or request elevation. A policy enforced by the organization can still block installation. Failures retain the cache and recovery checkpoints for investigation; they do not schedule another installation.

## Package trust and limits

Only stable, published Latest releases with a canonical `vX.Y.Z` tag and the matching Windows ZIP are offered. Versions are compared numerically. The embedded download requires the asset's SHA-256 digest from the GitHub API, an exact expected initial URL, allowed HTTPS GitHub asset redirects, a matching byte count and hash, bounded archive entries and sizes, safe extraction paths, and the expected packaged version. Packages without the API digest remain available through **Download manually**.

The trust source is this GitHub repository over HTTPS. The API digest detects corruption or a mismatched asset; it is not an independent publisher signature and does not protect against compromise of the repository or GitHub. Cached files and staging are not a security boundary against another process already running as the same Windows user. Do not treat local checks as a cryptographic signing system.

State and downloaded packages are stored in `%LOCALAPPDATA%\FirefoxGhostUI\updates`. The stored state includes release metadata, check timing, dismissal and a short installation result; it never contains install consent. Cached/staged files are retained rather than recursively deleted using metadata. Removing the companion disables these checks.

## Preparing a release

Agents must read the root [AGENTS.md](../AGENTS.md) in the source checkout. Keep **English-only** user-facing notes in [CHANGELOG.md](../CHANGELOG.md), under `Unreleased` during development. Each released section uses this structure:

```markdown
## [1.2.0] - 2026-11-01

### Summary
- One concrete change, no more than 100 characters.
- Another user-visible improvement.

### Details
Additional explanation if needed.
```

The example version/date are illustrative. When intentionally preparing a release, move the accumulated changes under its actual version/date and update `VERSION` to match. `scripts/Write-ReleaseNotes.ps1` requires 1-3 plain-text Summary bullets. The release workflow rejects a missing versioned summary and waits for the shared Windows and Linux checks, including the real Firefox and installer-handoff fixtures. It publishes the tested ZIP and uses its validated notes as the GitHub Release body. The updater extracts the same Summary block as plain text; it never executes release-note HTML or JavaScript. A missing or unsupported summary is displayed as **Release notes unavailable**.

Local package builds can use `Unreleased` as an explicitly labeled preview; tag publication cannot. The initial version containing this feature must be installed manually. No release is published merely by changing the changelog or pushing an ordinary commit.

## Verification status

Automated checks run on hosted runners for branch pushes and release tags: offline rule/metadata/archive regression fixtures including damaged-cache repair, the installer and package suites, a real-Firefox overlay fixture (disposable profile; card presence, identity and hide/re-show, restart reminders, disabled-check settings access and pre-stop helper failure recovery), a real installer-handoff fixture, and a live GitHub Releases metadata check that exercises the shipped update network code against the real API and never downloads or installs a package (a rate-limited shared runner skips that check instead of failing).

These fixtures cannot cover everything: the 125/150/200 % DPI scaling matrix, unmaximized and very small window layouts beyond the fixed runner resolution, keyboard/focus traversal, graphics-driver variation, and the multi-restart reminder cycle on a personal profile remain manual checks. Verify them on your own machine when the update system matters to you; they are not automatically validated and no source review or mockup substitutes for them.
