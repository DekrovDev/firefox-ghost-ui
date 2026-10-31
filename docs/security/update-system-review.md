# Update system: source review and release gate

Date: 2026-10-31

Status: scoped release security audit `firefox-ghost-ui-release-1.2.0-run-1` completed at source ref `92c46b3` on the working branch (`codex/update-system-20261031-qa`). No confirmed security vulnerabilities. The single needs-validation record was closed 2026-10-31 as informational / works-as-designed by a bounded owner-run restricted-token probe; `findings.json` carries one `rejected` record. This document records source-level review evidence and runtime validation outcomes; it is not by itself a release approval for publishing.

## Coverage

Reviewed `companion/GhostUpdates.cs`, its `GhostHandles.cs` integration, companion compilation/cache inputs, the package whitelist, release-note generation, changelog, workflows, and the existing installer entry point's profile/rollback handoff.

The scoped release audit (26 ledger units) covered: CI workflow inputs and permissions; the release workflow tag/validate/publish path; the native companion surface (controller/native/dispose/debug and program entry); the full update pipeline (probe origin/walk, consent, LaunchInstall, update network, release rules parsing, store read/verify, extractor, installer run/handoff); installer/profile surfaces (Install.ps1 checkpoint/restore, GhostUI.psm1 install/preferences, Select-Profile); companion lifecycle scripts (Handles.psm1 install/stop/uninstall); build/package surfaces (Build-Handles compiler choices, Build-Release whitelist); and the unshipped Marionette test driver only as a narrow process-lifetime unit. Out of scope, with reasons in the audit ledger: unshipped developer HTTP bridges (`devtools/firefox/*`), browser DOM/auth/cloud/LLM/protocol domains, and the plaintext-recovery/datastore axis.

The audit ran under a source-and-local-only execution boundary: target code was never executed by the audit itself. Hosted-runner test results were used only as separate functional evidence. Methodology, agent chain, ledger and reports live in the audit run directory (see "Release audit" below); the earlier partial state of this document was superseded when the scoped run completed.

## Source-visible controls

- Published, non-draft, non-prerelease metadata; strict numeric versions and an exact expected repository asset URL/name.
- Public HTTPS requests without tokens, disabled automatic redirects, API/download size limits, bounded I/O times, cooldown/backoff and no inbound listener. TLS 1.2 is explicitly enabled in addition to the system-selected protocols for GitHub update calls (`UpdateNetwork` static constructor, `ServicePointManager.SecurityProtocol |= Tls12`); the code does not narrow the system protocol selection or pin TLS 1.2 exclusively.
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
- Enable TLS 1.2 explicitly in addition to the system-selected protocols for GitHub update calls so current .NET defaults on older Framework configs cannot silently fail (`post-review release fix`).

## Verification performed

Source-only Roslyn analysis against installed .NET Framework references (C# 5 syntax) with zero diagnostics; PowerShell parser clean for project scripts and tests; workflow YAML parsed; static package inspection of the allowlist; `git diff --check` clean.

Offline regression fixtures exist for numeric version order, unstable/malformed releases, summary extraction/limits/markup, URL hosts, unsafe ZIP names, persistence, valid extraction and tampered package rejection; they execute on the hosted runners.

### Release audit (release-1.2.0)

- Scoped audit run `firefox-ghost-ui-release-1.2.0-run-1` completed at ref `92c46b3`: 26 coverage units, all covered; round-1 hunters (native/updater/installer/delivery) at `e6d11d8`, round-2 scoped delta hunters covering the delivery/installer delta, a fresh re-anchoring hunter for the reassigned consent unit, an independent candidate verifier for the single needs_validation lead, and the full critic chain (post-wave, final-clean pass that queued one legitimate reassignment, wave-3 post-wave, final-clean closure) — final verdict: no missing units, no reassignments.
- Both audit validators pass for `findings.json` (1 rejected record with the preserved probe rationale) and `coverage-ledger.json` (26 units) using the unchanged exported validation APIs (Windows CLI lacks the no-follow read checks; recorded limitation).
- Three carried prior leads were re-verified at the current ref and closed as resolved in source: `Handles.psm1.registry-metadata-orphan`, `Install.ps1.helper-stage-failure-residue`, `tests/Toolbar.Tests.py:marionette-system-access-residual-listener`.
- Functional blocker evidence: the updater process separation and real installer handoff are exercised by the real-Firefox fixtures on hosted runners, green in authorized CI run `41200499391` on this same branch lineage (`e6d11d8`/`92c46b3`).
- The post-audit TLS enablement, version bump and release notes were reviewed as a strictly additive delta by the final critics: no new boundary unit required.
- The single candidate (`local-ipc-stop-signal-no-peer-auth`) was closed 2026-10-31 by a bounded owner-run probe: a per-run surrogate-named event constructed with the shipped constructor semantics, with a live companion owning the production-named event (never signaled), default DACL read as FullControl Allow for SYSTEM/logon session/user only, and a same-session Basic User restricted-token child opening the event and performing `Set()`, observed by the creating session (`PARENT-OBSERVED: True`). Transcripts live in the audit run directory (`ipc-stop-signal-probe-2026-10-31.txt`, `ipc-stop-signal-probe.ps1.txt`); disposition: rejected / informational works-as-designed (see `NEEDS-VALIDATION.md`).

## Validation status

Original pre-release list with current state:

1. ✔ Installer, companion, package and release-note fixtures executed in the authorized isolated hosted Windows environment (Windows PowerShell 5.1 wrapper plus PowerShell 7 job in `checks.yml`); green in the referenced run on this branch.
2. ✔ Real-Firefox overlay fixture runs on the hosted runner with a disposable profile and the packaged UUID-referenced Bonjourr start page (card presence and origin identity, Download/Download manually controls with the summary, dismissed-session persistence across a companion restart, Ready-to-install promotion and the explicit-confirm step that never persists consent); green in the referenced runs on this branch.
3. ◐ 100/125/150/200 % DPI scaling, small/unmaximized window layouts beyond the fixed runner resolution and keyboard-access traversal remain manual checks on a personal machine.
4. ◐ Negative update cases (invalid hash, unsafe archive paths, duplicates, oversized entries, stale/corrupt state, reparse-point rejection, tampered packages) run as offline fixtures on the hosted runners; live production endpoints are never probed with failure payloads, by design.
5. ◐ Downloaded-state reminder behavior is exercised for the helper/handoff path; the full multi-restart reminder cycle on a personal profile remains an owner check.
6. ✔ Approval expiry/revocation, explicit-confirm-then-close flow, unexpectedly reopened browser, helper failure and installer conflicts are covered by hosted fixtures (C# update self-tests plus the installer/handoff fixture suites).
7. ✔ Independent scoped audit coverage and candidate validation completed (this run); the one candidate was then closed by the bounded owner-run IPC probe (rejected, informational works-as-designed; see `NEEDS-VALIDATION.md`).
8. ✔ Real GitHub Releases metadata integration is exercised by a tests-only fixture (`tests/UpdateMetadataProbe.cs`) on the disposable hosted runner: one metadata GET against the real API through the shipped update code path, checking digest/summary/URL rules, typed network-error handling and the explicit TLS 1.2 enablement; it never downloads or installs a package and self-skips on shared-runner rate limits with the real backoff bookkeeping verified. Green in CI run `41213155666` (commit `e0c036d`) on this branch: the shared runner's IP was not rate limited, so the full real-API success path ran (TLS 1.2 enabled by the shipped static constructor and observed, real latest-release metadata parsed by the shipped rules at the published version 1.1.2, zero-summary legacy body handled per the rules, digest/size accepted, canonical redirect policy, typed `ConnectFailure` handled); artifacts and per-step logs are archived in the audit run directory.

Remaining owner-side validations (non-blocking for publishing):

- DPI 125/150/200 % scaling, small-window and keyboard-access manual sweep (item 3) before or shortly after release.

## Trust limitations

GitHub's digest over HTTPS detects corruption/mismatched assets, not a compromised repository or publisher account. Independent release signing and key rotation are not implemented. The local Windows user and their writable staging directory are one principal; checks are not a boundary against another process with that user's full authority. Source inspection cannot establish live Windows accessibility, event delivery, UI stacking, execution-policy behavior or installer recovery. A brief polling transition remains possible; title/selection event invalidation reduces but does not prove elimination of it. The stop-event DACL fact is closed at runtime: the default DACL admits any same-session same-session process of the same Windows user, including a restricted-token Basic User child, while carrying no identity broader than the user (no Everyone/Users ACE); a Firefox content-process token composition was not exercised and is not asserted.
