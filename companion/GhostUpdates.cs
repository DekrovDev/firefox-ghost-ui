// English-only, consent-driven updates. No HTTP listener or browser injection.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;
using Accessibility;
using System.Windows.Forms;

namespace GhostUI {
    public sealed class UpdateRelease {
        public string Version;
        public string Digest;
        public long Size;
        public string[] Summary;
    }
    public sealed class UpdateState {
        public UpdateRelease Release;
        public string CheckedUtc;
        public string RetryUtc;
        public string DismissedSession;
        public string DismissedVersion;
        public Dictionary<string, string> DismissedSessions;
        public string Result;
        public bool Disabled;
    }
    internal sealed class UpdateProfile {
        internal string Path, Version, Origin;
        public override string ToString() { return System.IO.Path.GetFileName(Path) + " (" + Version + ")"; }
    }
    public static class UpdateRules {
        public const string Repository = "https://github.com/DekrovDev/firefox-ghost-ui";
        internal const string BonjourrId = "{4f391a9e-8717-4ba6-a5b1-488a34931fcb}";
        internal static string ValidOrigin(string value) {
            return value != null && Regex.IsMatch(value, @"\Amoz-extension://[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}/\z") ? value : null;
        }
        internal static string StartPage(string url, string[] origins) {
            if (url == "about:newtab" || url == "about:home") return url;
            return origins.FirstOrDefault(o => ValidOrigin(o) != null && (url == o || url == o + "index.html" || url == o + "index.html#"));
        }
        internal static Rectangle VisibleBounds(Rectangle window, Rectangle[] workingAreas) {
            return workingAreas.Select(area => Rectangle.Intersect(window, area))
                .OrderByDescending(area => (long)area.Width * area.Height).FirstOrDefault();
        }
        internal static bool IsDismissed(UpdateState state, string session, string version) {
            string dismissed;
            return state.DismissedSession == session && state.DismissedVersion == version ||
                state.DismissedSessions != null && session != null && state.DismissedSessions.TryGetValue(session, out dismissed) && dismissed == version;
        }
        internal static void RememberDismissal(UpdateState state, string session, string version, Func<string, bool> alive = null) {
            if (String.IsNullOrEmpty(session)) return;
            var sessions = new Dictionary<string, string>();
            if (state.DismissedSessions != null)
                foreach (var pair in state.DismissedSessions.Take(64))
                    if (Regex.IsMatch(pair.Key, @"\A[0-9]{1,10}:[0-9]{1,19}\z") && (pair.Value == "none" || VersionValid(pair.Value))) sessions[pair.Key] = pair.Value;
            // Import the legacy record before replacing it, preserving an older
            // companion's most recent dismissal during additive-state migration.
            if (state.DismissedSession != null && Regex.IsMatch(state.DismissedSession, @"\A[0-9]{1,10}:[0-9]{1,19}\z") &&
                (state.DismissedVersion == "none" || VersionValid(state.DismissedVersion)) && !sessions.ContainsKey(state.DismissedSession))
                sessions[state.DismissedSession] = state.DismissedVersion;
            sessions.Remove(session);
            if (alive != null)
                foreach (string ended in sessions.Keys.Where(key => !alive(key)).ToArray()) sessions.Remove(ended);
            while (sessions.Count >= 64) sessions.Remove(sessions.Keys.First());
            sessions[session] = version; state.DismissedSessions = sessions;
            state.DismissedSession = session; state.DismissedVersion = version;
        }
        public const long MaxPackage = 64 * 1024 * 1024;
        internal static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 24 }; }
        internal static object[] ArrayValue(object value) {
            var sequence = value as IEnumerable;
            return sequence == null || value is string || value is IDictionary ? new object[0] : sequence.Cast<object>().ToArray();
        }
        public static bool VersionValid(string value) {
            return value != null && value.Length <= 29 && Regex.IsMatch(value, @"\A(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\z");
        }
        public static int Compare(string a, string b) {
            if (!VersionValid(a) || !VersionValid(b)) throw new InvalidDataException("Invalid version.");
            int[] left = a.Split('.').Select(Int32.Parse).ToArray(), right = b.Split('.').Select(Int32.Parse).ToArray();
            for (int i = 0; i < 3; i++) { int result = left[i].CompareTo(right[i]); if (result != 0) return result; }
            return 0;
        }
        public static string AssetName(string version) {
            if (!VersionValid(version)) throw new InvalidDataException("Invalid version.");
            return "firefox-ghost-ui-" + version + "-windows.zip";
        }
        public static string ReleaseUrl(string version) {
            AssetName(version); return Repository + "/releases/tag/v" + version;
        }
        internal static string AssetUrl(string version) { return Repository + "/releases/download/v" + version + "/" + AssetName(version); }
        public static bool MetadataValid(UpdateRelease release) {
            return release != null && VersionValid(release.Version) && release.Size > 0 && release.Size <= MaxPackage;
        }
        public static bool Valid(UpdateRelease release) {
            return MetadataValid(release) &&
                release.Digest != null && Regex.IsMatch(release.Digest, @"\A[0-9a-f]{64}\z");
        }
        public static string[] ReadSummary(string body) {
            if (body == null || body.Length > 65536) return new string[0];
            MatchCollection sections = Regex.Matches(body.Replace("\r\n", "\n"), @"(?ms)^### Summary\n(.*?)(?=^#{1,3} |\z)");
            if (sections.Count != 1) return new string[0];
            string[] lines = sections[0].Groups[1].Value.Trim().Split('\n').Where(s => s.Trim().Length > 0).ToArray();
            if (lines.Length < 1 || lines.Length > 3 || lines.Any(s => !Regex.IsMatch(s, @"\A- [^\p{Cc}\p{Cf}\p{Zl}\p{Zp}<>\[\]`*\u0400-\u052f]{1,100}\z") || s.Substring(2).Trim().Length == 0)) return new string[0];
            return lines.Select(s => s.Substring(2)).ToArray();
        }
        public static UpdateRelease ParseRelease(string json) {
            var item = Json().Deserialize<Dictionary<string, object>>(json);
            if (item == null || !item.ContainsKey("draft") || !item.ContainsKey("prerelease") ||
                !(item["draft"] is bool) || !(item["prerelease"] is bool) || (bool)item["draft"] || (bool)item["prerelease"])
                throw new InvalidDataException("No stable release.");
            string tag = item.ContainsKey("tag_name") ? item["tag_name"] as string : null;
            string version = tag != null && tag.StartsWith("v", StringComparison.Ordinal) ? tag.Substring(1) : null;
            if (!VersionValid(version)) throw new InvalidDataException("Unsupported release version.");
            if (!item.ContainsKey("assets")) throw new InvalidDataException("Release has no Windows package.");
            var found = ArrayValue(item["assets"]).OfType<Dictionary<string, object>>().Where(a => a.ContainsKey("name") && (a["name"] as string) == AssetName(version)).ToArray();
            if (found.Length != 1) throw new InvalidDataException("Release needs one Windows package.");
            var asset = found[0];
            if (!asset.ContainsKey("browser_download_url") || (asset["browser_download_url"] as string) != AssetUrl(version) ||
                !asset.ContainsKey("state") || (asset["state"] as string) != "uploaded") throw new InvalidDataException("Unexpected package location.");
            string digest = asset.ContainsKey("digest") ? asset["digest"] as string : null;
            var release = new UpdateRelease { Version = version, Size = asset.ContainsKey("size") ? Convert.ToInt64(asset["size"]) : 0,
                Digest = digest != null && digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest.Substring(7) : null,
                Summary = ReadSummary(item.ContainsKey("body") ? item["body"] as string : null) };
            if (!MetadataValid(release)) throw new InvalidDataException("Invalid Windows package metadata.");
            if (!Valid(release)) release.Digest = null; // Still offer the manual release-page path.
            return release;
        }
        public static bool DownloadLocation(Uri uri, string version, bool redirected) {
            if (uri == null || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
            if (!redirected) return uri.AbsoluteUri == AssetUrl(version);
            return uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com";
        }
        public static string EntryPath(string name) {
            const string prefix = "firefox-ghost-ui/";
            if (name == null || !name.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected ZIP root.");
            string relative = name.Substring(prefix.Length);
            if (relative.Length == 0) return null;
            if (relative.Length > 180 || relative.IndexOf('\\') >= 0 || relative.IndexOf(':') >= 0) throw new InvalidDataException("Unsafe ZIP path.");
            string[] parts = relative.TrimEnd('/').Split('/');
            if (parts.Any(p => !Regex.IsMatch(p, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\z") || p.EndsWith(".", StringComparison.Ordinal) ||
                Regex.IsMatch(p, @"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|\z)", RegexOptions.IgnoreCase))) throw new InvalidDataException("Unsafe ZIP filename.");
            if (name.EndsWith("/", StringComparison.Ordinal)) return null;
            bool rootFile = parts.Length == 1 && new[] { "Install.cmd", "Install.ps1", "Uninstall.cmd", "Uninstall.ps1", "Install-Handles.ps1", "Uninstall-Handles.ps1", "README.md", "AGENTS.md", "CHANGELOG.md", "LICENSE", "VERSION" }.Contains(relative);
            bool nested = parts.Length >= 2 && new[] { "theme", "scripts", "companion", "docs" }.Contains(parts[0]) &&
                new[] { ".ps1", ".psm1", ".cs", ".css", ".config", ".manifest", ".md", ".png" }.Contains(Path.GetExtension(relative));
            if (!rootFile && !nested) throw new InvalidDataException("Unexpected packaged file.");
            return relative;
        }
        internal static DateTime Utc(string value) {
            DateTime result; return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out result) ? result.ToUniversalTime() : DateTime.MinValue;
        }
    }
    internal sealed class UpdateStore {
        internal readonly string Root;
        internal UpdateStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FirefoxGhostUI", "updates")) { }
        internal UpdateStore(string root) {
            Root = Path.GetFullPath(root);
            SafeDirectory(Root);
        }
        // Extended paths avoid depending on the machine-wide LongPathsEnabled
        // policy. Validate/canonicalize logical paths before crossing into I/O.
        internal static string IOPath(string path) {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            string full = Path.GetFullPath(path);
            if (full.Length < 240) return full;
            return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full.Substring(2) : @"\\?\" + full;
        }
        internal static void SafeDirectory(string path) {
            string io = IOPath(path);
            Directory.CreateDirectory(io);
            for (var directory = new DirectoryInfo(io); directory != null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected update path is not supported.");
        }
        internal static string ReadSmall(string path, int max) {
            path = IOPath(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (stream.Length > max || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid update metadata.");
                using (var reader = new StreamReader(stream, Encoding.UTF8, true)) return reader.ReadToEnd();
            }
        }
        internal UpdateState Read() {
            try {
                var state = UpdateRules.Json().Deserialize<UpdateState>(ReadSmall(Path.Combine(Root, "updates.json"), 65536));
                if (state == null) return new UpdateState();
                if (!UpdateRules.MetadataValid(state.Release)) state.Release = null;
                else if (!UpdateRules.Valid(state.Release)) state.Release.Digest = null;
                if (state.Release != null) state.Release.Summary = UpdateRules.ReadSummary("### Summary\n" + String.Join("\n", (state.Release.Summary ?? new string[0]).Select(s => "- " + s)));
                return state;
            } catch (Exception e) { if (!(e is IOException || e is ArgumentException || e is InvalidOperationException || e is UnauthorizedAccessException)) throw; return new UpdateState(); }
        }
        internal void Write(UpdateState state) {
            SafeDirectory(Root);
            string target = IOPath(Path.Combine(Root, "updates.json")), temp = IOPath(Path.Combine(Root, Guid.NewGuid().ToString("N") + ".tmp"));
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                byte[] data = Encoding.UTF8.GetBytes(UpdateRules.Json().Serialize(state)); file.Write(data, 0, data.Length); file.Flush(true);
            }
            if (File.Exists(target)) {
                if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected metadata was preserved.");
                File.Replace(temp, target, null);
            } else File.Move(temp, target);
        }
        internal string Package(UpdateRelease release) {
            if (!UpdateRules.Valid(release)) throw new InvalidDataException("Invalid package metadata.");
            return IOPath(Path.Combine(Root, "package-" + release.Version + "-" + release.Digest + ".zip"));
        }
        internal static bool Verify(Stream stream, UpdateRelease release) {
            if (!UpdateRules.Valid(release) || stream.Length != release.Size) return false;
            stream.Position = 0;
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() == release.Digest;
        }
        internal bool Ready(UpdateRelease release) {
            try {
                string file = Package(release);
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return false;
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) return Verify(stream, release);
            } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
        }
        internal void PromotePackage(string temporary, UpdateRelease release) {
            SafeDirectory(Root);
            temporary = IOPath(temporary);
            using (var incoming = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (!Verify(incoming, release)) throw new InvalidDataException("Package verification failed.");
            string target = Package(release);
            if (!File.Exists(target)) { File.Move(temporary, target); return; }
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected cached package was preserved.");
            if (Ready(release)) { File.Delete(temporary); return; }
            // Preserve the damaged file, but let a verified retry repair the
            // fixed cache path atomically. Never delete paths from metadata.
            string backup = IOPath(Path.Combine(Root, Guid.NewGuid().ToString("N") + ".invalid"));
            File.Replace(temporary, target, backup);
        }
        internal UpdateProfile[] Profiles() {
            string handles = Path.Combine(Path.GetDirectoryName(Root), "handles", "installed.json");
            try {
                var installed = UpdateRules.Json().Deserialize<Dictionary<string, object>>(ReadSmall(handles, 65536));
                if (!installed.ContainsKey("profiles")) return new UpdateProfile[0];
                var result = new List<UpdateProfile>();
                foreach (string path in UpdateRules.ArrayValue(installed["profiles"]).OfType<string>().Take(32)) {
                    try {
                        string full = Path.GetFullPath(path);
                        if (path != full || path.IndexOf('"') >= 0 || path.Any(Char.IsControl)) continue;
                        var state = UpdateRules.Json().Deserialize<Dictionary<string, object>>(ReadSmall(Path.Combine(path, "chrome", "ghost-ui", "state.json"), 262144));
                        string version = state != null && state.ContainsKey("version") ? state["version"] as string : null;
                        if (!UpdateRules.VersionValid(version) || !File.Exists(IOPath(Path.Combine(path, "prefs.js")))) continue;
                        // Core identity never depends on optional integration or CSS.
                        string origin = null;
                        if (state.ContainsKey("bonjourrOrigin")) origin = UpdateRules.ValidOrigin(state["bonjourrOrigin"] as string);
                        else {
                            // Legacy schema-1 profiles: read only the exact UUID
                            // preference, never treat arbitrary stylesheet URLs as config.
                            try { origin = LegacyBonjourrOrigin(ReadSmall(Path.Combine(path, "prefs.js"), 2 * 1024 * 1024)); }
                            catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
                        }
                        result.Add(new UpdateProfile { Path = path, Version = version, Origin = origin });
                    } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
                }
                return result.GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
            } catch (IOException) { return new UpdateProfile[0]; } catch (ArgumentException) { return new UpdateProfile[0]; } catch (InvalidOperationException) { return new UpdateProfile[0]; }
        }
        internal static string LegacyBonjourrOrigin(string preferences) {
            MatchCollection found = Regex.Matches(preferences, @"(?m)^user_pref\(""extensions\.webextensions\.uuids"", (""(?:[^""\r\n\\]|\\.)*"")\);\r?$");
            if (found.Count != 1) return null;
            string encoded = UpdateRules.Json().Deserialize<string>(found[0].Groups[1].Value);
            var map = UpdateRules.Json().Deserialize<Dictionary<string, object>>(encoded);
            object uuid;
            return map != null && map.TryGetValue(UpdateRules.BonjourrId, out uuid) && uuid is string
                ? UpdateRules.ValidOrigin("moz-extension://" + ((string)uuid).ToLowerInvariant() + "/") : null;
        }
        internal string CompanionExecutable() {
            string handles = Path.Combine(Path.GetDirectoryName(Root), "handles");
            var installed = UpdateRules.Json().Deserialize<Dictionary<string, object>>(ReadSmall(Path.Combine(handles, "installed.json"), 65536));
            string executable = installed.ContainsKey("executable") ? installed["executable"] as string : null;
            if (String.IsNullOrEmpty(executable) || Path.GetFullPath(executable) != executable ||
                !executable.StartsWith(handles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(executable) != "GhostHandles.exe") throw new InvalidDataException("Companion registration is invalid.");
            return executable;
        }
    }
    internal static class UpdateNetwork {
        // GitHub serves update metadata and packages over TLS. System-default
        // protocol selection may lack TLS 1.2 on legacy Framework
        // configurations; keep system protocols and explicitly enable TLS 1.2.
        static UpdateNetwork() { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
        internal static HttpWebResponse Open(Uri uri, bool api, CancellationToken token) {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.AllowAutoRedirect = false; request.Timeout = 15000; request.ReadWriteTimeout = 15000;
            request.UserAgent = "FirefoxGhostUI-UpdateCheck"; request.Accept = api ? "application/vnd.github+json" : "application/octet-stream";
            if (api) request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            using (token.Register(request.Abort)) return (HttpWebResponse)request.GetResponse();
        }
        internal static UpdateRelease Latest(UpdateState state) {
            try {
                using (var response = Open(new Uri("https://api.github.com/repos/DekrovDev/firefox-ghost-ui/releases/latest"), true, CancellationToken.None))
                using (var input = response.GetResponseStream())
                using (var memory = new MemoryStream()) {
                    Copy(input, memory, 1024 * 1024, CancellationToken.None, null, 0);
                    return UpdateRules.ParseRelease(Encoding.UTF8.GetString(memory.ToArray()));
                }
            } catch (WebException e) {
                using (var response = e.Response as HttpWebResponse) {
                    if (response != null && ((int)response.StatusCode == 403 || (int)response.StatusCode == 429)) {
                        long seconds;
                        if (!Int64.TryParse(response.Headers["Retry-After"], out seconds)) {
                            long reset;
                            seconds = Int64.TryParse(response.Headers["X-RateLimit-Reset"], out reset) ? reset - (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds : 3600;
                        }
                        state.RetryUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, Math.Min(86400, seconds))).ToString("o");
                    }
                }
                throw;
            }
        }
        internal static void Copy(Stream input, Stream output, long limit, CancellationToken token, Action<int> progress, long expected) {
            var watch = Stopwatch.StartNew(); byte[] buffer = new byte[32768]; long total = 0; int count, last = -1;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0) {
                token.ThrowIfCancellationRequested(); total += count;
                if (total > limit || watch.Elapsed.TotalMinutes > 5) throw new InvalidDataException("Download exceeded its limit.");
                output.Write(buffer, 0, count);
                int value = expected > 0 ? (int)Math.Min(100, total * 100 / expected) : 0;
                if (progress != null && value != last) { last = value; progress(value); }
            }
        }
        internal static void Download(UpdateStore store, UpdateRelease release, CancellationToken token, Action<int> progress) {
            Uri uri = new Uri(UpdateRules.AssetUrl(release.Version));
            string temporary = UpdateStore.IOPath(Path.Combine(store.Root, Guid.NewGuid().ToString("N") + ".part"));
            try {
                for (int redirects = 0; redirects <= 3; redirects++) {
                    token.ThrowIfCancellationRequested();
                    if (!UpdateRules.DownloadLocation(uri, release.Version, redirects > 0)) throw new InvalidDataException("Unexpected download redirect.");
                    using (var response = Open(uri, false, token)) {
                        if ((int)response.StatusCode == 302 || (int)response.StatusCode == 301 || (int)response.StatusCode == 307) {
                            uri = new Uri(uri, response.Headers["Location"]); continue;
                        }
                        if (response.StatusCode != HttpStatusCode.OK || response.ContentLength > release.Size) throw new InvalidDataException("Invalid package response.");
                        using (var input = response.GetResponseStream())
                        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) {
                            Copy(input, output, release.Size, token, progress, release.Size); output.Flush(true);
                        }
                        store.PromotePackage(temporary, release);
                        return;
                    }
                }
                throw new InvalidDataException("Too many download redirects.");
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    // Read only accessibility roles/states and the active document URL, not page text.
    internal static class StartPageProbe {
        [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible);
        [DllImport("oleacc.dll")] private static extern int AccessibleChildren(IAccessible container, int start, int count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children, out int obtained);
        internal static string Origin(IntPtr window, string[] origins) {
            IAccessible root = null;
            try {
                Guid iid = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71");
                if (AccessibleObjectFromWindow(window, unchecked((uint)-4), ref iid, out root) != 0 || root == null) return null;
                int budget = 2048; return Walk(root, origins, ref budget, 0, Stopwatch.StartNew());
            } catch (COMException) { return null; } catch (InvalidCastException) { return null; } catch (ArgumentException) { return null; }
            finally { if (root != null && Marshal.IsComObject(root)) Marshal.ReleaseComObject(root); }
        }
        private static string Walk(IAccessible item, string[] origins, ref int budget, int depth, Stopwatch watch) {
            if (--budget < 0 || depth > 24 || watch.ElapsedMilliseconds > 500) return null;
            int state = Convert.ToInt32(item.get_accState(0));
            if ((state & (0x8000 | 0x10000)) != 0) return null;
            int role = Convert.ToInt32(item.get_accRole(0));
            if (role == 15) {
                string url = item.get_accValue(0);
                // Never descend into documents or inspect their contents. Do not
                // accept title matching: websites can choose their own title.
                return UpdateRules.StartPage(url, origins);
            }
            int count = Math.Min(256, item.accChildCount);
            for (int start = 0; start < count && budget > 0; start += 32) {
                var children = new object[Math.Min(32, count - start)]; int obtained;
                if (AccessibleChildren(item, start, children.Length, children, out obtained) < 0) continue;
                for (int i = 0; i < obtained; i++) {
                    IAccessible child = children[i] as IAccessible;
                    if (child == null) continue;
                    try {
                        string result = Walk(child, origins, ref budget, depth + 1, watch);
                        if (result != null) return result;
                    } finally { if (Marshal.IsComObject(child)) Marshal.ReleaseComObject(child); }
                }
            }
            return null;
        }
    }
    internal static class UpdateVisuals {
        internal static readonly Color Surface = Color.FromArgb(36, 36, 36);
        internal static readonly Color Text = Color.FromArgb(243, 243, 243);
        internal static readonly Color Muted = Color.FromArgb(190, 190, 190);
        internal static readonly Color Edge = Color.FromArgb(82, 82, 82);
        internal static readonly Color ControlEdge = Color.FromArgb(112, 112, 112);
        internal static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle bounds, int radius) {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int diameter = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }
    }
    // Keep native button input and accessibility; customize only its appearance.
    internal sealed class UpdateButton : Button {
        internal bool PrimaryStyle, TextOnly, WrapText;
        internal string Symbol;
        private bool hovered, pressed;
        private readonly Font symbolFont = new Font("Segoe MDL2 Assets", 11);
        internal UpdateButton() {
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false; BackColor = UpdateVisuals.Surface;
            Cursor = Cursors.Hand; UseMnemonic = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = true; Invalidate(); } base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { pressed = false; Invalidate(); base.OnKeyUp(e); }
        protected override void OnPaint(PaintEventArgs e) {
            e.Graphics.Clear(BackColor);
            bool highContrast = SystemInformation.HighContrast;
            Color fill = highContrast ? SystemColors.Control : PrimaryStyle ? Color.FromArgb(230, 230, 230) : UpdateVisuals.Surface;
            Color ink = highContrast ? SystemColors.ControlText : PrimaryStyle ? Color.FromArgb(30, 30, 30) : UpdateVisuals.Text;
            if (!Enabled) ink = highContrast ? SystemColors.GrayText : Color.FromArgb(150, 150, 150);
            if (Enabled && hovered && !highContrast) fill = PrimaryStyle ? Color.White : Color.FromArgb(49, 49, 49);
            if (Enabled && pressed && !highContrast) fill = PrimaryStyle ? Color.FromArgb(199, 199, 199) : Color.FromArgb(60, 60, 60);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (var path = UpdateVisuals.Rounded(bounds, (int)Math.Round((TextOnly ? 5 : 9) * DeviceDpi / 96f))) {
                if (!TextOnly || hovered || highContrast) using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
                if (!TextOnly && !PrimaryStyle) using (var pen = new Pen(highContrast ? SystemColors.WindowText : UpdateVisuals.ControlEdge)) e.Graphics.DrawPath(pen, path);
            }
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter |
                (WrapText ? TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl : TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            int symbolWidth = String.IsNullOrEmpty(Symbol) ? 0 : (int)Math.Round(20 * DeviceDpi / 96f);
            int gap = symbolWidth == 0 || String.IsNullOrEmpty(Text) ? 0 : (int)Math.Round(8 * DeviceDpi / 96f);
            var measureFlags = TextFormatFlags.NoPrefix | (WrapText ? TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl : TextFormatFlags.SingleLine);
            Size textSize = String.IsNullOrEmpty(Text) ? Size.Empty : TextRenderer.MeasureText(e.Graphics, Text, Font,
                new Size(WrapText ? Math.Max(1, Width - 12) : Int32.MaxValue, Int32.MaxValue), measureFlags);
            int textWidth = textSize.Width;
            if (textWidth > 0 && (WrapText || textWidth + symbolWidth + gap + 12 > Width)) { symbolWidth = 0; gap = 0; }
            int start = Math.Max(4, (Width - textWidth - symbolWidth - gap) / 2);
            if (symbolWidth > 0) TextRenderer.DrawText(e.Graphics, Symbol, symbolFont, new Rectangle(start, 0, symbolWidth, Height), ink,
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            if (textWidth > 0) {
                Rectangle textBounds = WrapText
                    ? new Rectangle(6, Math.Max(0, (Height - textSize.Height) / 2), Math.Max(1, Width - 12), Math.Min(Height, textSize.Height))
                    : new Rectangle(start + symbolWidth + gap, 0, Math.Max(1, Width - start - symbolWidth - gap), Height);
                TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, ink, WrapText ? flags | TextFormatFlags.HorizontalCenter : flags);
            }
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), ink, fill);
        }
        protected override void Dispose(bool disposing) { if (disposing) symbolFont.Dispose(); base.Dispose(disposing); }
    }
    internal sealed class UpdateProfilePicker : ComboBox {
        private readonly Font symbolFont = new Font("Segoe MDL2 Assets", 9);
        internal UpdateProfilePicker() {
            DropDownStyle = ComboBoxStyle.DropDownList; DrawMode = DrawMode.OwnerDrawFixed; FlatStyle = FlatStyle.Flat;
        }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void WndProc(ref Message message) {
            // Preserve the native ComboBox's input/notification path. UserPaint
            // changes that path; overlay only the collapsed face after painting.
            int paintMessage = message.Msg;
            IntPtr printContext = message.WParam;
            base.WndProc(ref message);
            if ((paintMessage == 0x000f || paintMessage == 0x0317 || paintMessage == 0x0318) && IsHandleCreated && !IsDisposed && !Disposing) {
                using (var graphics = paintMessage == 0x000f || printContext == IntPtr.Zero ? Graphics.FromHwnd(Handle) : Graphics.FromHdc(printContext)) PaintFace(graphics);
            }
        }
        private void PaintFace(Graphics graphics) {
            graphics.Clear(BackColor); graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            bool contrast = SystemInformation.HighContrast;
            Color ink = Enabled ? ForeColor : contrast ? SystemColors.GrayText : UpdateVisuals.Muted;
            using (var outline = UpdateVisuals.Rounded(new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), (int)Math.Round(7 * DeviceDpi / 96f)))
            using (var pen = new Pen(contrast || Focused ? ForeColor : UpdateVisuals.ControlEdge)) graphics.DrawPath(pen, outline);
            int arrow = (int)Math.Round(28 * DeviceDpi / 96f), pad = (int)Math.Round(10 * DeviceDpi / 96f);
            string text = SelectedIndex < 0 ? "Choose a profile" : GetItemText(SelectedItem);
            TextRenderer.DrawText(graphics, text, Font, new Rectangle(pad, 0, Math.Max(1, Width - arrow - pad), Height), ink,
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(graphics, "\uE70D", symbolFont, new Rectangle(Width - arrow, 0, arrow, Height), ink,
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -4, -4), ink, BackColor);
        }
        protected override void Dispose(bool disposing) { if (disposing) symbolFont.Dispose(); base.Dispose(disposing); }
    }
    internal sealed class UpdateCard : Form {
        internal event Action DismissRequested;
        internal event Action OpenRequested;
        internal event Action UserInteraction;
        private readonly Panel body = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        private readonly Button launcher;
        private readonly Label brand, statusIcon, profileCaption, summaryCaption;
        private readonly Button close;
        private readonly System.Collections.Generic.List<Font> ownedFonts = new System.Collections.Generic.List<Font>();
        private bool showAction = true, showConfirm, showCancel, showDestination;
        private int contentHeight, dividerY;
        private string layoutKey, fitKey;
        internal bool IsCompact { get; private set; }
        internal readonly Label TitleLabel, VersionLabel, SummaryLabel, StatusLabel;
        internal readonly Button Primary, Later, Cancel, Confirm, Check, Manual;
        internal readonly LinkLabel Changes;
        internal readonly ComboBox Profiles;
        internal readonly CheckBox AutoCheck;
        internal readonly TextBox Destination;
        private bool shortcutRegistered;
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams {
            get {
                var parameters = base.CreateParams;
                // An owned tool window stays out of the taskbar. Initial Show
                // remains nonactivating, but explicit user input may focus it.
                parameters.ExStyle = (parameters.ExStyle | 0x00000080) & ~0x08000000;
                return parameters;
            }
        }
        internal UpdateCard() {
            Text = "Ghost UI updates"; AccessibleName = Text; ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(400, 472);
            BackColor = SystemInformation.HighContrast ? SystemColors.Window : UpdateVisuals.Surface;
            ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : UpdateVisuals.Text;
            Font = OwnFont("Segoe UI", 10, FontStyle.Regular); Opacity = SystemInformation.HighContrast ? 1 : .98;
            DoubleBuffered = true;
            Controls.Add(body);
            launcher = new UpdateButton { Text = "Ghost UI updates", Symbol = "\uE72C", Dock = DockStyle.Fill, TextOnly = true,
                BackColor = BackColor, Visible = false, AccessibleName = "Open Ghost UI update settings" };
            launcher.MouseDown += delegate(object sender, MouseEventArgs e) {
                // A real click authorizes activation; automatic display and
                // programmatic opening must remain nonactivating.
                if (e.Button == MouseButtons.Left && Visible && IsCompact) Native.SetForegroundWindow(Handle);
            };
            launcher.Click += delegate { if (OpenRequested != null) OpenRequested(); };
            Controls.Add(launcher);
            brand = LabelAt("GHOST UI", 22, 16, 270, 20); brand.Font = OwnFont("Segoe UI", 8.5f, FontStyle.Bold);
            close = ButtonAt("", 350, 9, 28, 28, false); close.AccessibleName = "Remind me next Firefox session";
            ((UpdateButton)close).Symbol = "\uE711"; ((UpdateButton)close).TextOnly = true;
            statusIcon = LabelAt("\uE72C", 22, 49, 28, 30); statusIcon.Font = OwnFont("Segoe MDL2 Assets", 19, FontStyle.Regular);
            TitleLabel = LabelAt("Update available", 64, 48, 314, 28); TitleLabel.Font = OwnFont("Segoe UI", 14.5f, FontStyle.Regular);
            VersionLabel = LabelAt("", 64, 79, 314, 22); VersionLabel.ForeColor = MutedColor;
            profileCaption = LabelAt("Profile", 22, 112, 52, 32); profileCaption.TextAlign = ContentAlignment.MiddleLeft;
            Profiles = new UpdateProfilePicker { Left = 82, Top = 112, Width = 296, DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat, DrawMode = DrawMode.OwnerDrawFixed, BackColor = BackColor, ForeColor = ForeColor,
                AccessibleName = "Profile to update" };
            body.Controls.Add(Profiles);
            Profiles.DrawItem += delegate(object sender, DrawItemEventArgs e) {
                bool selected = (e.State & DrawItemState.Selected) != 0;
                Color background = selected ? SystemColors.Highlight : BackColor;
                Color ink = selected ? SystemColors.HighlightText : Profiles.Enabled ? ForeColor : MutedColor;
                using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
                string text = e.Index < 0 ? "Choose a profile" : Profiles.GetItemText(Profiles.Items[e.Index]);
                TextRenderer.DrawText(e.Graphics, text, Profiles.Font, Rectangle.Inflate(e.Bounds, -6, 0), ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                e.DrawFocusRectangle();
            };
            summaryCaption = LabelAt("What's new", 22, 164, 356, 24); summaryCaption.Font = OwnFont("Segoe UI", 10, FontStyle.Bold);
            SummaryLabel = LabelAt("", 22, 194, 356, 100);
            StatusLabel = LabelAt("Installation always needs your permission.", 22, 440, 356, 40); StatusLabel.ForeColor = MutedColor;
            Primary = ButtonAt("Download", 22, 340, 222, 38, true); ((UpdateButton)Primary).Symbol = "\uE896";
            Later = ButtonAt("Later", 256, 340, 122, 38, false);
            Confirm = ButtonAt("Confirm install", 22, 340, 222, 38, true); Confirm.Visible = false;
            Cancel = ButtonAt("Cancel", 256, 340, 122, 38, false); Cancel.Visible = false;
            Manual = ButtonAt("Download manually", 22, 390, 160, 24, false); ((UpdateButton)Manual).TextOnly = true;
            Manual.Font = OwnFont("Segoe UI", 10, FontStyle.Underline);
            Changes = LinkAt("All changes", 190, 392, 90);
            AutoCheck = new CheckBox { Text = "Check daily", Left = 22, Top = 420, Width = 190, Height = 32, Checked = true, AccessibleName = "Check for updates daily", UseVisualStyleBackColor = false };
            body.Controls.Add(AutoCheck);
            Check = ButtonAt("Check now", 266, 420, 112, 32, false);
            Destination = new TextBox { Left = 22, Top = 300, Width = 356, Height = 88, Multiline = true,
                ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle, Visible = false, AccessibleName = "Installation destination",
                BackColor = BackColor, ForeColor = ForeColor };
            body.Controls.Add(Destination);
            // Tab order follows the visual reading order, including an explicit destination.
            close.TabIndex = 0; Profiles.TabIndex = 1; Destination.TabIndex = 2;
            Primary.TabIndex = 3; Confirm.TabIndex = 3; Cancel.TabIndex = 4; Later.TabIndex = 4;
            Manual.TabIndex = 5; Changes.TabIndex = 6; AutoCheck.TabIndex = 7; Check.TabIndex = 8;
            body.Paint += delegate(object sender, PaintEventArgs e) {
                using (var pen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : UpdateVisuals.Edge)) {
                    if (body.ClientSize.Width > 1 && body.ClientSize.Height > 1)
                        using (var outline = UpdateVisuals.Rounded(new Rectangle(0, 0, body.ClientSize.Width - 1, body.ClientSize.Height - 1), S(12)))
                            e.Graphics.DrawPath(pen, outline);
                    int y = dividerY + body.AutoScrollPosition.Y;
                    e.Graphics.DrawLine(pen, S(22), y, body.ClientSize.Width - S(22), y);
                }
            };
            close.Click += delegate { if (DismissRequested != null) DismissRequested(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; if (DismissRequested != null) DismissRequested(); } };
        }
        internal void SetCompact(bool compact) {
            if (IsCompact == compact) return;
            IsCompact = compact;
            if (compact) body.AutoScrollPosition = Point.Empty;
            body.Visible = !compact; launcher.Visible = compact;
            Opacity = SystemInformation.HighContrast ? 1 : compact ? .90 : .98;
            ClientSize = compact ? new Size(S(176), S(38)) : new Size(S(400), Math.Max(S(300), contentHeight));
        }
        internal bool FitToWindow(Rectangle bounds) {
            if (bounds.Width < 300 || bounds.Height < 280) return false;
            int width = Math.Min(S(IsCompact ? 176 : 400), bounds.Width - 64);
            int maxHeight = bounds.Height - 100;
            string fit = width + ":" + maxHeight + ":" + DeviceDpi + ":" + IsCompact + ":" + showAction + ":" + showConfirm + ":" + showCancel + ":" + showDestination +
                ":" + TitleLabel.Text + ":" + VersionLabel.Text + ":" + SummaryLabel.Text + ":" + StatusLabel.Text + ":" + Primary.Text;
            if (fitKey == fit) return true;
            Point scroll = body.AutoScrollPosition;
            if (!IsCompact) {
                ArrangeContent(width, 0);
                // Measure again with the width actually available beside a vertical scrollbar.
                if (contentHeight > maxHeight) ArrangeContent(width, SystemInformation.VerticalScrollBarWidth);
                body.AutoScrollMinSize = new Size(0, contentHeight);
            } else body.AutoScrollMinSize = Size.Empty;
            Size fitted = new Size(width, Math.Min(IsCompact ? S(38) : contentHeight, maxHeight));
            if (ClientSize != fitted) ClientSize = fitted;
            if (!IsCompact && body.AutoScrollPosition != scroll)
                body.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
            fitKey = fit;
            return true;
        }
        private int S(int value) { return (int)Math.Round(value * DeviceDpi / 96f); }
        private Color MutedColor { get { return SystemInformation.HighContrast ? SystemColors.WindowText : UpdateVisuals.Muted; } }
        private Font OwnFont(string family, float size, FontStyle style) {
            var font = new Font(family, size, style); ownedFonts.Add(font); return font;
        }
        internal void Present(bool action, bool confirmation, bool approved, bool downloading, bool current) {
            showAction = action; showConfirm = confirmation; showCancel = confirmation || approved || downloading;
            showDestination = confirmation || approved;
            Primary.Visible = action; Confirm.Visible = confirmation; Cancel.Visible = showCancel;
            Later.Visible = !confirmation && !approved && !downloading;
            Destination.Visible = showDestination;
            statusIcon.Text = current ? "\uE930" : "\uE72C";
            statusIcon.ForeColor = current && !SystemInformation.HighContrast ? Color.FromArgb(133, 200, 138) : ForeColor;
            statusIcon.AccessibleName = current ? "Up to date" : "Update status";
            ((UpdateButton)Primary).Symbol = Primary.Text == "Download" ? "\uE896" : "\uE73E";
            // Never use inherited Visible values to infer state while the form is hidden.
            // FitToWindow performs the measured layout before the next Show.
            Invalidate();
        }
        private int MeasuredHeight(Label label, int width) {
            using (var graphics = CreateGraphics()) {
                var flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;
                int rendered = TextRenderer.MeasureText(graphics, label.Text, label.Font, new Size(Math.Max(1, width), Int32.MaxValue), flags).Height;
                return Math.Max(rendered, label.GetPreferredSize(new Size(Math.Max(1, width), 0)).Height) + S(4);
            }
        }
        private void ArrangeContent(int width, int scrollbar) {
            int usable = width - scrollbar, pad = Math.Min(S(22), Math.Max(8, usable / 12));
            int inner = Math.Max(1, usable - pad * 2);
            bool narrow = inner < S(320);
            foreach (var button in new[] { Primary, Confirm, Cancel, Later, Manual, Check }) ((UpdateButton)button).WrapText = narrow;
            bool actions = showAction || showConfirm || showCancel;
            string key = usable + ":" + DeviceDpi + ":" + actions + ":" + showDestination + ":" + showConfirm + ":" + showCancel +
                ":" + TitleLabel.Text + ":" + VersionLabel.Text + ":" + SummaryLabel.Text + ":" + StatusLabel.Text;
            if (layoutKey == key) return;
            layoutKey = key;
            Point offset = body.AutoScrollPosition;
            body.SuspendLayout();
            try {
                brand.Visible = inner >= S(140);
                Place(brand, pad, S(14), Math.Max(1, inner - S(40)), S(22), offset);
                Place(close, usable - pad - S(28), S(9), S(28), S(28), offset);
                Place(statusIcon, pad, S(48), S(28), S(32), offset);
                bool stackedHeading = inner < S(220);
                int titleTop = S(stackedHeading ? 84 : 46);
                int textX = stackedHeading ? pad : pad + S(42), textWidth = Math.Max(1, inner - (stackedHeading ? 0 : S(42)));
                int titleHeight = Math.Max(S(27), MeasuredHeight(TitleLabel, textWidth));
                Place(TitleLabel, textX, titleTop, textWidth, titleHeight, offset);
                int versionTop = titleTop + titleHeight + S(2);
                int versionHeight = MeasuredHeight(VersionLabel, textWidth);
                Place(VersionLabel, textX, versionTop, textWidth, versionHeight, offset);
                int y = versionTop + versionHeight + S(18);
                Profiles.ItemHeight = S(22);
                int profileHeight = Math.Max(S(32), Profiles.PreferredHeight);
                if (narrow) {
                    Place(profileCaption, pad, y, inner, S(24), offset); y += S(26);
                    Place(Profiles, pad, y, inner, profileHeight, offset);
                } else {
                    Place(profileCaption, pad, y, S(52), profileHeight, offset);
                    Place(Profiles, pad + S(60), y, inner - S(60), profileHeight, offset);
                }
                y += profileHeight + S(18);
                int captionHeight = Math.Max(S(24), MeasuredHeight(summaryCaption, inner));
                Place(summaryCaption, pad, y, inner, captionHeight, offset); y += captionHeight + S(5);
                int summaryHeight = MeasuredHeight(SummaryLabel, inner);
                Place(SummaryLabel, pad, y, inner, summaryHeight, offset);
                y += summaryHeight + S(16); dividerY = y; y += S(14);
                if (showDestination) {
                    Place(Destination, pad, y, inner, S(88), offset); y += S(102);
                }
                int rightWidth = narrow ? inner : S(106), leftWidth = narrow ? inner : inner - rightWidth - S(12);
                var later = (UpdateButton)Later; later.TextOnly = !actions;
                if (actions) {
                    Place(Primary, pad, y, leftWidth, S(38), offset);
                    Place(Confirm, pad, y, leftWidth, S(38), offset);
                    int secondaryX = narrow ? pad : pad + leftWidth + S(12);
                    int secondaryY = narrow && (showAction || showConfirm) ? y + S(46) : y;
                    Place(Later, secondaryX, secondaryY, rightWidth, S(38), offset);
                    Place(Cancel, showAction || showConfirm ? secondaryX : pad, secondaryY,
                        showAction || showConfirm ? rightWidth : inner, S(38), offset);
                    y = secondaryY + S(50);
                }
                if (narrow) {
                    Place(Manual, pad, y, inner, S(42), offset); y += S(46);
                    Place(Changes, pad, y, inner, S(26), offset); y += S(30);
                    if (!actions) { Place(Later, pad, y, inner, S(30), offset); y += S(34); }
                    Place(AutoCheck, pad, y, inner, S(36), offset); y += S(42);
                    Place(Check, pad, y, inner, S(36), offset); y += S(48);
                } else {
                    Place(Manual, pad - S(5), y, S(155), S(26), offset);
                    Place(Changes, pad + S(163), y + S(3), S(84), S(23), offset);
                    if (!actions) Place(Later, usable - pad - S(62), y, S(62), S(26), offset);
                    y += S(38);
                    Place(AutoCheck, pad, y, inner - S(124), S(32), offset);
                    Place(Check, usable - pad - S(112), y, S(112), S(32), offset);
                    y += S(46);
                }
                int statusHeight = MeasuredHeight(StatusLabel, inner);
                Place(StatusLabel, pad, y, inner, statusHeight, offset);
                contentHeight = y + statusHeight + S(20);
                body.Invalidate();
            } finally { body.ResumeLayout(false); }
        }
        private static void Place(Control control, int x, int y, int width, int height, Point offset) {
            var bounds = new Rectangle(x + offset.X, y + offset.Y, width, height);
            if (control.Bounds != bounds) control.Bounds = bounds;
        }
        internal void OpenSettings() { launcher.PerformClick(); }
        internal void SetShortcutEnabled(bool enabled) {
            if (enabled && !shortcutRegistered) shortcutRegistered = Native.RegisterHotKey(Handle, 3, 0x4003, 0x77); // Ctrl+Alt+F8
            else if (!enabled && shortcutRegistered) { Native.UnregisterHotKey(Handle, 3); shortcutRegistered = false; }
        }
        protected override void WndProc(ref Message message) {
            // WM_MOUSEACTIVATE precedes Firefox losing accessibility focus.
            // A real left press on an automatic card enters manual mode before
            // those focus events can invalidate the page-specific presentation.
            if (message.Msg == 0x0021 && ((message.LParam.ToInt64() >> 16) & 0xffff) == 0x0201 && Visible) {
                if (IsCompact) ActiveControl = launcher;
                if (UserInteraction != null) UserInteraction();
                // Own the explicit activation instead of forwarding it through
                // the foreign Firefox owner; do not eat the first mouse press.
                message.Result = new IntPtr(1); // MA_ACTIVATE
                return;
            }
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == 3 && Visible) {
                IntPtr foreground = Native.GetForegroundWindow();
                if (Native.IsFirefox(foreground) || foreground == Handle || Native.GetAncestor(foreground, 2) == Handle) {
                    if (OpenRequested != null) OpenRequested();
                    Native.SetForegroundWindow(Handle);
                }
            }
            base.WndProc(ref message);
        }
        protected override void Dispose(bool disposing) {
            if (disposing && shortcutRegistered) { Native.UnregisterHotKey(Handle, 3); shortcutRegistered = false; }
            base.Dispose(disposing);
            if (disposing) { foreach (var font in ownedFonts) font.Dispose(); ownedFonts.Clear(); }
        }
        protected override bool ProcessCmdKey(ref Message message, Keys key) {
            if (key == Keys.Escape) { if (DismissRequested != null) DismissRequested(); return true; }
            return base.ProcessCmdKey(ref message, key);
        }
        private Label LabelAt(string text, int x, int y, int width, int height) {
            var label = new Label { Text = text, Left = x, Top = y, Width = width, Height = height, UseMnemonic = false,
                AutoEllipsis = false, UseCompatibleTextRendering = false };
            body.Controls.Add(label); return label;
        }
        private Button ButtonAt(string text, int x, int y, int width, int height, bool primary) {
            var button = new UpdateButton { Text = text, Left = x, Top = y, Width = width, Height = height,
                PrimaryStyle = primary, BackColor = BackColor, ForeColor = ForeColor };
            body.Controls.Add(button); return button;
        }
        private LinkLabel LinkAt(string text, int x, int y, int width) {
            var link = new LinkLabel { Text = text, Left = x, Top = y, Width = width, Height = 24,
                LinkColor = MutedColor, ActiveLinkColor = ForeColor, VisitedLinkColor = MutedColor, LinkBehavior = LinkBehavior.AlwaysUnderline };
            body.Controls.Add(link); return link;
        }
        protected override void OnResize(EventArgs e) {
            base.OnResize(e);
            if (Width < 1 || Height < 1) return;
            using (var path = UpdateVisuals.Rounded(new Rectangle(0, 0, Width, Height), IsCompact ? Height / 2 : S(12))) {
                Region old = Region; Region = new Region(path); if (old != null) old.Dispose();
            }
        }
    }
    internal sealed class UpdateController : IDisposable {
        private delegate void WindowEvent(IntPtr hook, uint kind, IntPtr window, int objectId, int childId, uint thread, uint time);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WindowEvent callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        private readonly WindowEvent windowEvent;
        private readonly IntPtr titleHook, selectionHook;
        private int invalidationQueued;
        private readonly UpdateStore store;
        private UpdateState state;
        private UpdateProfile[] profiles = new UpdateProfile[0];
        private readonly UpdateCard card = new UpdateCard();
        private sealed class CardOwner : IWin32Window {
            internal IntPtr Window;
            public IntPtr Handle { get { return Window; } }
        }
        private readonly CardOwner cardOwner = new CardOwner();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private bool busy, ready, disposed, probing, refreshPending, confirming, launching;
        private bool manualOpen;
        private CancellationTokenSource download;
        private DateTime nextProbe, detectedAt, nextProfiles, consentUntil, startedAt = DateTime.UtcNow;
        private DateTime profileCheckedAt = DateTime.MinValue;
        private IntPtr detectedWindow, owner;
        private string detectedOrigin, session, consentProfile, consentVersion;
        private string status;
        private int generation;
        private int pageGeneration;
        internal UpdateController() : this(new UpdateStore()) { }
        internal UpdateController(UpdateStore updateStore) {
            store = updateStore; state = store.Read();
            IntPtr initialHandle = card.Handle; // BeginInvoke must work before the first visible card.
            status = state.Result;
            windowEvent = delegate(IntPtr hook, uint kind, IntPtr window, int objectId, int childId, uint thread, uint time) {
                IntPtr top = Native.GetAncestor(window, 2);
                if (top != IntPtr.Zero && (top == owner || top == detectedWindow) && ((objectId == 0 && childId == 0) || (kind >= 0x8006 && kind <= 0x8009)) &&
                    Interlocked.Exchange(ref invalidationQueued, 1) == 0) Post(delegate {
                    Interlocked.Exchange(ref invalidationQueued, 0);
                    pageGeneration++; detectedOrigin = null;
                    // Only automatic full cards depend on page identity.
                    // Hiding the compact entry on Firefox focus/selection events
                    // can remove it between mouse activation and MouseDown.
                    if (!manualOpen && !card.IsCompact) card.Hide();
                    nextProbe = DateTime.MinValue;
                });
            };
            titleHook = SetWinEventHook(0x800c, 0x800c, IntPtr.Zero, windowEvent, 0, 0, 2); // Window title changes, skip own process.
            selectionHook = SetWinEventHook(0x8006, 0x8009, IntPtr.Zero, windowEvent, 0, 0, 2); // Tab/selection changes invalidate a pending probe.
            card.AutoCheck.Checked = !state.Disabled;
            card.AutoCheck.CheckedChanged += delegate { manualOpen = true; state.Disabled = !card.AutoCheck.Checked; Save(); };
            card.Primary.Click += delegate { manualOpen = true; if (ready) Confirm(); else Download(); };
            card.Later.Click += delegate { Dismiss(); };
            card.DismissRequested += Dismiss;
            card.OpenRequested += OpenManually;
            card.UserInteraction += delegate {
                if (!card.IsCompact && Native.IsFirefox(owner) && !Native.IsIconic(owner) && !Fullscreen(owner)) manualOpen = true;
            };
            card.Cancel.Click += delegate { Cancel(); };
            card.Confirm.Click += delegate { Consent(); };
            card.Check.Click += delegate { Check(true); };
            card.Manual.Click += delegate {
                confirming = false; consentUntil = DateTime.MinValue;
                if (download != null) download.Cancel();
                status = "Extract the Windows ZIP, close Firefox, then run Install.cmd.";
                Render(); OpenRelease();
            };
            card.Changes.LinkClicked += delegate { OpenRelease(); };
            card.Profiles.SelectedIndexChanged += delegate { confirming = false; consentUntil = DateTime.MinValue; Render(); };
            card.Profiles.SelectionChangeCommitted += delegate { manualOpen = true; };
            timer.Interval = 150; timer.Tick += delegate { Tick(); }; timer.Start();
            RefreshProfiles(); VerifyCache();
        }
        private void Post(Action action) {
            if (disposed) return;
            try { card.BeginInvoke((MethodInvoker)delegate { if (!disposed) action(); }); }
            catch (InvalidOperationException) { }
        }
        private void Save() {
            try { store.Write(state); } catch (IOException) { status = "Could not save update settings."; } catch (UnauthorizedAccessException) { status = "Could not save update settings."; }
        }
        private UpdateProfile Selected { get { return card.Profiles.SelectedItem as UpdateProfile; } }
        private void RefreshProfiles() {
            if (refreshPending) return;
            refreshPending = true; nextProfiles = DateTime.UtcNow.AddSeconds(5);
            Task.Run(delegate {
                UpdateProfile[] found;
                try { found = store.Profiles(); } catch { found = new UpdateProfile[0]; }
                Post(delegate {
                    refreshPending = false;
                    profileCheckedAt = DateTime.UtcNow;
                    if (String.Join("|", profiles.Select(p => p.Path + p.Version + p.Origin)) == String.Join("|", found.Select(p => p.Path + p.Version + p.Origin))) return;
                    // A formerly automatic sole-profile choice is no longer
                    // sufficient when another destination becomes available.
                    bool ambiguousGrowth = found.Length > profiles.Length && found.Length > 1;
                    string selected = Selected == null || ambiguousGrowth ? null : Selected.Path;
                    profiles = found; card.Profiles.Items.Clear(); card.Profiles.Items.AddRange(found);
                    card.Profiles.SelectedIndex = Math.Max(-1, Array.FindIndex(found, p => p.Path == selected));
                    if (card.Profiles.SelectedIndex < 0) SelectContextProfile();
                    Render();
                });
            });
        }
        private void SelectContextProfile() {
            bool fresh = detectedWindow == owner && DateTime.UtcNow - detectedAt <= TimeSpan.FromMilliseconds(800);
            var matching = profiles.Where(p => fresh && p.Origin != null && p.Origin == detectedOrigin).ToArray();
            card.Profiles.SelectedItem = matching.Length == 1 ? matching[0] : profiles.Length == 1 ? profiles[0] : null;
        }
        private void OpenManually() {
            IntPtr foreground = Native.GetForegroundWindow();
            IntPtr firefox = foreground == card.Handle || Native.GetAncestor(foreground, 2) == card.Handle ? owner : foreground;
            if (!Native.IsFirefox(firefox) || Native.IsIconic(firefox) || Fullscreen(firefox)) return;
            cardOwner.Window = firefox;
            bool changed = owner != firefox;
            bool alreadyOpen = card.Visible && !card.IsCompact && !changed;
            if (changed) { card.Hide(); owner = firefox; detectedOrigin = null; pageGeneration++; }
            if (!alreadyOpen) SelectContextProfile();
            manualOpen = true; card.SetCompact(false); Render(); Tick();
        }
        private void VerifyCache() {
            var release = state.Release; int stamp = ++generation; ready = false;
            if (release == null) { Render(); return; }
            Task.Run(delegate { bool verified = store.Ready(release); Post(delegate { if (stamp == generation) { ready = verified; Render(); } }); });
        }
        private void Check(bool manual) {
            if (manual) manualOpen = true;
            if (confirming || consentUntil != DateTime.MinValue) return;
            if (busy || DateTime.UtcNow < UpdateRules.Utc(state.RetryUtc)) {
                if (manual) { status = "Please wait before checking again."; Render(); } return;
            }
            busy = true; card.Check.Enabled = false; status = manual ? "Checking for updates..." : status; Render();
            Task.Run(delegate {
                UpdateRelease release = null; string error = null;
                var timing = new UpdateState();
                try { release = UpdateNetwork.Latest(timing); } catch (Exception e) { error = e is InvalidDataException ? e.Message : "Could not check GitHub. Try again later."; }
                Post(delegate {
                    busy = false; card.Check.Enabled = true;
                    // A failed check never erases a previously verified download.
                    state.RetryUtc = timing.RetryUtc ?? DateTime.UtcNow.AddSeconds(error == null ? 60 : 900).ToString("o");
                    if (error == null) {
                        state.CheckedUtc = DateTime.UtcNow.ToString("o");
                        if (state.Release == null || UpdateRules.Compare(release.Version, state.Release.Version) >= 0) {
                            state.Release = release; if (manual) state.DismissedVersion = null;
                        }
                        status = manual ? "Update check completed." : null;
                    } else if (manual) status = error;
                    Save(); VerifyCache();
                });
            });
        }
        private void Download() {
            var release = state.Release;
            if (busy || !UpdateRules.Valid(release)) return;
            busy = true; download = new CancellationTokenSource(); var token = download.Token;
            status = "Downloading update..."; Render();
            Task.Run(delegate {
                string error = null;
                try { UpdateNetwork.Download(store, release, token, p => Post(delegate { status = "Downloading update... " + p + "%"; Render(); })); }
                catch (Exception e) { error = token.IsCancellationRequested ? "Download cancelled." : e is InvalidDataException ? e.Message : "Download failed. Try again or download manually."; }
                Post(delegate {
                    busy = false; download.Dispose(); download = null;
                    status = error ?? "Download verified. Install when you are ready.";
                    Save(); VerifyCache();
                });
            });
        }
        private void Confirm() {
            if (Selected == null || busy || !ready) return;
            manualOpen = true;
            confirming = true;
            status = "Install to the selected profile? Then close Firefox normally within 2 minutes. Cancel to keep your current version.";
            Render();
        }
        private void Consent() {
            if (!confirming || busy || !ready || Selected == null || state.Release == null) return;
            consentProfile = Selected.Path; consentVersion = state.Release.Version;
            consentUntil = DateTime.UtcNow.AddMinutes(2); confirming = false;
            status = "Installation approved. Close Firefox normally within 2 minutes, or Cancel."; Render();
        }
        private void Cancel() {
            if (download != null) download.Cancel();
            confirming = false; consentUntil = DateTime.MinValue;
            status = "Installation was not started."; Render();
        }
        private void Dismiss() {
            // Dismissal also revokes an in-memory installation approval.
            confirming = false; consentUntil = DateTime.MinValue; manualOpen = false;
            UpdateRules.RememberDismissal(state, session, state.Release == null ? "none" : state.Release.Version, SessionAlive);
            Save(); card.Hide();
        }
        private void OpenRelease() {
            string url = state.Release == null ? UpdateRules.Repository + "/releases/latest" : UpdateRules.ReleaseUrl(state.Release.Version);
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { status = "Could not open the release page."; Render(); }
        }
        private void Render() {
            var release = state.Release; var selected = Selected;
            bool approved = consentUntil > DateTime.UtcNow;
            bool current = release != null && selected != null && UpdateRules.Compare(release.Version, selected.Version) <= 0;
            card.TitleLabel.Text = release == null ? "Ghost UI updates" : current ? "Up to date" : ready ? "Ready to install" : "Update available";
            card.VersionLabel.Text = release == null ? "Check for Ghost UI updates" : current ? "Version " + selected.Version + " installed" :
                selected == null ? "Version " + release.Version + " available" : selected.Version + " \u2192 " + release.Version;
            card.SummaryLabel.Text = release == null || release.Summary == null || release.Summary.Length == 0 ? "Release notes unavailable. See All changes." : String.Join("\n", release.Summary.Select(s => "\u2022 " + s));
            card.Destination.Visible = confirming || approved;
            card.Destination.Text = selected == null ? "Select the Firefox profile to update." : "Install version " + (release == null ? "" : release.Version) + " to:\r\n" + selected.Path;
            card.Profiles.AccessibleDescription = selected == null ? "Choose an installed Firefox profile explicitly." : selected.Path;
            card.StatusLabel.Text = release != null && !UpdateRules.Valid(release) ? "Automatic download unavailable. Choose Download manually." : status ??
                (ready && !current ? "Downloaded and verified. Installation needs your permission." : !current && release != null ? "Downloading does not install the update." : "Installation always needs your permission.");
            card.Primary.Text = ready ? "Install update" : "Download";
            card.Primary.Enabled = !busy && UpdateRules.Valid(release) && selected != null && UpdateRules.Compare(release.Version, selected.Version) > 0;
            card.Profiles.Enabled = !busy && !confirming && !approved;
            card.Check.Enabled = !busy && !approved && !confirming;
            card.Present(release != null && !current && !confirming && !approved && download == null, confirming, approved, download != null, current);
        }
        private void Tick() {
            if (disposed || launching) return;
            DateTime now = DateTime.UtcNow;
            try {
                if (now >= nextProfiles) RefreshProfiles();
                if (!state.Disabled && now - startedAt > TimeSpan.FromSeconds(15) && !busy && now - UpdateRules.Utc(state.CheckedUtc) >= TimeSpan.FromHours(24) && now >= UpdateRules.Utc(state.RetryUtc)) Check(false);
                if (consentUntil != DateTime.MinValue) {
                    if (now >= consentUntil) { consentUntil = DateTime.MinValue; status = "Approval expired. Click Install update to try again."; Render(); }
                    else if (!FirefoxRunning()) { LaunchInstall(); return; }
                }
                if (now - profileCheckedAt > TimeSpan.FromSeconds(10) || profiles.Length == 0) { card.Hide(); card.SetShortcutEnabled(false); return; }
                bool offered = state.Release != null && profiles.Any(p => UpdateRules.Compare(state.Release.Version, p.Version) > 0);
                IntPtr foreground = Native.GetForegroundWindow();
                IntPtr firefox = foreground == card.Handle || Native.GetAncestor(foreground, 2) == card.Handle ? owner : foreground;
                if (!Native.IsFirefox(firefox) || Native.IsIconic(firefox) || Fullscreen(firefox)) { card.Hide(); card.SetShortcutEnabled(false); return; }
                uint pid; Native.GetWindowThreadProcessId(firefox, out pid);
                using (var process = Process.GetProcessById((int)pid)) session = pid + ":" + process.StartTime.ToUniversalTime().Ticks;
                string version = state.Release == null ? "none" : state.Release.Version;
                bool dismissed = UpdateRules.IsDismissed(state, session, version);
                // A failed/unknown page probe only suppresses automatic expansion.
                // The manual entry uses verified Firefox HWND identity on every tab.
                if (owner != IntPtr.Zero && owner != firefox) manualOpen = false;
                if (now >= nextProbe && !probing && offered && !dismissed && !manualOpen) Probe(firefox);
                bool startPage = detectedWindow == firefox && detectedOrigin != null && now - detectedAt <= TimeSpan.FromMilliseconds(800);
                bool compact = !manualOpen && (!offered || dismissed || !startPage);
                // WinForms consults its stored IWin32Window during sizing too.
                // Replace a closed Firefox owner's handle before any resize.
                cardOwner.Window = firefox;
                card.SetCompact(compact);
                Rectangle bounds = UpdateRules.VisibleBounds(Native.Bounds(firefox), Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray());
                if (!card.FitToWindow(bounds)) { card.Hide(); card.SetShortcutEnabled(false); return; }
                int margin = Math.Max(8, Math.Min((int)Math.Round(24 * card.DeviceDpi / 96f), Math.Min(bounds.Width - card.Width, bounds.Height - card.Height) / 2));
                int rightInset = card.IsCompact ? Math.Min((int)Math.Round(88 * card.DeviceDpi / 96f), bounds.Width - card.Width - margin) : margin;
                card.Location = new Point(bounds.Right - card.Width - rightInset, bounds.Bottom - card.Height - margin);
                if (!card.Visible || owner != firefox) {
                    card.Hide(); owner = firefox;
                    if (consentUntil == DateTime.MinValue) {
                        if (!manualOpen) SelectContextProfile();
                    }
                    card.Show(cardOwner);
                }
                card.SetShortcutEnabled(true);
            } catch { card.Hide(); card.SetShortcutEnabled(false); }
        }
        private void Probe(IntPtr window) {
            probing = true; nextProbe = DateTime.UtcNow.AddMilliseconds(350);
            int page = pageGeneration;
            string[] origins = profiles.Select(p => p.Origin).Where(o => o != null).Distinct().ToArray();
            var thread = new Thread(delegate() {
                string origin = null;
                try { origin = StartPageProbe.Origin(window, origins); } catch { }
                Post(delegate { probing = false; if (page != pageGeneration) return; detectedWindow = window; detectedOrigin = origin; detectedAt = DateTime.UtcNow; });
            });
            thread.IsBackground = true; thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        }
        private static bool Fullscreen(IntPtr window) {
            return !Native.IsZoomed(window) && Screen.AllScreens.Any(s => s.Bounds == Native.Bounds(window));
        }
        internal static bool FirefoxRunning() {
            Process[] processes = Process.GetProcessesByName("firefox");
            try { return processes.Length != 0; } finally { foreach (var process in processes) process.Dispose(); }
        }
        private static bool SessionAlive(string session) {
            string[] parts = session.Split(':'); int pid; long started;
            if (parts.Length != 2 || !Int32.TryParse(parts[0], out pid) || !Int64.TryParse(parts[1], out started)) return false;
            try { using (var process = Process.GetProcessById(pid)) return process.StartTime.ToUniversalTime().Ticks == started; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return true; } // Preserve a session whose identity cannot be read.
        }
        private void LaunchInstall() {
            if (state.Release == null || state.Release.Version != consentVersion || !profiles.Any(p => p.Path == consentProfile)) { Cancel(); return; }
            launching = true; card.Hide(); card.SetShortcutEnabled(false);
            consentUntil = DateTime.MinValue;
            try {
                var helper = UpdateInstaller.Start(store, state.Release, consentProfile);
                Task.Run(delegate {
                    try { helper.WaitForExit(); } catch (InvalidOperationException) { }
                    finally {
                        helper.Dispose();
                        Post(delegate {
                            // If the parent is still alive, handoff failed before
                            // stopping it. Restore a usable card rather than waiting
                            // forever for an installer which has already exited.
                            launching = false; state = store.Read();
                            status = state.Result ?? "Installation stopped. Try again or download manually.";
                            RefreshProfiles(); VerifyCache();
                        });
                    }
                });
            } catch { launching = false; status = "Could not start installation. Try again or download manually."; Render(); }
        }
        public void Dispose() {
            disposed = true; consentUntil = DateTime.MinValue;
            if (titleHook != IntPtr.Zero) UnhookWinEvent(titleHook);
            if (selectionHook != IntPtr.Zero) UnhookWinEvent(selectionHook);
            if (download != null) download.Cancel(); timer.Stop(); timer.Dispose(); card.Dispose();
        }
    }
    public sealed class InstallRequest {
        public UpdateRelease Release;
        public string Profile;
        public int Parent;
        public long ParentStartedUtc;
        public string Companion;
    }
    internal static class UpdateInstaller {
        internal static Process Start(UpdateStore store, UpdateRelease release, string profile) {
            string companion = store.CompanionExecutable();
            if (!String.Equals(companion, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Only the registered companion may start this update.");
            // A different process name/path is essential: the ordinary installer
            // stops all old GhostHandles processes while this helper must survive.
            string folder = Path.Combine(store.Root, "helper-" + Guid.NewGuid().ToString("N"));
            UpdateStore.SafeDirectory(folder);
            string helper = Path.Combine(folder, "GhostUpdater.exe");
            File.Copy(UpdateStore.IOPath(companion), UpdateStore.IOPath(helper), false);
            File.Copy(UpdateStore.IOPath(companion + ".config"), UpdateStore.IOPath(helper + ".config"), false);
            using (var parent = Process.GetCurrentProcess()) {
                var request = new InstallRequest { Release = release, Profile = profile, Parent = parent.Id,
                    ParentStartedUtc = parent.StartTime.ToUniversalTime().Ticks, Companion = companion };
                string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(UpdateRules.Json().Serialize(request)));
                var child = Process.Start(new ProcessStartInfo(helper, "--apply-update " + encoded) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                if (child == null) throw new InvalidOperationException("Could not start the update helper.");
                return child;
            }
        }
        internal static int Run(string encoded) {
            UpdateStore store = null; bool parentStopped = false; string companion = null;
            try {
                if (encoded == null || encoded.Length > 16384) return 1;
                var request = UpdateRules.Json().Deserialize<InstallRequest>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
                store = new UpdateStore();
                companion = store.CompanionExecutable();
                if (request == null || !UpdateRules.Valid(request.Release) || !store.Profiles().Any(p => p.Path == request.Profile && UpdateRules.Compare(request.Release.Version, p.Version) > 0)) throw new InvalidDataException("Profile is not registered for this update.");
                if (!String.Equals(request.Companion, companion, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(Application.ExecutablePath) != "GhostUpdater.exe") throw new InvalidDataException("Update helper identity is invalid.");
                if (UpdateController.FirefoxRunning()) throw new IOException("Firefox is still running. No update was installed.");
                // Parent must still be the consenting instance of this exact executable.
                using (var parent = Process.GetProcessById(request.Parent)) {
                    if (!String.Equals(parent.MainModule.FileName, companion, StringComparison.OrdinalIgnoreCase) ||
                        parent.StartTime.ToUniversalTime().Ticks != request.ParentStartedUtc) throw new InvalidDataException("Update approval is no longer active.");
                    string packageRoot = Extract(store, request.Release);
                    // Keep consent ephemeral: this process is the only handoff. There
                    // are no persisted install requests, scheduled tasks or retry jobs.
                    using (var stop = EventWaitHandle.OpenExisting("Local\\FirefoxGhostUIHandlesStop")) stop.Set();
                    parentStopped = true;
                    if (!parent.WaitForExit(5000)) throw new IOException("Companion did not stop. No installer was launched.");
                    string script = Path.Combine(packageRoot, "Install.ps1");
                    var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                        "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -ProfilePath \"" + request.Profile + "\" -NoPrompt") {
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = packageRoot
                    };
                    using (var child = Process.Start(start)) {
                        // Drain both streams concurrently. Retain no profile paths or
                        // unbounded installer output in the UI state.
                        child.OutputDataReceived += delegate { }; child.ErrorDataReceived += delegate { };
                        child.BeginOutputReadLine(); child.BeginErrorReadLine(); child.WaitForExit();
                        bool installed = child.ExitCode == 0 && store.Profiles().Any(p => p.Path == request.Profile && p.Version == request.Release.Version);
                        var state = store.Read(); state.Result = installed ? "Update installed. Start Firefox to apply it." : "Installation failed. Your backup was retained. Try manual installation."; store.Write(state);
                        if (!installed) Restart(companion);
                        return installed ? 0 : 1;
                    }
                }
            } catch {
                if (store != null) { try { var state = store.Read(); state.Result = "Update was not installed. Try again or download manually."; store.Write(state); } catch { } }
                if (parentStopped && companion != null) { try { Restart(companion); } catch { } }
                return 1;
            }
        }
        private static void Restart(string companion) {
            Process.Start(new ProcessStartInfo(companion) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        }
        internal static string Extract(UpdateStore store, UpdateRelease release) {
            string zip = store.Package(release);
            if ((File.GetAttributes(zip) & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected package was preserved.");
            using (var input = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (!UpdateStore.Verify(input, release)) throw new InvalidDataException("Package verification failed.");
                input.Position = 0;
                using (var archive = new ZipArchive(input, ZipArchiveMode.Read, true)) {
                    if (archive.Entries.Count > 256) throw new InvalidDataException("Too many packaged files.");
                    long size = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in archive.Entries) {
                        string relative = UpdateRules.EntryPath(entry.FullName);
                        if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("ZIP links are not supported.");
                        if (relative == null) continue;
                        if (!names.Add(relative)) throw new InvalidDataException("Duplicate ZIP entry.");
                        size = checked(size + entry.Length);
                        if (entry.Length > 32 * 1024 * 1024 || size > 128 * 1024 * 1024) throw new InvalidDataException("Unpacked package is too large.");
                    }
                    foreach (string required in new[] { "Install.ps1", "VERSION", "scripts/GhostUI.psm1", "scripts/Handles.psm1", "scripts/Build-Handles.ps1", "scripts/Select-Profile.ps1" })
                        if (!names.Contains(required)) throw new InvalidDataException("Incomplete Windows package.");
                    string root = Path.Combine(store.Root, "install-" + Guid.NewGuid().ToString("N"));
                    UpdateStore.SafeDirectory(root);
                    foreach (var entry in archive.Entries) {
                        string relative = UpdateRules.EntryPath(entry.FullName); if (relative == null) continue;
                        string target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ZIP escaped staging.");
                        UpdateStore.SafeDirectory(Path.GetDirectoryName(target));
                        using (var source = entry.Open())
                        using (var output = new FileStream(UpdateStore.IOPath(target), FileMode.CreateNew, FileAccess.Write, FileShare.None)) UpdateNetwork.Copy(source, output, entry.Length, CancellationToken.None, null, 0);
                    }
                    if (UpdateStore.ReadSmall(Path.Combine(root, "VERSION"), 64).Trim() != release.Version) throw new InvalidDataException("Packaged version mismatch.");
                    return root;
                }
            }
        }
    }
    internal static class UpdateSelfTests {
        internal static void Run(Action<bool, string> check) {
            check(UpdateRules.Compare("1.10.0", "1.9.9") > 0, "Numeric update comparison");
            check(UpdateRules.Compare("2.0.0", "2.0.0") == 0, "Equal release is not newer");
            check(UpdateRules.Compare("0.9.0", "1.0.0") < 0, "Downgrade comparison");
            foreach (string invalid in new[] { "01.2.3", "1.2", "1.2.3-rc.1", "1.2.3\n", "../1.2.3", "9999999999.0.0", "1.2.3.4" })
                check(!UpdateRules.VersionValid(invalid), "Reject invalid update version: " + invalid);
            check(UpdateRules.ReadSummary("### Summary\n- Fixed downloads.\n- Improved startup.\n\n### Details\nFull notes.").Length == 2, "Extract the release summary only");
            foreach (string invalid in new[] { "", "### Summary\n- <img src=x>", "### Summary\n- [Click](https://example.com)", "### Summary\n- " + new string('x', 101), "### Summary\n- One\n- Two\n- Three\n- Four", "### Summary\n- One\n### Summary\n- Two" })
                check(UpdateRules.ReadSummary(invalid).Length == 0, "Invalid release notes remain plain/unavailable");
            foreach (string name in new[] { "firefox-ghost-ui/../Install.ps1", "firefox-ghost-ui/scripts/../../bad.ps1", "firefox-ghost-ui/scripts\\bad.ps1", "firefox-ghost-ui/Install.ps1:stream", "firefox-ghost-ui/docs/CON.md", "firefox-ghost-ui/docs/a./x.md", "foreign/Install.ps1", "firefox-ghost-ui/.hidden.ps1", "firefox-ghost-ui/scripts/bad.exe" }) {
                bool rejected = false; try { UpdateRules.EntryPath(name); } catch (InvalidDataException) { rejected = true; }
                check(rejected, "Reject unsafe package path: " + name);
            }
            check(UpdateRules.EntryPath("firefox-ghost-ui/scripts/GhostUI.psm1") == "scripts/GhostUI.psm1", "Allow expected package layout");
            check(!UpdateRules.DownloadLocation(new Uri("http://release-assets.githubusercontent.com/a"), "1.2.0", true), "HTTPS required for downloads");
            check(!UpdateRules.DownloadLocation(new Uri("https://release-assets.githubusercontent.com.attacker.invalid/a"), "1.2.0", true), "Reject lookalike asset host");
            check(!UpdateRules.DownloadLocation(new Uri("https://github.com/foreign/repo/releases/download/v1.2.0/a.zip"), "1.2.0", false), "Reject foreign package repository");
            check(UpdateRules.DownloadLocation(new Uri(UpdateRules.AssetUrl("1.2.0")), "1.2.0", false), "Allow canonical package URL");
            check(UpdateRules.DownloadLocation(new Uri("https://release-assets.githubusercontent.com/asset?signature=test"), "1.2.0", true), "Allow HTTPS signed asset redirect");
            string digest = new string('a', 64);
            string json = "{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v1.2.0\",\"body\":\"### Summary\\n- Fixed downloads.\",\"assets\":[{\"name\":\"firefox-ghost-ui-1.2.0-windows.zip\",\"size\":42,\"state\":\"uploaded\",\"digest\":\"sha256:" + digest + "\",\"browser_download_url\":\"" + UpdateRules.AssetUrl("1.2.0") + "\"}]}";
            var parsed = UpdateRules.ParseRelease(json);
            check(UpdateRules.Valid(parsed) && parsed.Summary.Length == 1, "Parse actual GitHub array/asset schema");
            foreach (string invalid in new[] { json.Replace("\"draft\":false", "\"draft\":true"), json.Replace("\"prerelease\":false", "\"prerelease\":true"), json.Replace("v1.2.0", "v1.2.0-rc.1"), json.Replace("\"size\":42", "\"size\":999999999"), json.Replace("DekrovDev", "foreign") }) {
                bool rejected = false; try { UpdateRules.ParseRelease(invalid); } catch (InvalidDataException) { rejected = true; }
                check(rejected, "Reject invalid or non-stable release metadata");
            }
            var manualOnly = UpdateRules.ParseRelease(json.Replace("sha256:" + digest, ""));
            check(UpdateRules.MetadataValid(manualOnly) && !UpdateRules.Valid(manualOnly), "Missing digest retains manual download only");
            string fixtureRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update-fixtures-" + Guid.NewGuid().ToString("N"));
            while (fixtureRoot.Length < 280) fixtureRoot = Path.Combine(fixtureRoot, "long-cache-path");
            var fixture = new UpdateStore(fixtureRoot);
            var state = new UpdateState { Release = parsed, DismissedSession = "123:456", DismissedVersion = parsed.Version, Disabled = true };
            fixture.Write(state); var restored = fixture.Read();
            check(restored.Disabled && restored.DismissedSession == state.DismissedSession && restored.Release.Summary[0] == parsed.Summary[0], "Persist dismissal/summary/settings without install approval");
            check(!File.ReadAllText(UpdateStore.IOPath(Path.Combine(fixture.Root, "updates.json"))).Contains("consent"), "No persisted installation approval");
            UpdateRules.RememberDismissal(restored, "222:333", parsed.Version);
            fixture.Write(restored); restored = fixture.Read();
            check(UpdateRules.IsDismissed(restored, "123:456", parsed.Version) && UpdateRules.IsDismissed(restored, "222:333", parsed.Version), "Preserve legacy and second-process dismissal across restart");
            check(!UpdateRules.IsDismissed(restored, "222:333", "9.9.9"), "A different release is not dismissed by an old version");
            for (int index = 1; index < 100; index++) UpdateRules.RememberDismissal(restored, index + ":999", parsed.Version);
            check(restored.DismissedSessions.Count <= 64 && UpdateRules.IsDismissed(restored, "99:999", parsed.Version), "Bound retained process-session dismissals");
            var longRunning = new UpdateState();
            UpdateRules.RememberDismissal(longRunning, "1:111", parsed.Version);
            for (int index = 2; index < 100; index++) {
                string current = index + ":999";
                UpdateRules.RememberDismissal(longRunning, current, parsed.Version, key => key == "1:111" || key == current);
            }
            check(UpdateRules.IsDismissed(longRunning, "1:111", parsed.Version), "Ended-session churn cannot evict a live process reminder");
            string origin = "moz-extension://11111111-2222-3333-4444-555555555555/";
            check(UpdateRules.VisibleBounds(new Rectangle(0, 0, 1100, 820), new[] { new Rectangle(0, 0, 1024, 728) }) == new Rectangle(0, 0, 1024, 728), "Update controls use the visible Firefox area above the taskbar");
            check(UpdateRules.VisibleBounds(new Rectangle(-600, 50, 1100, 820), new[] { new Rectangle(0, 0, 1024, 728) }) == new Rectangle(0, 50, 500, 678), "Partially offscreen Firefox has bounded update-control placement");
            check(UpdateRules.StartPage("about:newtab", new string[0]) == "about:newtab", "Recognize ordinary Firefox new tabs without integrations");
            check(UpdateRules.StartPage("about:home", new string[0]) == "about:home", "Recognize ordinary Firefox home");
            foreach (string url in new[] { "about:blank", "about:privatebrowsing", "about:newtab#fake", "https://example.invalid/about:newtab", origin + "settings.html", origin + "index.html?fake=1", origin.Replace("555555555555", "666666666666") })
                check(UpdateRules.StartPage(url, new[] { origin }) == null, "Never automatically trust an unrelated document: " + url);
            check(UpdateRules.StartPage(origin + "index.html", new[] { origin }) == origin, "Recognize a registered optional start page");
            string profile = Path.Combine(fixture.Root, "ordinary-profile");
            Action<string, string> write = delegate(string path, string body) {
                UpdateStore.SafeDirectory(Path.GetDirectoryName(path)); File.WriteAllText(UpdateStore.IOPath(path), body);
            };
            write(Path.Combine(profile, "prefs.js"), "// synthetic ordinary Firefox profile");
            string installedPath = Path.Combine(Path.GetDirectoryName(fixture.Root), "handles", "installed.json");
            write(installedPath, UpdateRules.Json().Serialize(new { profiles = new[] { profile } }));
            string profileState = Path.Combine(profile, "chrome", "ghost-ui", "state.json");
            write(profileState, "{\"version\":\"1.2.1\"}");
            check(fixture.Profiles().Length == 1 && fixture.Profiles()[0].Origin == null, "Legacy ordinary profile remains registered without any stylesheet");
            write(Path.Combine(profile, "chrome", "ghost-ui", "userContent.css"), "/* unrelated CSS origin: " + origin + " */");
            check(fixture.Profiles()[0].Origin == null, "Stylesheet text cannot register a start-page origin");
            write(profileState, UpdateRules.Json().Serialize(new { version = "1.2.1", bonjourrOrigin = origin }));
            check(fixture.Profiles()[0].Origin == origin, "Explicit optional metadata supplies origin independently of CSS");
            write(profileState, "{\"version\":\"1.2.1\",\"bonjourrOrigin\":{\"invalid\":true}}");
            check(fixture.Profiles().Length == 1 && fixture.Profiles()[0].Origin == null, "Malformed optional metadata cannot hide an installed profile");
            write(profileState, "{\"version\":\"1.2.1\"}");
            string mapping = UpdateRules.Json().Serialize(new Dictionary<string, string> { { UpdateRules.BonjourrId, "11111111-2222-3333-4444-555555555555" } });
            string pref = "user_pref(\"extensions.webextensions.uuids\", " + UpdateRules.Json().Serialize(mapping) + ");\r\n";
            write(Path.Combine(profile, "prefs.js"), pref);
            check(fixture.Profiles()[0].Origin == origin, "Legacy Bonjourr metadata migrates from the exact UUID preference");
            write(Path.Combine(profile, "prefs.js"), pref + pref);
            check(fixture.Profiles().Length == 1 && fixture.Profiles()[0].Origin == null, "Ambiguous legacy preference disables only integration");
            write(Path.Combine(profile, "prefs.js"), "user_pref(\"extensions.webextensions.uuids\", \"not-json\");\n");
            check(fixture.Profiles().Length == 1 && fixture.Profiles()[0].Origin == null, "Corrupt legacy optional preference preserves core registration");
            string inaccessible = Path.Combine(fixture.Root, "inaccessible-profile");
            write(Path.Combine(inaccessible, "prefs.js"), "// disposable inaccessible-profile fixture");
            string deniedState = UpdateStore.IOPath(Path.Combine(inaccessible, "chrome", "ghost-ui", "state.json"));
            write(deniedState, "{\"version\":\"1.2.1\"}");
            write(installedPath, UpdateRules.Json().Serialize(new { profiles = new[] { profile, inaccessible } }));
            var originalAccess = File.GetAccessControl(deniedState);
            try {
                var deniedAccess = File.GetAccessControl(deniedState);
                deniedAccess.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    System.Security.Principal.WindowsIdentity.GetCurrent().User, System.Security.AccessControl.FileSystemRights.ReadData,
                    System.Security.AccessControl.AccessControlType.Deny));
                File.SetAccessControl(deniedState, deniedAccess);
                bool readDenied = false;
                try { UpdateStore.ReadSmall(deniedState, 65536); } catch (UnauthorizedAccessException) { readDenied = true; }
                check(readDenied, "Synthetic profile metadata denial was actually enforced");
                var available = fixture.Profiles();
                check(available.Length == 1 && available[0].Path == profile, "An inaccessible profile cannot hide a healthy profile");
            } finally { File.SetAccessControl(deniedState, originalAccess); }
            byte[] zip;
            using (var memory = new MemoryStream()) {
                using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true)) {
                    foreach (string name in new[] { "Install.ps1", "VERSION", "scripts/GhostUI.psm1", "scripts/Handles.psm1", "scripts/Build-Handles.ps1", "scripts/Select-Profile.ps1" }) {
                        using (var writer = new StreamWriter(archive.CreateEntry("firefox-ghost-ui/" + name).Open())) writer.Write(name == "VERSION" ? "1.2.0" : "# inert test fixture; never execute");
                    }
                }
                zip = memory.ToArray();
            }
            var package = new UpdateRelease { Version = "1.2.0", Size = zip.Length, Summary = parsed.Summary };
            using (var sha = SHA256.Create()) package.Digest = BitConverter.ToString(sha.ComputeHash(zip)).Replace("-", "").ToLowerInvariant();
            File.WriteAllBytes(fixture.Package(package), zip);
            check(fixture.Ready(package), "Verify cached ZIP hash and size");
            string extracted = UpdateInstaller.Extract(fixture, package);
            check(File.ReadAllText(UpdateStore.IOPath(Path.Combine(extracted, "VERSION"))) == "1.2.0", "Extract verified ZIP without executing it");
            zip[0] ^= 1; File.WriteAllBytes(fixture.Package(package), zip);
            check(!fixture.Ready(package), "Tampered package cannot be offered as ready");
            bool corruptRejected = false;
            try { UpdateInstaller.Extract(fixture, package); } catch (InvalidDataException) { corruptRejected = true; }
            check(corruptRejected, "Reverify package before extracting for installation");
            zip[0] ^= 1;
            string retry = UpdateStore.IOPath(Path.Combine(fixture.Root, "verified-retry.part"));
            File.WriteAllBytes(retry, zip); fixture.PromotePackage(retry, package);
            check(fixture.Ready(package), "Verified retry replaces a damaged cache");
            check(Directory.GetFiles(UpdateStore.IOPath(fixture.Root), "*.invalid").Length == 1, "Damaged cache is preserved separately");
            zip[0] ^= 1; File.WriteAllBytes(retry, zip);
            bool badRetryRejected = false;
            try { fixture.PromotePackage(retry, package); } catch (InvalidDataException) { badRetryRejected = true; }
            check(badRetryRejected && fixture.Ready(package), "An invalid retry cannot replace a verified cache");
        }
    }
}
