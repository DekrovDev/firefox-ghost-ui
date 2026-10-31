# Update system: source review and release gate

Date: 2026-10-31

Status: partial source review. Full audit and execution validation are incomplete. This is not a security pass or release approval.

## Coverage

Reviewed `companion/GhostUpdates.cs`, its `GhostHandles.cs` integration, companion compilation/cache inputs, the package whitelist, release-note generation, changelog, workflows, and the existing installer entry point's profile/rollback handoff.

The update architecture adds network input, downloaded executable source, local staging, native accessibility observation and a consent-sensitive installer handoff. These changes require a full audit before release. The local environment has no configured execution sandbox satisfying all required no-network, empty-environment, read-only-source/toolchain, scratch-only-write and resource-limit controls. Docker was not available; WSL reported that its Windows component is not enabled. Target builds, tests, Firefox and installer fixtures were therefore not executed. Independent full-audit reconnaissance/hunting/verification phases were not completed. Existing historical installer findings are not closed by this review.

## Source-visible controls

- Published, non-draft, non-prerelease metadata; strict numeric versions and an exact expected repository asset URL/name.
- Public HTTPS requests without tokens, disabled automatic redirects, API/download size limits, bounded I/O times, cooldown/backoff and no inbound listener.
- Only expected GitHub asset hosts are accepted after a redirect. Built-in download requires the API SHA-256 digest; missing digest leaves the manual path available.
- Cached packages are rehashed after restart and before extraction. Extraction uses the same open, verified stream, not a second open of the archive by path.
- Archive root/path/filename/type checks, case-insensitive duplicate detection, count/size limits, exclusive file creation, a fresh staging directory, required installer files and packaged-version verification.
- A fresh explicit installation confirmation binds the shown release and selected registered profile. Approval exists only in memory, expires after two minutes and is revoked by Cancel, Later, closing the card, or choosing the manual path. An ordinary browser/Windows/companion restart cannot restore it.
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

## Verification performed

Trusted, source-only Roslyn analysis against installed .NET Framework references, with C# 5 syntax, returned zero diagnostics without emitting or executing an assembly. PowerShell's parser returned zero errors for the project script/test files. Workflow YAML parsed successfully. Static package-source inspection found all 28 allowlisted files, no Cyrillic in packaged text, and no broken packaged Markdown links. `git diff --check` reported no whitespace errors.

Offline regression fixtures were added for numeric version order, unstable/malformed releases, summary extraction/limits/markup, URL hosts, unsafe ZIP names, persistence, valid extraction and tampered package rejection. Their execution is pending. They are wired into the existing companion self-test and Windows workflow; wiring is not evidence of a successful run.

## Required validation before release

1. Run the existing installer, companion, package and release-note fixtures in an approved isolated Windows environment. Verify both Windows PowerShell 5.1 and PowerShell 7 behavior where applicable.
2. Use a disposable Firefox profile with separately installed official Bonjourr. Verify that its actual accessibility document URL is recognized and that the overlay appears, is clickable, remains inside Firefox, and hides on tab switches, other apps, minimized/fullscreen windows and unsupported pages.
3. Verify 100/125/150/200 percent and mixed-monitor DPI, small windows, keyboard access and the longest allowed three-bullet summary. Native overlay rendering has not been visually verified.
4. Use mocked responses and inert locally controlled packages for interrupted/cancelled downloads, API rate limits, missing digest, invalid hash, malicious archive paths, duplicates, oversized entries, stale/corrupt state and reparse-point rejection. Do not probe production.
5. Verify downloaded-state reminders across full Firefox and companion restarts, dismissal within one process session, manual-path cancellation, profile selection and concurrent metadata changes.
6. Verify approval expiry/revocation, the explicit-confirm-then-close flow, an unexpectedly reopened browser, helper failure, policy restrictions, installer conflicts and rollback. No negative case may install, force-close Firefox or create an install-on-startup job.
7. Complete independent full-audit coverage and candidate validation before publishing. No new security vulnerability is claimed confirmed by this partial review.

## Trust limitations

GitHub's digest over HTTPS detects corruption/mismatched assets, not a compromised repository or publisher account. Independent release signing and key rotation are not implemented. The local Windows user and their writable staging directory are one principal; checks are not a boundary against another process with that user's full authority. Source inspection cannot establish live Windows accessibility, event delivery, UI stacking, execution-policy behavior or installer recovery. A brief polling transition remains possible; title/selection event invalidation reduces but does not prove elimination of it.
