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
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput { internal int X, Y; internal uint Data, Flags, Time; internal UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct KeyInput { internal ushort Key, Scan; internal uint Flags, Time; internal UIntPtr Extra; }
        [StructLayout(LayoutKind.Explicit)] private struct InputData {
            [FieldOffset(0)] internal MouseInput Mouse;
            [FieldOffset(0)] internal KeyInput Keyboard;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Input { internal uint Type; internal InputData Data; }
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
        private static void Send(Input[] inputs) {
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) != inputs.Length)
                throw new InvalidOperationException("Native fixture input was not delivered.");
        }
        private static Input Key(ushort key, bool up) { return new Input { Type = 1, Data = new InputData { Keyboard = new KeyInput { Key = key, Flags = up ? 2u : 0u } } }; }
        private static void Shortcut() {
            try { Send(new[] { Key(0x11, false), Key(0x12, false), Key(0x77, false), Key(0x77, true), Key(0x12, true), Key(0x11, true) }); }
            finally { Send(new[] { Key(0x77, true), Key(0x12, true), Key(0x11, true) }); }
        }
        private static void Click(Control control, UpdateCard card) {
            Point center = control.PointToScreen(new Point(control.Width / 2, control.Height / 2));
            Cursor.Position = center;
            if (Cursor.Position != center || Native.GetAncestor(Native.WindowFromPoint(center), 2) != card.Handle)
                throw new InvalidDataException("Native control click is clipped or covered.");
            Send(new[] { new Input { Data = new InputData { Mouse = new MouseInput { Flags = 2 } } }, new Input { Data = new InputData { Mouse = new MouseInput { Flags = 4 } } } });
        }
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
                int openRequests = 0;
                int mouseActivations = 0;
                string activationControl = null;
                long mouseDownForeground = 0, clickForeground = 0;
                bool shortcutClaimed = false;
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
                                if (!card.Visible || !card.IsCompact) throw new InvalidDataException("Expected the real compact entry.");
                                var center = new Point(card.Left + card.Width / 2, card.Top + card.Height / 2);
                                Cursor.Position = center;
                                if (Cursor.Position != center) throw new InvalidDataException("Native cursor was clipped away from the launcher.");
                                if (Native.GetAncestor(Native.WindowFromPoint(Cursor.Position), 2) != card.Handle) throw new InvalidDataException("Launcher is covered by another window.");
                                Send(new[] { new Input { Data = new InputData { Mouse = new MouseInput { Flags = 2 } } }, new Input { Data = new InputData { Mouse = new MouseInput { Flags = 4 } } } });
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
                    string result = UpdateRules.Json().Serialize(new {
                        actor = Process.GetCurrentProcess().Id, ack = acknowledged, visible = card.Visible && !card.IsCompact,
                        launcherVisible = card.Visible && card.IsCompact, autoChecks = card.AutoCheck.Checked, checkEnabled = card.Check.Enabled,
                        title = card.TitleLabel.Text, summary = card.SummaryLabel.Text, primary = card.Primary.Text,
                        manual = card.Manual.Text, manualEnabled = card.Manual.Enabled,
                        ready = (bool)Field(controller, "ready"), confirming = card.Confirm.Visible,
                        primaryEnabled = card.Primary.Enabled, selectedProfile = card.Profiles.SelectedIndex,
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
                        mouseDownForeground = mouseDownForeground, clickForeground = clickForeground,
                        mouseActivations = mouseActivations, activationControl = activationControl,
                        launcherEnabled = ((Button)Field(card, "launcher")).Enabled,
                        launcherActualVisible = ((Button)Field(card, "launcher")).Visible,
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
