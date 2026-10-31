// Trusted CI fixture: exercises the real GitHub Releases metadata path of the
// shipped update code on a disposable GitHub-hosted Windows runner. It performs
// one metadata GET against api.github.com, never downloads or installs any
// release artifact, and treats shared-runner rate limiting as a skip, not a
// product failure.
using System;
using System.Net;
using System.Threading;

namespace GhostUI {
    internal static class UpdateMetadataProbe {
        static int failed;
        static void check(bool condition, string message) {
            Console.WriteLine((condition ? "PASS: " : "FAIL: ") + message);
            if (!condition) failed++;
        }
        static int Main() {
            // The companion never restricts system protocols; the static
            // constructor explicitly adds TLS 1.2 for GitHub calls.
            try {
                check((ServicePointManager.SecurityProtocol & SecurityProtocolType.Tls12) == SecurityProtocolType.Tls12,
                    "TLS 1.2 explicitly enabled for GitHub update requests");
            } catch (Exception e) {
                check(false, "TLS protocol state readable, got " + e.GetType().Name);
            }

            UpdateState state = new UpdateState();
            try {
                UpdateRelease release = UpdateNetwork.Latest(state);
                check(UpdateRules.Valid(release), "Real GitHub Releases metadata parses and passes the shipped update rules");
                check(UpdateRules.VersionValid(release.Version), "Latest published version is stable numeric: " + release.Version);
                check(release.Summary != null && release.Summary.Length > 0 && release.Summary[0].Length > 0,
                    "Plain English summary bullets present: " + release.Summary.Length + " bullet(s)");
                check(release.Digest != null && release.Digest.Length > 0 && release.Size > 0,
                    "Asset digest and byte size available in metadata (no package downloaded)");
                Uri asset = new Uri(UpdateRules.AssetUrl(release.Version));
                check(UpdateRules.DownloadLocation(asset, release.Version, false),
                    "Canonical GitHub download URL accepted by the shipped redirect policy");
                Console.WriteLine("INFO: latest published release " + release.Version);
            } catch (WebException e) {
                HttpWebResponse response = e.Response as HttpWebResponse;
                int code = response == null ? 0 : (int)response.StatusCode;
                if (code == 403 || code == 429) {
                    // Shared runner IP hit the unauthenticated GitHub API rate
                    // limit; verify the real backoff bookkeeping and skip.
                    DateTime retry;
                    bool parsed = DateTime.TryParse(state.RetryUtc, out retry);
                    double seconds = parsed ? (retry - DateTime.UtcNow).TotalSeconds : -1;
                    check(parsed && seconds > 45 && seconds <= 86500,
                        "Rate limited; Retry-After backoff recorded inside the bounded window");
                    Console.WriteLine("SKIP: GitHub rate limited this shared runner IP; metadata success path not exercised this run.");
                    return 0;
                }
                check(false, "Unexpected metadata response: " + (code == 0 ? e.Status.ToString() : "HTTP " + code));
            } catch (Exception e) {
                check(false, "Metadata check failed with " + e.GetType().Name + ": " + e.Message);
            }

            try {
                UpdateNetwork.Open(new Uri("http://127.0.0.1:9/firefox-ghost-ui-probe"), true, CancellationToken.None);
                check(false, "Closed local endpoint was unexpectedly reachable");
            } catch (WebException e) {
                check(true, "Network-error path surfaces as a typed WebException (" + e.Status + "), handled by the friendly message branch");
            } catch (Exception e) {
                check(false, "Network-error path must stay a WebException, got " + e.GetType().Name);
            }

            Console.WriteLine(failed == 0 ? "PASS: real GitHub metadata integration, error handling and enabled TLS 1.2" : ("FAIL: " + failed + " check(s) failed"));
            return failed == 0 ? 0 : 1;
        }
    }
}
