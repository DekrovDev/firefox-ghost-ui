# Remote regression testing

Use the existing `checks.yml` workflow for manual regression checks. It also keeps
the existing push and pull-request checks; do not dispatch a duplicate if CI is
already testing the same commit and scope.

Windows runs the real installer on Windows PowerShell 5.1 and PowerShell 7,
companion installation/geometry, package build/install/removal, process-lifetime
checks, and development-helper fixtures. Manual runs additionally test the
portable helper fixtures on Linux; Windows-specific cases are skipped there.
This does not run the full Firefox toolbar/browser integration suite.

## Run and retrieve evidence

First inspect authentication, repository visibility, workflow content, and the
remote branch SHA. Standard hosted runners in a public repository are free;
private repositories, larger runners, and additional services need a separate
cost check and permission before any potentially billable operation.

```powershell
gh auth status
gh repo view OWNER/REPO --json visibility,viewerPermission,defaultBranchRef
gh workflow list -R OWNER/REPO
gh workflow view checks.yml -R OWNER/REPO --ref BRANCH --yaml
gh api repos/OWNER/REPO/commits/BRANCH --jq .sha
gh workflow run checks.yml -R OWNER/REPO --ref BRANCH
gh run list -R OWNER/REPO --workflow checks.yml --branch BRANCH --event workflow_dispatch --limit 10 --json databaseId,headSha,createdAt,status,url
gh run watch RUN_ID -R OWNER/REPO --exit-status --interval 15
gh run view RUN_ID -R OWNER/REPO --json status,conclusion,headSha,event,jobs,url
gh run view RUN_ID -R OWNER/REPO --log-failed
gh run download RUN_ID -R OWNER/REPO --dir PATH_TO_FRESH_EVIDENCE_DIRECTORY
```

Identify the dispatched run by branch, exact SHA, event, and dispatch time;
never assume the newest run belongs to this session. Only a completed run with
successful required jobs verifies the change. Uncommitted/unpushed changes are
not tested. Run IDs and source SHA are also included in the artifacts.

The workflow filename already exists on the default branch. Dispatching the
enhanced version with `--ref` can be used before merging; a completely new
workflow filename needs registration on the default branch first. Do not merge
or modify that branch without authorization.

## Boundaries and resource use

- Only GitHub-hosted `windows-latest` and `ubuntu-latest`; no self-hosted runner.
- `contents: read`, checkout credentials are not persisted, no user secrets,
  environment approvals, external API credentials, release or deployment steps.
- One active check workflow for this repository, no automatic cancellation of
  another agent's run. GitHub keeps at most one pending run in this concurrency
  group; a newer pending run may replace an older pending run.
- Windows has a 20-minute timeout; Linux has five minutes and follows Windows.
- Only named text evidence is uploaded, including on test failure; retention is
  one day. No packages, profiles, caches or workspace dumps are uploaded.
  Cancellation or runner failure can prevent artifact upload; use GitHub logs.
- These are disposable VM functional regression checks for reviewed project
  code. The test process has VM user rights and networking. This is not a
  network-denied, read-only, resource-limited sandbox for hostile code, and is
  not evidence that a security audit's stronger sandbox requirements are met.
- Never copy local credentials or private data into this public repository.
  Explicit permission is required for new uploads of unpublished code/data,
  releases, default-branch changes, production security changes or paid usage.

If no matching CI is running, an agent may dispatch a reviewed, appropriately
configured test-only workflow when remote OS or environment coverage materially
improves verification. Inspect failures before retrying; avoid identical reruns
without a new diagnostic reason.
