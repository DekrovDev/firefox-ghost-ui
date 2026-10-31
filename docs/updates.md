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

## Install a downloaded update

A valid cached package is detected and verified after restarting the companion. The card then says **Ready to install** and offers **Install update** instead of Download. Missing or invalid packages are not offered for installation.

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

The example version/date are illustrative. When intentionally preparing a release, move the accumulated changes under its actual version/date and update `VERSION` to match. `scripts/Write-ReleaseNotes.ps1` requires 1-3 plain-text Summary bullets. The release workflow rejects a missing versioned summary, packages the validated notes, and uses that file as the GitHub Release body. The updater extracts the same Summary block as plain text; it never executes release-note HTML or JavaScript. A missing or unsupported summary is displayed as **Release notes unavailable**.

Local package builds can use `Unreleased` as an explicitly labeled preview; tag publication cannot. The initial version containing this feature must be installed manually. No release is published merely by changing the changelog or pushing an ordinary commit.

## Verification status

The implementation includes offline rule/metadata/archive regression fixtures and existing installer/package checks. A live Windows/Firefox test of accessibility identification, stacking, mixed DPI, restart reminders, download cancellation and the full installation handoff is required before release. Do not interpret the visual mockup or a source review as proof those checks passed.
