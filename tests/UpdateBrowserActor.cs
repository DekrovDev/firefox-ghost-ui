// Unshipped fixture: production controller and card, with an explicit dummy store.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Windows.Forms;

namespace GhostUI {
    internal static class UpdateBrowserActor {
        private static bool clickDelivered;
        private static string clickDetail;
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput { internal int X, Y; internal uint Data, Flags, Time; internal UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct KeyInput { internal ushort Key, Scan; internal uint Flags, Time; internal UIntPtr Extra; }
        [StructLayout(LayoutKind.Explicit)] private struct InputData {
            [FieldOffset(0)] internal MouseInput Mouse;
            [FieldOffset(0)] internal KeyInput Keyboard;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Input { internal uint Type; internal InputData Data; }
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
        private static void Send(Input[] inputs) {
            int size = Marshal.SizeOf(typeof(Input));
            uint sent = SendInput((uint)inputs.Length, inputs, size);
            int error = Marshal.GetLastWin32Error();
            if (sent != inputs.Length)
                throw new InvalidOperationException("Native fixture input was not delivered: count=" + inputs.Length + ", sent=" + sent + ", size=" + size + ", error=" + error);
        }
        private static Input Key(ushort key, bool up) { return new Input { Type = 1, Data = new InputData { Keyboard = new KeyInput { Key = key, Flags = up ? 2u : 0u } } }; }
        private static void Shortcut() {
            try { Send(new[] { Key(0x11, false), Key(0x12, false), Key(0x77, false), Key(0x77, true), Key(0x12, true), Key(0x11, true) }); }
            finally { Send(new[] { Key(0x77, true), Key(0x12, true), Key(0x11, true) }); }
        }
        private static void Click(Control control, UpdateCard card) {
            clickDelivered = false;
            if (!card.Visible || !control.Visible || !control.Enabled) { clickDetail = "Native target is not currently visible/enabled."; return; }
            Point center = control.PointToScreen(new Point(control.Width / 2, control.Height / 2));
            Cursor.Position = center;
            IntPtr hit = Native.WindowFromPoint(center);
            if (Cursor.Position != center || hit != control.Handle) {
                var blocker = Control.FromHandle(hit);
                var className = new System.Text.StringBuilder(128); Native.GetClassName(hit, className, className.Capacity);
                uint blockerPid; Native.GetWindowThreadProcessId(hit, out blockerPid);
                string blockerProcess = "unavailable";
                try { using (var process = Process.GetProcessById((int)blockerPid)) blockerProcess = process.ProcessName; } catch (ArgumentException) { }
                clickDetail = "Native control click is clipped or covered: target=" + control.GetType().Name + " bounds=" + control.Bounds + " visible=" + control.Visible + " cardVisible=" + card.Visible + " compact=" + card.IsCompact + " center=" + center + " hit=" + hit + " expected=" + control.Handle + " hitClass=" + className + " managed=" + (blocker == null ? "native" : blocker.GetType().Name) + " blockerProcess=" + blockerProcess + " blockerBounds=" + Native.Bounds(hit);
                return;
            }
            Send(new[] { new Input { Data = new InputData { Mouse = new MouseInput { Flags = 2 } } } });
            // The actor is on the UI thread. Let that thread process activation
            // and MouseDown before releasing, as an external human/test driver would.
            var release = new System.Windows.Forms.Timer { Interval = 100 };
            release.Tick += delegate {
                release.Stop(); release.Dispose();
                Send(new[] { new Input { Data = new InputData { Mouse = new MouseInput { Flags = 4 } } } });
            };
            release.Start();
            clickDelivered = true; clickDetail = "Native press delivered to the exact control HWND.";
        }
        private static object Field(object item, string name) {
            return item.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(item);
        }
        private static int SummaryHeight(Label summary) {
            int width = Math.Max(1, summary.ClientSize.Width - summary.Padding.Horizontal);
            using (var graphics = summary.CreateGraphics()) {
                Size measured = TextRenderer.MeasureText(graphics, summary.Text, summary.Font,
                    new Size(width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
                return Math.Max(measured.Height + summary.Padding.Vertical,
                    summary.GetPreferredSize(new Size(summary.Width, 0)).Height);
            }
        }
        private static string[] SummaryOverlaps(UpdateCard card) {
            var overlaps = new System.Collections.Generic.List<string>();
            Rectangle summary = card.SummaryLabel.RectangleToScreen(card.SummaryLabel.ClientRectangle);
            foreach (Control control in new Control[] { card.Profiles, card.StatusLabel, card.Primary, card.Later,
                card.Cancel, card.Confirm, card.Check, card.Manual, card.Changes, card.AutoCheck, card.Destination }) {
                if (control.Visible && summary.IntersectsWith(control.RectangleToScreen(control.ClientRectangle)))
                    overlaps.Add(control.AccessibleName ?? control.Text ?? control.GetType().Name);
            }
            return overlaps.ToArray();
        }
        private static string ControlLabel(Control control) {
            string label = control.AccessibleName ?? control.Name;
            if (String.IsNullOrEmpty(label)) label = control.GetType().Name + ":" + control.Text;
            return label.Length > 70 ? label.Substring(0, 70) : label;
        }
        private static string[] LayoutIssues(Panel body, Control[] controls) {
            var issues = new System.Collections.Generic.List<string>();
            Point offset = body.AutoScrollPosition;
            for (int index = 0; index < controls.Length; index++) {
                Control control = controls[index]; Rectangle bounds = control.Bounds;
                int logicalLeft = bounds.Left - offset.X;
                if (bounds.Width <= 0 || bounds.Height <= 0) issues.Add("Empty control: " + ControlLabel(control));
                if (logicalLeft < 0 || logicalLeft + bounds.Width > body.ClientSize.Width)
                    issues.Add("Outside body width: " + ControlLabel(control));
                for (int next = index + 1; next < controls.Length; next++)
                    if (control.Parent == controls[next].Parent && bounds.IntersectsWith(controls[next].Bounds))
                        issues.Add("Overlap: " + ControlLabel(control) + " / " + ControlLabel(controls[next]));
            }
            return issues.ToArray();
        }
        private static string[] VisibleLayoutIssues(UpdateCard card) {
            var body = (Panel)Field(card, "body");
            var controls = new System.Collections.Generic.List<Control>();
            foreach (Control control in body.Controls) if (control.Visible) controls.Add(control);
            return LayoutIssues(body, controls.ToArray());
        }
        private static object[] NarrowLayouts(IntPtr owner) {
            // Native scrolling requires a shown control. Use only an owned,
            // nonactivating fixture inside the already verified Firefox window.
            var results = new System.Collections.Generic.List<object>();
            IntPtr foreground = Native.GetForegroundWindow();
            if (!Native.IsFirefox(owner)) throw new InvalidDataException("Narrow fixture requires its verified Firefox owner.");
            Rectangle ownerBounds = Native.Bounds(owner);
            var constraint = new Rectangle(0, 0, 300, Math.Min(700, ownerBounds.Height + 84));
            using (var fixture = new UpdateCard()) {
                IntPtr fixtureHandle = fixture.Handle;
                var body = (Panel)Field(fixture, "body"); IntPtr bodyHandle = body.Handle;
                fixture.Profiles.Items.Add("Disposable profile"); fixture.Profiles.SelectedIndex = 0;
                fixture.VersionLabel.Text = "Version 9.9.9 - installed 1.3.0";
                string wide = ""; for (int index = 0; index < 24; index++) wide += "WWW ";
                fixture.SummaryLabel.Text = "\u2022 " + wide + "ONE!\n\u2022 " + wide + "TWO!\n\u2022 " + wide + "END!";
                fixture.Destination.Text = "Install to the selected disposable profile.";
                fixture.Location = new Point(ownerBounds.Left + 8, ownerBounds.Top + 8);
                fixture.Show(new WindowOwner(owner));
                string[] states = { "available", "ready", "confirm", "approved", "downloading", "current", "no-release" };
                foreach (string state in states) {
                    bool action = state == "available" || state == "ready";
                    bool confirm = state == "confirm", approved = state == "approved", downloading = state == "downloading";
                    bool current = state == "current";
                    fixture.TitleLabel.Text = current ? "Up to date" : "Ghost UI updates";
                    fixture.Primary.Text = state == "ready" ? "Install update" : "Download";
                    fixture.StatusLabel.Text = "Downloading update... 00%";
                    fixture.Present(action, confirm, approved, downloading, current);
                    if (!fixture.FitToWindow(constraint)) throw new InvalidDataException("Minimum supported synthetic bounds were rejected.");
                    fixture.PerformLayout(); body.PerformLayout();
                    var expected = new System.Collections.Generic.List<Control>(new Control[] {
                        (Control)Field(fixture, "brand"), (Control)Field(fixture, "close"), (Control)Field(fixture, "statusIcon"),
                        (Control)Field(fixture, "profileCaption"), (Control)Field(fixture, "summaryCaption"),
                        fixture.TitleLabel, fixture.VersionLabel, fixture.Profiles, fixture.SummaryLabel,
                        fixture.StatusLabel, fixture.Manual, fixture.Changes, fixture.AutoCheck, fixture.Check });
                    if (action) expected.Add(fixture.Primary);
                    if (confirm) expected.Add(fixture.Confirm);
                    if (confirm || approved || downloading) expected.Add(fixture.Cancel); else expected.Add(fixture.Later);
                    if (confirm || approved) expected.Add(fixture.Destination);
                    var issues = new System.Collections.Generic.List<string>(LayoutIssues(body, expected.ToArray()));
                    if (fixture.SummaryLabel.ClientSize.Height < SummaryHeight(fixture.SummaryLabel)) issues.Add("Summary allocation is shorter than rendered text.");
                    body.AutoScrollPosition = new Point(0, 80);
                    Point scroll = body.AutoScrollPosition;
                    if (scroll.Y >= 0) issues.Add("Synthetic long content did not actually scroll.");
                    for (int repeat = 0; repeat < 5; repeat++) {
                        fixture.StatusLabel.Text = "Downloading update... " + (repeat + 10) + "%";
                        fixture.Present(action, confirm, approved, downloading, current);
                        fixture.FitToWindow(constraint);
                        fixture.PerformLayout(); body.PerformLayout();
                        if (body.AutoScrollPosition != scroll) issues.Add("Repeated fit/progress update reset scroll position.");
                    }
                    issues.AddRange(LayoutIssues(body, expected.ToArray()));
                    results.Add(new { state = state, width = fixture.ClientSize.Width, bodyWidth = body.ClientSize.Width,
                        controls = expected.Count, issues = issues.ToArray(), scrollY = body.AutoScrollPosition.Y,
                        foregroundPreserved = Native.GetForegroundWindow() == foreground });
                    body.AutoScrollPosition = Point.Empty;
                }
            }
            return results.ToArray();
        }
        private static void CaptureCard(string root, UpdateCard card, string name) {
            // Render only this fixture's own native control tree. Never capture
            // the desktop, Firefox document or any pixels behind the overlay.
            string[] names = { "launcher", "available", "ready", "confirm", "approved", "cancelled", "current", "small-window", "no-release" };
            if (Array.IndexOf(names, name) < 0 || !card.Visible || card.Width < 1 || card.Height < 1 || card.Width > 2048 || card.Height > 2048)
                throw new InvalidDataException("Invalid own-card capture request.");
            string folder = Path.Combine(root, "card-previews");
            Directory.CreateDirectory(folder);
            using (var bitmap = new Bitmap(card.Width, card.Height)) {
                card.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(folder, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                if (name == "launcher" || name == "available" || name == "current") {
                    // Normal-content QA uses the actual versioned notes and a
                    // separate real native card. The nine raw images above keep
                    // the stress data; no Firefox/desktop pixels are captured.
                    string workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
                    if (String.IsNullOrEmpty(workspace) || !Path.IsPathRooted(workspace)) throw new InvalidDataException("Missing source workspace for versioned preview notes.");
                    string version = UpdateStore.ReadSmall(Path.Combine(workspace, "VERSION"), 64).Trim();
                    if (!UpdateRules.VersionValid(version)) throw new InvalidDataException("Invalid preview version.");
                    string changelog = UpdateStore.ReadSmall(Path.Combine(workspace, "CHANGELOG.md"), 65536).Replace("\r\n", "\n");
                    var sections = System.Text.RegularExpressions.Regex.Matches(changelog,
                        @"(?ms)^## \[" + System.Text.RegularExpressions.Regex.Escape(version) + @"\] - [0-9]{4}-[0-9]{2}-[0-9]{2}\n(.*?)(?=^## |\z)");
                    if (sections.Count != 1) throw new InvalidDataException("Preview requires exactly one matching versioned changelog section.");
                    string[] notes = UpdateRules.ReadSummary(sections[0].Groups[1].Value);
                    if (notes.Length == 0) throw new InvalidDataException("Versioned preview summary is unavailable.");
                    IntPtr owner = Native.GetWindow(card.Handle, 4), foreground = Native.GetForegroundWindow();
                    if (!Native.IsFirefox(owner) || Native.IsIconic(owner)) throw new InvalidDataException("Preview requires the source card's visible Firefox owner.");
                    Rectangle ownerBounds = Native.Bounds(owner);
                    using (var demo = new UpdateCard()) {
                        bool current = name == "current";
                        string installed = current ? version : "1.2.1";
                        var profile = new UpdateProfile { Path = @"C:\Demo\default-release", Version = installed };
                        demo.Profiles.Items.Add(profile); demo.Profiles.SelectedIndex = 0;
                        demo.Profiles.AccessibleDescription = profile.Path;
                        demo.TitleLabel.Text = current ? "Up to date" : "Update available";
                        demo.VersionLabel.Text = current ? "Version " + installed + " installed" : installed + " \u2192 " + version;
                        demo.SummaryLabel.Text = String.Join("\n", Array.ConvertAll(notes, text => "\u2022 " + text));
                        demo.StatusLabel.Text = current ? "Installation always needs your permission." : "Downloading does not install the update.";
                        demo.Primary.Text = "Download"; demo.Primary.Enabled = !current;
                        demo.Check.Enabled = true; demo.Manual.Enabled = true; demo.AutoCheck.Checked = true;
                        demo.Destination.Text = "Install version " + version + " to:\r\n" + profile.Path;
                        demo.Present(!current, false, false, false, current);
                        demo.SetCompact(name == "launcher");
                        demo.Location = new Point(ownerBounds.Left + 16, ownerBounds.Top + 16);
                        if (!demo.FitToWindow(ownerBounds)) throw new InvalidDataException("Firefox owner is too small for the versioned preview.");
                        demo.Location = new Point(ownerBounds.Right - demo.Width - 16, ownerBounds.Bottom - demo.Height - 16);
                        if (!ownerBounds.Contains(demo.Bounds)) throw new InvalidDataException("Versioned preview escaped its Firefox owner.");
                        try {
                            demo.Show(new WindowOwner(owner));
                            if (Native.GetForegroundWindow() != foreground) throw new InvalidOperationException("Versioned preview stole foreground focus.");
                            if (!demo.FitToWindow(ownerBounds)) throw new InvalidDataException("Visible versioned preview no longer fits.");
                            demo.Location = new Point(ownerBounds.Right - demo.Width - 16, ownerBounds.Bottom - demo.Height - 16);
                            if (!ownerBounds.Contains(demo.Bounds) || demo.Width > 2048 || demo.Height > 2048) throw new InvalidDataException("Invalid visible preview geometry.");
                            using (var normal = new Bitmap(demo.Width, demo.Height)) {
                                demo.DrawToBitmap(normal, new Rectangle(Point.Empty, normal.Size));
                                // Synthetic contrast contexts only. The OS applies
                                // uniform form opacity; no wallpaper is inspected.
                                const int pad = 24, heading = 32;
                                using (var contexts = new Bitmap((normal.Width + pad) * 3 + pad, normal.Height + pad * 2 + heading))
                                using (var graphics = Graphics.FromImage(contexts))
                                using (var alpha = new System.Drawing.Imaging.ImageAttributes()) {
                                    graphics.Clear(Color.FromArgb(70, 70, 70));
                                    var matrix = new System.Drawing.Imaging.ColorMatrix(); matrix.Matrix33 = (float)demo.Opacity;
                                    alpha.SetColorMatrix(matrix);
                                    string[] labels = { "Light", "Dark", "Pattern" };
                                    for (int index = 0; index < 3; index++) {
                                        var area = new Rectangle(pad + index * (normal.Width + pad), pad + heading, normal.Width, normal.Height);
                                        using (var brush = new SolidBrush(index == 0 ? Color.White : Color.FromArgb(12, 12, 12))) graphics.FillRectangle(brush, area);
                                        if (index == 2) {
                                            var previousClip = graphics.Clip;
                                            graphics.SetClip(area);
                                            for (int y = area.Top; y < area.Bottom; y += 28)
                                                for (int x = area.Left; x < area.Right; x += 28)
                                                    using (var brush = new SolidBrush(((x - area.Left) / 28 + (y - area.Top) / 28) % 2 == 0 ? Color.FromArgb(242, 209, 123) : Color.FromArgb(93, 32, 72)))
                                                        graphics.FillRectangle(brush, x, y, 28, 28);
                                            graphics.Clip = previousClip; previousClip.Dispose();
                                        }
                                        TextRenderer.DrawText(graphics, labels[index], demo.Font, new Point(area.Left, pad), Color.White);
                                        var saved = graphics.Save();
                                        if (demo.Region != null) {
                                            using (var clip = demo.Region.Clone()) {
                                                clip.Translate(area.X, area.Y); graphics.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Replace);
                                            }
                                        }
                                        graphics.DrawImage(normal, area, 0, 0, normal.Width, normal.Height, GraphicsUnit.Pixel, alpha);
                                        graphics.Restore(saved);
                                    }
                                    contexts.Save(Path.Combine(folder, name + "-contexts.png"), System.Drawing.Imaging.ImageFormat.Png);
                                }
                            }
                            if (Native.GetForegroundWindow() != foreground) throw new InvalidOperationException("Versioned preview changed foreground focus while rendering.");
                        } finally { demo.Hide(); }
                    }
                    if (Native.GetForegroundWindow() != foreground) throw new InvalidOperationException("Closing the versioned preview changed foreground focus.");
                }
            }
        }
        [STAThread] private static int Main(string[] args) {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
                Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" || args.Length != 2) return 2;
            if (args[0] == "--apply-update") return UpdateInstaller.Run(args[1]);
            string root = Path.GetFullPath(args[0]); int browser = Int32.Parse(args[1]);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) {
                try { Send(new[] { new Input { Data = new InputData { Mouse = new MouseInput { Flags = 4 } } } }); } catch { }
                try { File.WriteAllText(Path.Combine(root, "actor-failure.txt"), e.Exception.ToString()); }
                finally { Environment.Exit(1); }
            };
            Native.EnablePhysicalCoordinates();
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var main = new Controller(null, null, true))
            using (var controller = new UpdateController(new UpdateStore(Path.Combine(root, "updates"))))
            using (var other = new Form { Text = "Unrelated application fixture", Width = 400, Height = 260 })
            using (var timer = new System.Windows.Forms.Timer()) {
                var card = (UpdateCard)Field(controller, "card");
                int acknowledged = 0;
                int openRequests = 0;
                int profileSelections = 0, profileCommits = 0;
                card.Profiles.SelectedIndexChanged += delegate { profileSelections++; };
                card.Profiles.SelectionChangeCommitted += delegate { profileCommits++; };
                int mouseActivations = 0;
                string activationControl = null;
                long mouseDownForeground = 0, clickForeground = 0;
                bool shortcutClaimed = false;
                object[] narrowLayouts = new object[0];
                card.UserInteraction += delegate {
                    mouseActivations++;
                    activationControl = card.ActiveControl == null ? "none" : card.ActiveControl.GetType().Name + ":" + card.ActiveControl.Visible;
                };
                ((Button)Field(card, "launcher")).MouseDown += delegate { mouseDownForeground = Native.GetForegroundWindow().ToInt64(); };
                card.OpenRequested += delegate { openRequests++; clickForeground = Native.GetForegroundWindow().ToInt64(); };
                timer.Interval = 200;
                timer.Tick += delegate {
                    string commandPath = Path.Combine(root, "command.json");
                    if (File.Exists(commandPath)) {
                        var command = UpdateRules.Json().Deserialize<System.Collections.Generic.Dictionary<string, object>>(UpdateStore.ReadSmall(commandPath, 4096));
                        File.Delete(commandPath); acknowledged = Convert.ToInt32(command["id"]);
                        switch ((string)command["action"]) {
                            case "narrow-layouts": narrowLayouts = NarrowLayouts((IntPtr)Field(controller, "owner")); break;
                            case "capture-card": CaptureCard(root, card, command.ContainsKey("name") ? command["name"] as string : null); break;
                            case "scroll-checks": ((ScrollableControl)card.AutoCheck.Parent).ScrollControlIntoView(card.AutoCheck); break;
                            case "click-profile": Click(card.Profiles, card); break;
                            case "open-profile-keyboard":
                                if (Native.GetForegroundWindow() != card.Handle || !card.Profiles.Enabled) throw new InvalidDataException("Profile keyboard test requires the enabled own foreground card.");
                                if (!card.Profiles.Focus()) throw new InvalidDataException("Native profile picker did not accept focus.");
                                Send(new[] { Key(0x73, false), Key(0x73, true) });
                                break;
                            case "choose-second-keyboard":
                                if (!card.Profiles.Focused || card.Profiles.Items.Count < 2) throw new InvalidDataException("Native profile picker is not focused or has no second destination.");
                                Send(new[] { Key(0x24, false), Key(0x24, true), Key(0x28, false), Key(0x28, true), Key(0x0d, false), Key(0x0d, true) });
                                break;
                            case "shortcut": Shortcut(); break;
                            case "click-auto-check": Click(card.AutoCheck, card); break;
                            case "claim-shortcut":
                                if (Native.GetForegroundWindow() != other.Handle) throw new InvalidDataException("Expected the unrelated fixture window in foreground.");
                                shortcutClaimed = Native.RegisterHotKey(other.Handle, 88, 0x4003, 0x77);
                                if (!shortcutClaimed) throw new InvalidOperationException("Another app could not claim the released update shortcut.");
                                Native.UnregisterHotKey(other.Handle, 88);
                                break;
                            case "clear-dismissals":
                                var updateState = (UpdateState)Field(controller, "state");
                                updateState.DismissedSession = null; updateState.DismissedVersion = null; updateState.DismissedSessions = null;
                                break;
                            case "escape": Send(new[] { Key(0x1b, false), Key(0x1b, true) }); break;
                            case "click-launcher":
                                Click((Button)Field(card, "launcher"), card);
                                break;
                            case "focus-pid":
                                int requested = Convert.ToInt32(command["pid"]);
                                other.Hide();
                                Native.EnumWindows(delegate(IntPtr window, IntPtr unused) {
                                    uint pid; Native.GetWindowThreadProcessId(window, out pid);
                                    if (pid == requested && Native.IsFirefox(window)) { Native.SetForegroundWindow(window); return false; }
                                    return true;
                                }, IntPtr.Zero);
                                break;
                            case "minimize": Native.ShowWindow((IntPtr)Field(controller, "owner"), 6); break;
                            case "restore": Native.ShowWindow((IntPtr)Field(controller, "owner"), 9); break;
                            case "open-settings": card.OpenSettings(); break;
                            case "enable-checks": card.AutoCheck.Checked = true; break;
                            case "select-second": card.Profiles.SelectedIndex = 1; break;
                            case "profile-current":
                                var profiles = (UpdateProfile[])Field(controller, "profiles");
                                if (profiles.Length != 1) throw new InvalidDataException("Expected one disposable profile.");
                                File.WriteAllText(Path.Combine(profiles[0].Path, "chrome", "ghost-ui", "state.json"), "{\"version\":\"9.9.9\"}");
                                break;
                            case "later": card.Later.PerformClick(); break;
                            case "install": card.Primary.PerformClick(); break;
                            case "approve": card.Confirm.PerformClick(); break;
                            case "cancel": card.Cancel.PerformClick(); break;
                            case "handoff-failure":
                                typeof(UpdateController).GetMethod("LaunchInstall", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller, null);
                                break;
                            case "other": other.Show(); other.Activate(); break;
                            case "focus":
                                other.Hide();
                                Native.EnumWindows(delegate(IntPtr window, IntPtr unused) {
                                    uint pid; Native.GetWindowThreadProcessId(window, out pid);
                                    if (pid == browser && Native.IsFirefox(window)) { Native.SetForegroundWindow(window); return false; }
                                    return true;
                                }, IntPtr.Zero);
                                break;
                            case "quit": Application.Exit(); break;
                            default: throw new InvalidDataException("Unknown fixture action.");
                        }
                    }
                    var bounds = card.Bounds;
                    IntPtr owner = (IntPtr)Field(controller, "owner");
                    IntPtr foreground = Native.GetForegroundWindow();
                    IntPtr browserWindow = IntPtr.Zero;
                    Native.EnumWindows(delegate(IntPtr window, IntPtr unused) {
                        if (browserWindow != IntPtr.Zero) return true;
                        uint pid; Native.GetWindowThreadProcessId(window, out pid);
                        if ((int)pid == browser && Native.IsFirefox(window) && Native.Bounds(window).Width > 0) { browserWindow = window; }
                        return true;
                    }, IntPtr.Zero);
                    var browserBounds = owner == IntPtr.Zero
                        ? (browserWindow == IntPtr.Zero ? System.Drawing.Rectangle.Empty : Native.Bounds(browserWindow))
                        : Native.Bounds(owner);
                    IntPtr detectedWindow = (IntPtr)Field(controller, "detectedWindow");
                    DateTime detectedAt = (DateTime)Field(controller, "detectedAt");
                    var className = new System.Text.StringBuilder(64);
                    Native.GetClassName(foreground, className, className.Capacity);
                    var foregroundControl = Control.FromHandle(foreground);
                    var forms = new System.Collections.Generic.List<string>();
                    foreach (Form form in Application.OpenForms)
                        forms.Add(form.GetType().Name + ":" + (form.IsHandleCreated ? form.Handle.ToInt64() : 0) + ":" + form.Visible);
                    var launcher = (Button)Field(card, "launcher");
                    int summaryHeight = SummaryHeight(card.SummaryLabel);
                    Rectangle summaryBounds = card.SummaryLabel.Bounds;
                    bool summaryFixedParentClips = card.SummaryLabel.Parent != null && card.SummaryLabel.Parent != card &&
                        !(card.SummaryLabel.Parent is ScrollableControl && ((ScrollableControl)card.SummaryLabel.Parent).AutoScroll) &&
                        !card.SummaryLabel.Parent.ClientRectangle.Contains(summaryBounds);
                    string result = UpdateRules.Json().Serialize(new {
                        actor = Process.GetCurrentProcess().Id, ack = acknowledged, visible = card.Visible && !card.IsCompact,
                        launcherVisible = card.Visible && card.IsCompact, autoChecks = card.AutoCheck.Checked, checkEnabled = card.Check.Enabled,
                        title = card.TitleLabel.Text, summary = card.SummaryLabel.Text, primary = card.Primary.Text,
                        summaryVisible = card.SummaryLabel.Visible, summaryRequiredHeight = summaryHeight,
                        summaryAllocatedHeight = card.SummaryLabel.ClientSize.Height, summaryWidth = card.SummaryLabel.ClientSize.Width,
                        summaryOverlaps = SummaryOverlaps(card), summaryFixedParentClips = summaryFixedParentClips,
                        summaryLineCount = card.SummaryLabel.Text.Split('\n').Length,
                        visibleLayoutIssues = VisibleLayoutIssues(card), narrowLayouts = narrowLayouts,
                        primaryVisible = card.Primary.Visible, manualVisible = card.Manual.Visible,
                        dailyVisible = card.AutoCheck.Visible, checkVisible = card.Check.Visible,
                        cancelVisible = card.Cancel.Visible, destinationVisible = card.Destination.Visible,
                        manual = card.Manual.Text, manualEnabled = card.Manual.Enabled,
                        ready = (bool)Field(controller, "ready"), confirming = card.Confirm.Visible,
                        primaryEnabled = card.Primary.Enabled, selectedProfile = card.Profiles.SelectedIndex,
                        profileSelections = profileSelections, profileCommits = profileCommits,
                        bodyScrollY = ((Panel)Field(card, "body")).AutoScrollPosition.Y,
                        profileDroppedDown = card.Profiles.DroppedDown, profileEnabled = card.Profiles.Enabled,
                        profileRole = card.Profiles.AccessibilityObject.Role.ToString(), profileAccessibleName = card.Profiles.AccessibleName,
                        scrollsVertically = ((Panel)Field(card, "body")).VerticalScroll.Visible,
                        destination = card.Destination.Text,
                        launching = (bool)Field(controller, "launching"), status = card.StatusLabel.Text,
                        approved = (DateTime)Field(controller, "consentUntil") > DateTime.UtcNow,
                        origin = Field(controller, "detectedOrigin"), profiles = card.Profiles.Items.Count,
                        inside = browserBounds.Contains(bounds),
                        onScreen = Array.Exists(Screen.AllScreens, screen => screen.WorkingArea.Contains(bounds)),
                        screenWidth = Screen.PrimaryScreen.WorkingArea.Width, screenHeight = Screen.PrimaryScreen.WorkingArea.Height,
                        cardX = card.Left, cardY = card.Top, cardWidth = card.Width, cardHeight = card.Height,
                        cursorX = Cursor.Position.X, cursorY = Cursor.Position.Y,
                        browserWindowFound = browserWindow != IntPtr.Zero,
                        browserWindowIsForeground = browserWindow != IntPtr.Zero && foreground == browserWindow,
                        browserWindowVisible = browserWindow != IntPtr.Zero && Native.IsWindowVisible(browserWindow),
                        foregroundClass = className.ToString(),
                        ownerIsForeground = owner != IntPtr.Zero && foreground == owner,
                        cardIsForeground = foreground == card.Handle || Native.GetAncestor(foreground, 2) == card.Handle,
                        owner = owner.ToInt64(), dpi = card.DeviceDpi,
                        nativeOwner = Native.GetWindow(card.Handle, 4).ToInt64(), cardHandle = card.Handle.ToInt64(), foregroundHandle = foreground.ToInt64(),
                        shortcutRegistered = (bool)Field(card, "shortcutRegistered"),
                        openRequests = openRequests, shortcutClaimed = shortcutClaimed,
                        manualOpen = (bool)Field(controller, "manualOpen"),
                        clickDelivered = clickDelivered, clickDetail = clickDetail,
                        mouseDownForeground = mouseDownForeground, clickForeground = clickForeground,
                        mouseActivations = mouseActivations, activationControl = activationControl,
                        launcherEnabled = ((Button)Field(card, "launcher")).Enabled,
                        launcherActualVisible = ((Button)Field(card, "launcher")).Visible,
                        launcherBounds = launcher.Bounds.ToString(), launcherCreated = launcher.IsHandleCreated,
                        launcherHandle = launcher.IsHandleCreated ? launcher.Handle.ToInt64() : 0,
                        bodyVisible = ((Panel)Field(card, "body")).Visible,
                        foregroundControl = foregroundControl == null ? "native" : foregroundControl.GetType().Name,
                        foregroundIsOther = other.IsHandleCreated && foreground == other.Handle,
                        openForms = forms.ToArray(),
                        detectedWindowMatch = detectedWindow == owner && owner != IntPtr.Zero,
                        freshnessMs = detectedAt == DateTime.MinValue ? -1 : (int)(DateTime.UtcNow - detectedAt).TotalMilliseconds,
                        browserWidth = browserBounds.Width, browserHeight = browserBounds.Height,
                        zoomed = (owner != IntPtr.Zero && Native.IsZoomed(owner)) || (browserWindow != IntPtr.Zero && Native.IsZoomed(browserWindow)),
                        probing = (bool)Field(controller, "probing"),
                        refreshPending = (bool)Field(controller, "refreshPending"),
                        profileFreshnessMs = (DateTime)Field(controller, "profileCheckedAt") == DateTime.MinValue ? -1 : (DateTime.UtcNow - (DateTime)Field(controller, "profileCheckedAt")).TotalMilliseconds,
                        foregroundBounds = Native.Bounds(foreground).ToString(), ownerValid = Native.IsWindow(owner),
                        cardDisposed = card.IsDisposed, cardDisposing = card.Disposing,
                        session = Field(controller, "session")
                        , uiFailure = File.Exists(Path.Combine(root, "ui-error.txt")) ? UpdateStore.ReadSmall(Path.Combine(root, "ui-error.txt"), 16384) : null
                    });
                    string temp = Path.Combine(root, "status.tmp"), destination = Path.Combine(root, "status.json");
                    File.WriteAllText(temp, result);
                    if (File.Exists(destination)) {
                        try { File.Replace(temp, destination, null); }
                        catch (IOException error) {
                            int code = error.HResult & 0xffff;
                            if (code != 32 && code != 33) throw;
                            // Python can briefly hold the last status without
                            // FileShare.Delete. Publish the current ack/state on
                            // the next timer tick; never sleep or replay input.
                        }
                    } else File.Move(temp, destination);
                };
                timer.Start(); Application.Run(main); timer.Stop();
            }
            return 0;
        }
    }
}
