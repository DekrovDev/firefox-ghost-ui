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
            // Exercise the release metadata path only through the shipped code:
            // run the shipped UpdateNetwork static constructor exactly as the
            // companion would at first use, then observe the result.
            try {
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(UpdateNetwork).TypeHandle);
                check((ServicePointManager.SecurityProtocol & SecurityProtocolType.Tls12) == SecurityProtocolType.Tls12,
                    "The shipped UpdateNetwork static constructor enabled TLS 1.2 in addition to the system-selected protocols");
            } catch (Exception e) {
                check(false, "TLS protocol state readable after the shipped enablement, got " + e.GetType().Name);
            }

            UpdateState state = new UpdateState();
            try {
                UpdateRelease release = UpdateNetwork.Latest(state);
                check(UpdateRules.Valid(release), "Real GitHub Releases metadata parses and passes the shipped update rules");
                check(UpdateRules.VersionValid(release.Version), "Latest published version is stable numeric: " + release.Version);
                int bullets = release.Summary == null ? 0 : release.Summary.Length;
                check(release.Summary != null && bullets <= 3,
                    "Shipped summary rule yields zero to three plain-text bullets: " + bullets +
                    (bullets == 0 ? " (legacy release body; the card shows Release notes unavailable)" : ""));
                check(release.Digest != null && release.Digest.Length > 0 && release.Size > 0,
                    "Asset digest and byte size available in metadata (no package downloaded)");
                Uri asset = new Uri(UpdateRules.AssetUrl(release.Version));
                check(UpdateRules.DownloadLocation(asset, release.Version, false),
                    "Canonical GitHub download URL accepted by the shipped redirect policy");
                Console.WriteLine("INFO: latest published release " + release.Version);
            } catch (WebException e) {
                // The shipped Latest() disposes the rate-limit response itself and
                // records state.RetryUtc exactly for the 403/429 case; read only
                // that bookkeeping, never the disposed response.
                if (!string.IsNullOrEmpty(state.RetryUtc)) {
                    DateTime retry;
                    bool parsed = DateTime.TryParse(state.RetryUtc, out retry);
                    double seconds = parsed ? (retry - DateTime.UtcNow).TotalSeconds : -1;
                    check(parsed && seconds > 45 && seconds <= 86500,
                        "Rate limited by the real API; the shipped backoff window bookkeeping held");
                    Console.WriteLine("SKIP: GitHub rate limited this shared runner IP; metadata success path not exercised this run.");
                    return 0;
                }
                check(false, "Unexpected metadata response; typed WebException status " + e.Status);
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
