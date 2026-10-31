// Unshipped fixture: production controller and card, with an explicit dummy store.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace GhostUI {
    internal static class UpdateBrowserActor {
        private static object Field(object item, string name) {
            return item.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(item);
        }
        [STAThread] private static int Main(string[] args) {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
                Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" || args.Length != 2) return 2;
            if (args[0] == "--apply-update") return UpdateInstaller.Run(args[1]);
            Native.EnablePhysicalCoordinates();
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string root = Path.GetFullPath(args[0]); int browser = Int32.Parse(args[1]);
            using (var controller = new UpdateController(new UpdateStore(Path.Combine(root, "updates"))))
            using (var other = new Form { Text = "Unrelated application fixture", Width = 400, Height = 260 })
            using (var timer = new System.Windows.Forms.Timer()) {
                var card = (UpdateCard)Field(controller, "card");
                int acknowledged = 0;
                timer.Interval = 200;
                timer.Tick += delegate {
                    string commandPath = Path.Combine(root, "command.json");
                    if (File.Exists(commandPath)) {
                        var command = UpdateRules.Json().Deserialize<System.Collections.Generic.Dictionary<string, object>>(UpdateStore.ReadSmall(commandPath, 4096));
                        File.Delete(commandPath); acknowledged = Convert.ToInt32(command["id"]);
                        switch ((string)command["action"]) {
                            case "open-settings": card.OpenSettings(); break;
                            case "enable-checks": card.AutoCheck.Checked = true; break;
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
                    string result = UpdateRules.Json().Serialize(new {
                        actor = Process.GetCurrentProcess().Id, ack = acknowledged, visible = card.Visible && !card.IsCompact,
                        launcherVisible = card.Visible && card.IsCompact, autoChecks = card.AutoCheck.Checked, checkEnabled = card.Check.Enabled,
                        title = card.TitleLabel.Text, summary = card.SummaryLabel.Text, primary = card.Primary.Text,
                        manual = card.Manual.Text, manualEnabled = card.Manual.Enabled,
                        ready = (bool)Field(controller, "ready"), confirming = card.Confirm.Visible,
                        launching = (bool)Field(controller, "launching"), status = card.StatusLabel.Text,
                        approved = (DateTime)Field(controller, "consentUntil") > DateTime.UtcNow,
                        origin = Field(controller, "detectedOrigin"), profiles = card.Profiles.Items.Count,
                        inside = browserBounds.Contains(bounds),
                        browserWindowFound = browserWindow != IntPtr.Zero,
                        browserWindowIsForeground = browserWindow != IntPtr.Zero && foreground == browserWindow,
                        browserWindowVisible = browserWindow != IntPtr.Zero && Native.IsWindowVisible(browserWindow),
                        foregroundClass = className.ToString(),
                        ownerIsForeground = owner != IntPtr.Zero && foreground == owner,
                        detectedWindowMatch = detectedWindow == owner && owner != IntPtr.Zero,
                        freshnessMs = detectedAt == DateTime.MinValue ? -1 : (int)(DateTime.UtcNow - detectedAt).TotalMilliseconds,
                        browserWidth = browserBounds.Width, browserHeight = browserBounds.Height,
                        zoomed = (owner != IntPtr.Zero && Native.IsZoomed(owner)) || (browserWindow != IntPtr.Zero && Native.IsZoomed(browserWindow)),
                        probing = (bool)Field(controller, "probing"),
                        session = Field(controller, "session")
                    });
                    string temp = Path.Combine(root, "status.tmp"), destination = Path.Combine(root, "status.json");
                    File.WriteAllText(temp, result);
                    if (File.Exists(destination)) File.Replace(temp, destination, null); else File.Move(temp, destination);
                };
                timer.Start(); Application.Run(); timer.Stop();
            }
            return 0;
        }
    }
}
