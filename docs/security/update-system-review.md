# Update system: source review and release gate

Date: 2026-10-31

Status: scoped release security audit `firefox-ghost-ui-release-1.2.0-run-1` completed at source ref `92c46b3` on the working branch (`codex/update-system-20261031-qa`). No confirmed security vulnerabilities; one non-blocking needs-validation record remains open (`local-ipc-stop-signal-no-peer-auth`, informational if confirmed). This document records source-level review evidence and pending runtime validations; it is not by itself a release approval for publishing.

## Coverage

Reviewed `companion/GhostUpdates.cs`, its `GhostHandles.cs` integration, companion compilation/cache inputs, the package whitelist, release-note generation, changelog, workflows, and the existing installer entry point's profile/rollback handoff.

The scoped release audit (26 ledger units) covered: CI workflow inputs and permissions; the release workflow tag/validate/publish path; the native companion surface (controller/native/dispose/debug and program entry); the full update pipeline (probe origin/walk, consent, LaunchInstall, update network, release rules parsing, store read/verify, extractor, installer run/handoff); installer/profile surfaces (Install.ps1 checkpoint/restore, GhostUI.psm1 install/preferences, Select-Profile); companion lifecycle scripts (Handles.psm1 install/stop/uninstall); build/package surfaces (Build-Handles compiler choices, Build-Release whitelist); and the unshipped Marionette test driver only as a narrow process-lifetime unit. Out of scope, with reasons in the audit ledger: unshipped developer HTTP bridges (`devtools/firefox/*`), browser DOM/auth/cloud/LLM/protocol domains, and the plaintext-recovery/datastore axis.

The audit ran under a source-and-local-only execution boundary: target code was never executed by the audit itself. Hosted-runner test results were used only as separate functional evidence. Methodology, agent chain, ledger and reports live in the audit run directory (see "Release audit" below); the earlier partial state of this document was superseded when the scoped run completed.

## Source-visible controls

- Published, non-draft, non-prerelease metadata; strict numeric versions and an exact expected repository asset URL/name.
- Public HTTPS requests without tokens, disabled automatic redirects, API/download size limits, bounded I/O times, cooldown/backoff and no inbound listener. Explicit TLS 1.2 is pinned for GitHub update calls (`UpdateNetwork` static constructor, `ServicePointManager.SecurityProtocol |= Tls12`).
- Only expected GitHub asset hosts are accepted after a redirect. Built-in download requires the API SHA-256 digest; missing digest leaves the manual path available.
- Cached packages are rehashed after restart and before extraction. Extraction uses the same open, verified stream, not a second open of the archive by path.
- Archive root/path/filename/type checks, case-insensitive duplicate detection, count/size limits, exclusive file creation, a fresh staging directory, required installer files and packaged-version verification.
- A fresh explicit installation confirmation binds the shown release and selected registered profile. Approval exists only in memory, expires after two minutes and is revoked by Cancel, Later, closing the card, or choosing the manual path. An ordinary browser/Windows/companion restart cannot restore it. Installer-side handoff revalidates release, profile registration, helper identity, browser absence, consenting-parent identity and package digest before extraction.
- A failed-handoff watchdog recovers the card only: it clears the single-flight latch, re-reads store state and refreshes the card; it never re-arms consent or reaches an install route without fresh consent.
- The helper rechecks profile registration, version, package integrity, the consenting parent executable and absence of Firefox before calling the installer. It does not force-close the browser or save retry jobs. The installer retains its profile safety and rollback checks.
- The overlay uses an accessibility document URL matched to a registered profile's Bonjourr origin, never the page title as proof. It does not descend into document content. It hides on stale/failed probes, foreground/fullscreen changes and relevant accessibility events. Test/demo controller modes do not initialize updates.
- Release summaries are limited plain text. Notes are not rendered as HTML or passed into shell commands. Both the release body and the card's Summary originate from the changelog. Publication requires a versioned summary; local Unreleased previews are explicitly marked.

## Defects corrected during implementation

These are source-level correctness improvements, not independently confirmed security vulnerabilities:

- Revoke active installation approval when the card is closed or the manual path is selected.
- Avoid switching the approved release through an automatic check while confirmation/approval is active.
- Create the form handle before asynchronous UI callbacks and accept both serializer array representations.
- Retain the manual path if GitHub does not supply a digest.
- Invalidate stale start-page probes after window/tab changes; do not scan accessibility when no newer release exists or the card was dismissed.
- Restart the previous companion after an unsuccessful installer handoff where possible; do not report success without matching installed profile metadata.
- Keep operating-system exception text out of English UI error messages.
- Stop making the update helper a plain GhostHandles.exe identity; use a dedicated GhostUpdater.exe whose process identity cannot collide with the installer's stop flow (`e6d11d8`).
- Recover a failed update handoff instead of wedging `launching` forever: clear the latch and re-post the card after the helper exits (`92c46b3`).
- Pin explicit TLS 1.2 for GitHub update calls so current .NET defaults on older Framework configs cannot silently fail (`post-review release fix`).

## Verification performed

Source-only Roslyn analysis against installed .NET Framework references (C# 5 syntax) with zero diagnostics; PowerShell parser clean for project scripts and tests; workflow YAML parsed; static package inspection of the allowlist; `git diff --check` clean.

Offline regression fixtures exist for numeric version order, unstable/malformed releases, summary extraction/limits/markup, URL hosts, unsafe ZIP names, persistence, valid extraction and tampered package rejection; they execute on the hosted runners.

### Release audit (release-1.2.0)

- Scoped audit run `firefox-ghost-ui-release-1.2.0-run-1` completed at ref `92c46b3`: 26 coverage units, all covered; round-1 hunters (native/updater/installer/delivery) at `e6d11d8`, round-2 scoped delta hunters covering the delivery/installer delta, a fresh re-anchoring hunter for the reassigned consent unit, an independent candidate verifier for the single needs_validation lead, and the full critic chain (post-wave, final-clean pass that queued one legitimate reassignment, wave-3 post-wave, final-clean closure) — final verdict: no missing units, no reassignments.
- Both audit validators pass for `findings.json` (1 needs-validation record) and `coverage-ledger.json` (26 units) using the unchanged exported validation APIs (Windows CLI lacks the no-follow read checks; recorded limitation).
- Three carried prior leads were re-verified at the current ref and closed as resolved in source: `Handles.psm1.registry-metadata-orphan`, `Install.ps1.helper-stage-failure-residue`, `tests/Toolbar.Tests.py:marionette-system-access-residual-listener`.
- Functional blocker evidence: the updater process separation and real installer handoff are exercised by the real-Firefox fixtures on hosted runners, green in authorized CI run `41200499391` on this same branch lineage (`e6d11d8`/`92c46b3`).
- The post-audit TLS pin, version bump and release notes were reviewed as a strictly additive delta by the final critics: no new boundary unit required.
- The single retained candidate (`local-ipc-stop-signal-no-peer-auth`) is informational, not a release blocker; its runtime DACL fact cannot be settled from source and its resolution contract is recorded below.

## Validation status

Original pre-release list with current state:

1. ✔ Installer, companion, package and release-note fixtures executed in the authorized isolated hosted Windows environment (Windows PowerShell 5.1 wrapper plus PowerShell 7 job in `checks.yml`); green in the referenced run on this branch.
2. ◐ Real-Firefox overlay fixture runs on the hosted runner with a disposable profile and the packaged UUID-referenced Bonjourr start page (card presence/identity, handoff restart); the wider DPI/monitor matrix and keyboard-access sweep beyond that fixture remain manual.
3. ✖ 100/125/150/200 % DPI, small windows and keyboard access remain unverified beyond the single hosted fixture.
4. ◐ Negative update cases (invalid hash, unsafe archive paths, duplicates, oversized entries, stale/corrupt state, reparse-point rejection, tampered packages) run as offline fixtures on the hosted runners; live production endpoints are never probed, by design.
5. ◐ Downloaded-state reminder behavior is exercised for the helper/handoff path; the full multi-restart reminder cycle on a personal profile remains an owner check.
6. ✔ Approval expiry/revocation, explicit-confirm-then-close flow, unexpectedly reopened browser, helper failure and installer conflicts are covered by hosted fixtures (C# update self-tests plus the installer/handoff fixture suites).
7. ✔ Independent scoped audit coverage and candidate validation completed (this run); one needs-validation record with an exact runtime blocker and a bounded local probe remains open by design.

Remaining owner-side validations (non-blocking for publishing):

- One bounded local probe for the needs-validation record's DACL fact (see audit `NEEDS-VALIDATION.md`): restricted-token `OpenExisting`/`Set()` attempt against `Local\FirefoxGhostUIHandlesStop` in a disposable isolated session; outcome closes the record as rejected or as informational works-as-designed.
- One owner-observed live GitHub update-call response check to confirm the explicit TLS 1.2 pin works end-to-end for the actual first decorated request after release (safe content check only).
- DPI/keyboard manual sweep (item 3) before or shortly after release; it does not block publishing a security pass.

## Trust limitations

GitHub's digest over HTTPS detects corruption/mismatched assets, not a compromised repository or publisher account. Independent release signing and key rotation are not implemented. The local Windows user and their writable staging directory are one principal; checks are not a boundary against another process with that user's full authority. Source inspection cannot establish live Windows accessibility, event delivery, UI stacking, execution-policy behavior or installer recovery. A brief polling transition remains possible; title/selection event invalidation reduces but does not prove elimination of it. The stop-event DACL/UIPI behavior of a restricted-token in-session process is explicitly unresolved runtime fact until the recorded probe runs.
