# Firefox Ghost UI agent instructions

Read this file before changing this project.

## Language and release notes

- All product UI, documentation, comments and release notes must be in English. Do not add translations.
- Keep `CHANGELOG.md` current in the same task as every user-visible behavior change. Add work to `Unreleased` until a release is intentionally prepared. Do not invent completed work or rewrite historical releases.
- Every released version needs a `### Summary` with 1-3 plain-text bullet points, at most 100 characters each. Describe concrete user benefits or fixes, not commit hashes or implementation details. Put further information under `### Details`.
- The update card and GitHub Release must use this same versioned summary. Do not maintain separate handwritten release descriptions. Validate notes with `scripts/Write-ReleaseNotes.ps1` before a release.
- Release preparation moves `Unreleased` to `## [X.Y.Z] - YYYY-MM-DD` and updates `VERSION` together.

## Update contract

- Update UI appears only inside a verified foreground Firefox window. Show the compact button and automatically expand cards only over a positively identified, visible Bonjourr, about:newtab or about:home document. Hide the button on other tabs and when page identification fails; retain Ctrl+Alt+F8 for explicitly opened settings on those tabs. No Windows update notifications, update tray items or separate update taskbar window.
- Bonjourr is optional. Profile registration, update settings, downloads and installation must work without it or its stylesheet. Never infer a profile from a native about page; require explicit selection when multiple profiles are ambiguous.
- Always display the short English summary. If unavailable, say so; never invent a summary.
- Offer `Download`, `Download manually`, `Later`, and `All changes`. Manual download opens the official release page for ZIP installation with `Install.cmd`.
- Keep Check now and Check daily reachable inside Firefox when automatic checks are disabled, even if every profile is up to date.
- Downloading never installs. Only a fresh explicit `Install update` action authorizes the selected version and profile. Never persist consent as a scheduled or startup action.
- After `Later`, remind once per Firefox process session when the user next opens the start page. Downloaded state survives companion/browser restarts; validate the cached package before offering installation.
- Preserve Bonjourr, user settings, profile backups and installer conflict checks. Do not modify/repackage another extension, disable Firefox signing/security checks, force-close Firefox, or use privileged browser injection.
- Network, package extraction and installer launching are security-sensitive. Bound remote input and downloads, validate URLs, versions, package hashes and paths, and preserve failure/rollback evidence. A checksum obtained from GitHub over TLS is not an independent publisher signature.

## Verification

- Installing into a personal Firefox profile requires explicit authorization.
- Tests must use disposable profiles, temporary files and isolated registry keys. Do not inspect web page contents beyond the active document URL required to identify the start page.
