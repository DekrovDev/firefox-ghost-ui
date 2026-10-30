# Firefox development helpers

These are developer tools recovered from older Ghost UI checks. They are not part of normal installation, are excluded from the Windows release ZIP, and do not replace `Install.cmd` or `Uninstall.cmd`.

Status: source reviewed, runtime validation pending. Python 3.10+ is required. Firefox startup helpers currently target desktop Firefox on Windows. The stdlib-only regression fixtures do not require Firefox.

## Authenticated probe

From this directory, run a trusted Python client through the launcher:

```text
python run-ghost-probe.py screenshot -c "from ghost_probe import cmd; print(cmd('WebDriver:GetWindowHandles'))"
```

`probe` uses a baseline stylesheet-disabled profile; `screenshot` enables the theme. Both create fresh temporary Firefox profiles, use an ephemeral Marionette port, and expose the HTTP bridge only on `127.0.0.1:2832`. They never copy personal preferences. A new 256-bit credential is inherited by the server and client for each run; it is not printed or written to disk. The client rejects redirects and does not use HTTP proxies. Missing credentials fail closed.

The launcher passes a private stdin pipe to the server. Closing it, including when the launcher exits, requests shutdown independently of HTTP readiness. The server stops its own disposable Firefox and removes its temporary profile after process exit. Abrupt termination of the server itself is not an OS-level guarantee of descendant cleanup.

The bridge accepts JSON POST requests with a matching Host and Bearer token and no Origin. Requests have a five-second absolute read deadline and a 2 MiB body limit. Transfer-Encoding, Expect and ambiguous Content-Length are rejected. This is privileged automation for a trusted local operator; the credential does not isolate the tool from processes with the same OS rights as the operator.

## Legacy profile operations

`install-ghost.py` and `restore-bonjourr-search.py` retain specialized old UI/Bonjourr checks. Use only prepared disposable profiles: they change CSS and Bonjourr storage and have assumptions about installed extensions and UI geometry. They are not a supported replacement for the product installer.

Both require explicit `--profile`; `--firefox` can override the executable. Use a new `--state` directory for each operation. By default their journals, reports and screenshots go under ignored `.local/backup/`. Existing `user.js`, redirected paths, ambiguous preferences and old incomplete backups block unsafe operations. No automatic operation resume is supported.

After closing Firefox normally, recover a pending transaction with the same profile and state path:

```text
python install-ghost.py --profile <disposable-profile> --state <original-state-directory> --recover
```

The search helper supports the same recovery options. Recovery restores temporary preferences before report writing, checks original snapshots and bootstrap ownership, and preserves unrelated current preferences. Edited or replaced files require manual review; the tool does not silently discard them. Completed journals are retained and are not reused for a new operation. The browser is left closed. Recovery is not a full rollback of CSS, extension storage or tabs; their saved snapshots may require manual restoration after an interrupted operation. Do not edit the profile concurrently.

## Regression fixtures

`test_ghost_safety.py` currently contains 24 test methods for authentication, redirects, framing, body limits, absolute deadlines, parent/child shutdown, recovery, CSS preservation and driver fault paths. They use only synthetic profiles, mock commands/processes and isolated ephemeral loopback ports. They never start Firefox or connect to the real bridge port.

The tests have **not been executed** in this change. The security review's execution rules require all of:

- OS-enforced denial of external networking, with a private loopback namespace;
- an empty environment populated from an explicit safe allowlist, with HOME/TEMP/cache in scratch;
- read-only source and toolchain, with writes restricted to assigned scratch;
- explicit low CPU, memory, process, file-size, disk and wall-clock limits.

Once those controls are available, run from this directory:

```text
python -m unittest -v test_ghost_safety
```

Validate the Windows profile-lock path separately in equivalent Windows isolation with artificial locks. Merely opening a fresh profile or mocking browser commands does not provide the required OS isolation.

See the [source review](../../docs/security/helper-control-review.md) for scope and limitations. This source snapshot is not a claim that runtime tests passed or that the entire product is free of vulnerabilities.
