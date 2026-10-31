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
        public string Result;
        public bool Disabled;
    }
    internal sealed class UpdateProfile {
        internal string Path, Version, Origin;
        public override string ToString() { return System.IO.Path.GetFileName(Path) + " (" + Version + ")"; }
    }
    public static class UpdateRules {
        public const string Repository = "https://github.com/DekrovDev/firefox-ghost-ui";
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
                        string version = state.ContainsKey("version") ? state["version"] as string : null;
                        if (!UpdateRules.VersionValid(version) || !File.Exists(IOPath(Path.Combine(path, "prefs.js")))) continue;
                        string css = ReadSmall(Path.Combine(path, "chrome", "ghost-ui", "userContent.css"), 1024 * 1024);
                        Match origin = Regex.Match(css, @"moz-extension://[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}/");
                        if (origin.Success) result.Add(new UpdateProfile { Path = path, Version = version, Origin = origin.Value });
                    } catch (IOException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
                }
                return result.GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
            } catch (IOException) { return new UpdateProfile[0]; } catch (ArgumentException) { return new UpdateProfile[0]; } catch (InvalidOperationException) { return new UpdateProfile[0]; }
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
                            if (!UpdateStore.Verify(output, release)) throw new InvalidDataException("Package verification failed.");
                        }
                        string target = store.Package(release);
                        if (File.Exists(target)) {
                            if (!store.Ready(release)) throw new IOException("An invalid cached package was preserved. Use Download manually.");
                            File.Delete(temporary);
                        } else File.Move(temporary, target);
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
                return origins.FirstOrDefault(o => url == o + "index.html" || url == o + "index.html#" || url == o);
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
    internal sealed class UpdateCard : Form {
        internal event Action DismissRequested;
        internal readonly Label TitleLabel, VersionLabel, SummaryLabel, StatusLabel;
        internal readonly Button Primary, Later, Cancel, Confirm, Check, Manual;
        internal readonly LinkLabel Changes;
        internal readonly ComboBox Profiles;
        internal readonly CheckBox AutoCheck;
        protected override bool ShowWithoutActivation { get { return true; } }
        internal UpdateCard() {
            Text = "Ghost UI updates"; AccessibleName = Text; ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(448, 472);
            BackColor = Color.FromArgb(34, 32, 30); ForeColor = Color.FromArgb(244, 239, 230);
            Font = new Font("Segoe UI", 10); Opacity = .97; Padding = new Padding(22);
            var brand = LabelAt("GHOST UI", 22, 16, 330, 20); brand.Font = new Font(Font.FontFamily, 9, FontStyle.Bold);
            var close = ButtonAt("\u00d7", 400, 9, 28, 28, false); close.AccessibleName = "Remind me next Firefox session";
            TitleLabel = LabelAt("Update available", 22, 48, 402, 32); TitleLabel.Font = new Font(Font.FontFamily, 17);
            VersionLabel = LabelAt("", 22, 85, 402, 26);
            var caption = LabelAt("What's new", 22, 119, 402, 22); caption.Font = new Font(Font.FontFamily, 10, FontStyle.Bold);
            SummaryLabel = LabelAt("", 22, 146, 402, 117);
            Profiles = new ComboBox { Left = 22, Top = 268, Width = 402, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Profile to update" };
            Controls.Add(Profiles);
            StatusLabel = LabelAt("Installation always needs your permission.", 22, 303, 402, 43);
            Primary = ButtonAt("Download", 22, 353, 222, 38, true);
            Later = ButtonAt("Later", 256, 353, 168, 38, false);
            Confirm = ButtonAt("Confirm install", 22, 353, 222, 38, true); Confirm.Visible = false;
            Cancel = ButtonAt("Cancel", 256, 353, 168, 38, false); Cancel.Visible = false;
            Manual = ButtonAt("Download manually", 22, 401, 222, 28, false);
            Changes = LinkAt("All changes", 256, 403, 168);
            AutoCheck = new CheckBox { Text = "Check daily", Left = 22, Top = 436, Width = 190, Height = 24, Checked = true, AccessibleName = "Check for updates daily" };
            Controls.Add(AutoCheck);
            Check = ButtonAt("Check now", 256, 432, 168, 28, false);
            close.Click += delegate { if (DismissRequested != null) DismissRequested(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; if (DismissRequested != null) DismissRequested(); } };
        }
        protected override bool ProcessCmdKey(ref Message message, Keys key) {
            if (key == Keys.Escape) { if (DismissRequested != null) DismissRequested(); return true; }
            return base.ProcessCmdKey(ref message, key);
        }
        private Label LabelAt(string text, int x, int y, int width, int height) {
            var label = new Label { Text = text, Left = x, Top = y, Width = width, Height = height, UseMnemonic = false };
            Controls.Add(label); return label;
        }
        private Button ButtonAt(string text, int x, int y, int width, int height, bool primary) {
            var button = new Button { Text = text, Left = x, Top = y, Width = width, Height = height, FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Color.FromArgb(244, 235, 220) : BackColor, ForeColor = primary ? Color.FromArgb(35, 32, 29) : ForeColor };
            button.FlatAppearance.BorderColor = Color.FromArgb(133, 123, 109); Controls.Add(button); return button;
        }
        private LinkLabel LinkAt(string text, int x, int y, int width) {
            var link = new LinkLabel { Text = text, Left = x, Top = y, Width = width, Height = 24, LinkColor = ForeColor, ActiveLinkColor = Color.White, VisitedLinkColor = ForeColor };
            Controls.Add(link); return link;
        }
        protected override void OnResize(EventArgs e) {
            base.OnResize(e);
            if (Width < 1 || Height < 1) return;
            using (var path = new System.Drawing.Drawing2D.GraphicsPath()) {
                int r = 28; path.AddArc(0, 0, r, r, 180, 90); path.AddArc(Width - r, 0, r, r, 270, 90);
                path.AddArc(Width - r, Height - r, r, r, 0, 90); path.AddArc(0, Height - r, r, r, 90, 90); path.CloseFigure();
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
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private bool busy, ready, disposed, probing, refreshPending, confirming, launching;
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
                    pageGeneration++; detectedOrigin = null; card.Hide(); nextProbe = DateTime.MinValue;
                });
            };
            titleHook = SetWinEventHook(0x800c, 0x800c, IntPtr.Zero, windowEvent, 0, 0, 2); // Window title changes, skip own process.
            selectionHook = SetWinEventHook(0x8006, 0x8009, IntPtr.Zero, windowEvent, 0, 0, 2); // Tab/selection changes invalidate a pending probe.
            card.AutoCheck.Checked = !state.Disabled;
            card.AutoCheck.CheckedChanged += delegate { state.Disabled = !card.AutoCheck.Checked; Save(); };
            card.Primary.Click += delegate { if (ready) Confirm(); else Download(); };
            card.Later.Click += delegate { Dismiss(); };
            card.DismissRequested += Dismiss;
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
                    string selected = Selected == null ? null : Selected.Path;
                    profiles = found; card.Profiles.Items.Clear(); card.Profiles.Items.AddRange(found);
                    card.Profiles.SelectedIndex = Math.Max(-1, Array.FindIndex(found, p => p.Path == selected));
                    if (card.Profiles.SelectedIndex < 0 && found.Length > 0) card.Profiles.SelectedIndex = 0;
                    Render();
                });
            });
        }
        private void VerifyCache() {
            var release = state.Release; int stamp = ++generation; ready = false;
            if (release == null) { Render(); return; }
            Task.Run(delegate { bool verified = store.Ready(release); Post(delegate { if (stamp == generation) { ready = verified; Render(); } }); });
        }
        private void Check(bool manual) {
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
            confirming = false; consentUntil = DateTime.MinValue;
            state.DismissedSession = session; state.DismissedVersion = state.Release == null ? "none" : state.Release.Version;
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
            card.TitleLabel.Text = release != null && selected != null && UpdateRules.Compare(release.Version, selected.Version) <= 0 ? "This profile is up to date" : ready ? "Ready to install" : "Update available";
            card.VersionLabel.Text = release == null ? "Check for Ghost UI updates" : "Version " + release.Version + (selected == null ? "" : " - installed " + selected.Version);
            card.SummaryLabel.Text = release == null || release.Summary == null || release.Summary.Length == 0 ? "Release notes unavailable. See All changes." : String.Join("\n", release.Summary.Select(s => "\u2022 " + s));
            card.StatusLabel.Text = release != null && !UpdateRules.Valid(release) ? "Automatic download unavailable. Choose Download manually." : status ?? (ready ? "Downloaded and verified. Installation needs your permission." : "Installation always needs your permission.");
            card.Primary.Text = ready ? "Install update" : "Download";
            card.Primary.Enabled = !busy && UpdateRules.Valid(release) && selected != null && UpdateRules.Compare(release.Version, selected.Version) > 0;
            card.Primary.Visible = !confirming && !approved && download == null;
            card.Later.Visible = !confirming && !approved && download == null;
            card.Confirm.Visible = confirming;
            card.Cancel.Visible = confirming || approved || download != null;
            card.Profiles.Enabled = !busy && !confirming && !approved;
            card.Check.Enabled = !busy && !approved && !confirming;
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
                if (now - profileCheckedAt > TimeSpan.FromSeconds(10) || state.Release == null ||
                    !profiles.Any(p => UpdateRules.Compare(state.Release.Version, p.Version) > 0)) { card.Hide(); return; }
                IntPtr foreground = Native.GetForegroundWindow();
                IntPtr firefox = foreground == card.Handle || Native.GetAncestor(foreground, 2) == card.Handle ? owner : foreground;
                if (!Native.IsFirefox(firefox) || Native.IsIconic(firefox) || Fullscreen(firefox)) { card.Hide(); return; }
                uint pid; Native.GetWindowThreadProcessId(firefox, out pid);
                using (var process = Process.GetProcessById((int)pid)) session = pid + ":" + process.StartTime.ToUniversalTime().Ticks;
                string version = state.Release == null ? "none" : state.Release.Version;
                if (state.DismissedSession == session && state.DismissedVersion == version) { card.Hide(); return; }
                if (now >= nextProbe && !probing) Probe(firefox);
                if (detectedWindow != firefox || detectedOrigin == null || now - detectedAt > TimeSpan.FromMilliseconds(800)) { card.Hide(); return; }
                Rectangle bounds = Native.Bounds(firefox);
                int margin = Math.Max(16, card.Width / 24);
                if (bounds.Width < card.Width + 100 || bounds.Height < card.Height + 120) { card.Hide(); return; }
                card.Location = new Point(bounds.Right - card.Width - margin, bounds.Bottom - card.Height - margin);
                if (!card.Visible || owner != firefox) {
                    card.Hide(); owner = firefox;
                    if (consentUntil == DateTime.MinValue) {
                        var profile = profiles.FirstOrDefault(p => p.Origin == detectedOrigin);
                        if (profile != null) card.Profiles.SelectedItem = profile;
                    }
                    card.Show(new WindowOwner(firefox));
                }
            } catch { card.Hide(); }
        }
        private void Probe(IntPtr window) {
            probing = true; nextProbe = DateTime.UtcNow.AddMilliseconds(350);
            int page = pageGeneration;
            string[] origins = profiles.Select(p => p.Origin).Distinct().ToArray();
            var thread = new Thread(delegate() {
                string origin = null; DateTime requestedAt = DateTime.UtcNow;
                try { origin = StartPageProbe.Origin(window, origins); } catch { }
                Post(delegate { probing = false; if (page != pageGeneration) return; detectedWindow = window; detectedOrigin = origin; detectedAt = requestedAt; });
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
        private void LaunchInstall() {
            if (state.Release == null || state.Release.Version != consentVersion || !profiles.Any(p => p.Path == consentProfile)) { Cancel(); return; }
            launching = true; card.Hide();
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
        }
    }
}
