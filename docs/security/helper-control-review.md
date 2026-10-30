# Firefox helper control review

Date: 2026-10-30

Status: focused source review completed, followed by Windows CI regression validation. No complete product security assurance is implied.

## Scope

Reviewed the two legacy HTTP-to-Marionette bridges, their shared Python client and launcher, and the temporary-profile-settings lifecycle in the two legacy profile helpers. The nine source/test files are preserved under `devtools/firefox/` to make the fixes reviewable and versioned. Reviewed the release ZIP allowlist to confirm these developer tools and their generated state are excluded.

This review does not establish browser-origin exploitability or a cross-user availability attack. The original two security hypotheses remain unvalidated, without assigned severity. The profile restoration defects are source-visible reliability problems; they are not additional confirmed vulnerabilities.

The current product installer, Windows companion, third-party extensions and browser UI security behavior were not comprehensively audited. Separate historical installer findings and their remediation plan are not closed by this review.

## Controls reviewed

- New unpredictable credential per launcher run, passed only through the child environment; no unauthenticated fallback.
- Exact loopback Host and no Origin, checked before reading bodies or dispatching commands; proxy use and redirects disabled in the client.
- Single valid Content-Length, JSON object payload, 2 MiB limit, absolute five-second header/body deadline, bounded connection queue and sequential dispatch.
- Fresh disposable bridge profiles, no copied personal settings, ephemeral Marionette ports, and loopback bind before Firefox startup.
- Private launcher/server stdin lifetime signaling, including startup failure; no shutdown request to an unrelated listener.
- Journal-before-mutation, full backup/ownership validation, explicit conflict handling, idempotent recovery and cleanup before report output.
- Existing, edited and replaced user.js preservation; targeted preference restoration while retaining other current preferences; redirected profile paths and multiply linked files rejected.

## Additional defects corrected during control review

1. Preference cleanup used substring replacement. A matching example inside a comment could be removed instead of the actual preference line. It now removes only complete parsed lines, and ambiguous syntax fails closed.
2. The default HTTP client could carry an Authorization header through a redirect. Redirects are now rejected before another request is made. This is a source-supported credential forwarding path, not evidence that a live token was exposed.
3. Rejected preflight could overwrite an earlier transaction's report. No report is written when a new transaction never began.
4. Success output preceded cleanup. PASS is emitted only after cleanup returns successfully.
5. Replacing an old Bonjourr stylesheet discarded all following text. Only one exact known stylesheet can now be replaced; following user CSS is retained, and modified managed CSS requires manual reconciliation.
6. Force-stopping a launcher during startup could bypass orderly server cleanup. The private pipe now requests shutdown even before READY; server code interrupts pending Marionette work and cleans up its own process.

## Evidence and limits

Source checks include Python AST parsing and compilation without execution, preservation-guard inspection, import/startup/cleanup control-flow review, shared-guard/client tracing, private-data boundary checks and release-manifest inspection. The regression suite includes both real profile-driver entry points with mocked Firefox.

The initial review was source-only. Subsequent release validation on 2026-10-30 passed all 24 then-existing helper fixtures, both installer PowerShell variants, companion checks, package installation and the dummy process-tree lifetime check: [Windows CI](https://github.com/DekrovDev/firefox-ghost-ui/actions/runs/41092033087). The suite now also includes an artificial Windows profile-lock fixture, bringing its total to 25. Checks run on disposable GitHub-hosted Windows VMs under the project owner's explicit authorization; the stricter initial audit sandbox requirements were not silently treated as satisfied by a standard VM. Real Firefox UI and browser-origin exploitability remain separate validation questions.

Content and file-identity comparisons are not atomic filesystem transactions against concurrent same-user edits. Abruptly killing the bridge server itself is not covered by the parent-pipe cleanup mechanism. Port reservation is released before Firefox binds; the familiar ephemeral-port race is not eliminated. Recovery can require manual action if Firefox remains running, bootstrap content was edited, preferences conflict or writes fail. CSS/storage/tab rollback is not automatic. No claim of an error-free build or an exhaustive audit is made.
