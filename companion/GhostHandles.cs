// Firefox Ghost UI: adaptive, non-activating window grips. No browser injection.
// Targets .NET Framework 4.8; build with scripts/Build-Handles.ps1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace GhostUI {
    public enum Edge { Left, Right, Top, Bottom }
    public sealed class Placement {
        public Edge Edge;
        public Rectangle Bounds;
        public Placement(Edge edge, Rectangle bounds) { Edge = edge; Bounds = bounds; }
    }

    public static class Motion {
        public static double OpacityAt(double from, double goal, double elapsedMs) {
            double duration = goal > from ? 210 : 180;
            double t = Math.Max(0, Math.Min(1, elapsedMs / duration));
            return from + (goal - from) * t * t * (3 - 2 * t);
        }
        public static double NextOpacity(double current, double goal, bool reduced) {
            if (reduced) return goal;
            return goal > current ? Math.Min(goal, current + .17) : Math.Max(goal, current - .12);
        }
    }

    // Physical desktop coordinates throughout. Negative monitor coordinates are valid.
    public static class Geometry {
        public static List<Placement> Grips(Rectangle window, Rectangle[] monitors, Point pointer, float scale) {
            var result = new List<Placement>();
            int thickness = Math.Max(10, (int)Math.Round(14 * scale));
            int length = Math.Max(40, (int)Math.Round(128 * scale));
            int gap = Math.Max(1, (int)Math.Round(2 * scale));
            int corner = Math.Max(12, (int)Math.Round(16 * scale));
            foreach (Edge edge in Enum.GetValues(typeof(Edge))) {
                bool vertical = edge == Edge.Left || edge == Edge.Right;
                int cross = edge == Edge.Left ? window.Left - gap - thickness :
                    edge == Edge.Right ? window.Right + gap :
                    edge == Edge.Top ? window.Top - gap - thickness : window.Bottom + gap;
                var candidates = new List<Rectangle>();
                foreach (Rectangle screen in monitors) {
                    // Never put even part of a handle in a monitor gap or over the website.
                    if (vertical ? cross < screen.Left || cross + thickness > screen.Right :
                        cross < screen.Top || cross + thickness > screen.Bottom) continue;
                    int start = Math.Max(vertical ? window.Top + corner : window.Left + corner,
                                         vertical ? screen.Top : screen.Left);
                    int end = Math.Min(vertical ? window.Bottom - corner : window.Right - corner,
                                       vertical ? screen.Bottom : screen.Right);
                    if (end - start < thickness * 2) continue;
                    int size = Math.Min(length, end - start);
                    int middle = vertical ? pointer.Y : pointer.X;
                    int along = Math.Max(start, Math.Min(end - size, middle - size / 2));
                    candidates.Add(vertical ? new Rectangle(cross, along, thickness, size) :
                        new Rectangle(along, cross, size, thickness));
                }
                if (candidates.Count > 0) {
                    Rectangle nearest = candidates.OrderBy(r => Distance(r, pointer)).First();
                    result.Add(new Placement(edge, nearest));
                }
            }
            return result;
        }
        public static double Distance(Rectangle r, Point p) {
            int dx = Math.Max(r.Left - p.X, Math.Max(0, p.X - r.Right));
            int dy = Math.Max(r.Top - p.Y, Math.Max(0, p.Y - r.Bottom));
            return Math.Sqrt((double)dx * dx + (double)dy * dy);
        }
        public static Rectangle Recover(Rectangle window, Rectangle work, int minimum) {
            int width = Math.Max(minimum, Math.Min(window.Width, work.Width));
            int height = Math.Max(minimum, Math.Min(window.Height, work.Height));
            width = Math.Min(width, work.Width); height = Math.Min(height, work.Height);
            return new Rectangle(work.Left + (work.Width - width) / 2,
                work.Top + (work.Height - height) / 2, width, height);
        }
        public static long VisibleArea(Rectangle window, Rectangle[] monitors) {
            long area = 0;
            foreach (Rectangle monitor in monitors) {
                Rectangle intersection = Rectangle.Intersect(window, monitor);
                if (!intersection.IsEmpty) area += (long)intersection.Width * intersection.Height;
            }
            return area;
        }
        public static bool NeedsRescue(Rectangle window, Rectangle frame, Rectangle[] monitors, float scale) {
            long visible = VisibleArea(window, monitors);
            if (window.Width <= 0 || window.Height <= 0 || visible >= (long)window.Width * window.Height) return false;
            return visible == 0 || Grips(frame, monitors, Point.Empty, scale).Count == 0;
        }
    }

    internal sealed class RescueReadiness {
        private Rectangle bounds;
        private DateTime stableSince;
        internal bool Ready(Rectangle current, bool lost, bool buttonDown, DateTime now) {
            if (current != bounds || buttonDown) { bounds = current; stableSince = now; }
            return !buttonDown && (lost || (now - stableSince).TotalMilliseconds >= 500);
        }
    }

    internal static class Native {
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("shcore.dll")] private static extern int SetProcessDpiAwareness(int awareness);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")] internal static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
        internal static void EnablePhysicalCoordinates() {
            // csc-built .NET Framework executables may ignore the WinForms
            // app.config DPI opt-in without a target-framework project attribute.
            // Set awareness before creating any HWND or reading any screen data.
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch (EntryPointNotFoundException) { }
            try { if (SetProcessDpiAwareness(2) == 0) return; } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
            SetProcessDPIAware();
        }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct BLEND {
            public byte Operation, Flags, ConstantAlpha, Format;
        }
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool UpdateLayeredWindow(IntPtr h, IntPtr screenDC,
            ref Point destination, ref Size size, IntPtr sourceDC, ref Point source, int colorKey, ref BLEND blend, int flags);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr h);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr h, IntPtr obj);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] internal struct RECT {
            public int Left, Top, Right, Bottom;
            public Rectangle Rectangle { get { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } }
        }
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point p);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr h, uint command);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int count);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr h, StringBuilder s, int count);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr h, int command);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, IntPtr param);
        internal delegate bool EnumProc(IntPtr h, IntPtr param);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out RECT r, int size);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out int value, int size);
        internal static Rectangle Bounds(IntPtr h) {
            RECT r;
            if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))) == 0) return r.Rectangle;
            return GetWindowRect(h, out r) ? r.Rectangle : Rectangle.Empty;
        }
        internal static bool Cloaked(IntPtr h) {
            int value; return DwmGetWindowAttribute(h, 14, out value, 4) == 0 && value != 0;
        }
        internal static bool IsFirefox(IntPtr h) {
            if (h == IntPtr.Zero || !IsWindow(h) || !IsWindowVisible(h)) return false;
            var name = new StringBuilder(128); GetClassName(h, name, name.Capacity);
            if (name.ToString() != "MozillaWindowClass") return false;
            // Reject other apps before the compositor RPC; this function is also
            // used while enumerating background windows for lost-window recovery.
            if (Cloaked(h)) return false;
            uint pid; GetWindowThreadProcessId(h, out pid);
            return ExecutableName(pid).Equals("firefox.exe", StringComparison.OrdinalIgnoreCase);
        }
        internal static string ExecutableName(uint pid) {
            // .NET Framework Process.ProcessName can enumerate system processes
            // on every access. Query only this HWND's process instead.
            IntPtr process = OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return String.Empty;
            try {
                var path = new StringBuilder(1024); int count = path.Capacity;
                return QueryFullProcessImageName(process, 0, path, ref count) ? Path.GetFileName(path.ToString()) : String.Empty;
            } finally { CloseHandle(process); }
        }
        internal static bool IsDesktop(IntPtr h) {
            var name = new StringBuilder(128); GetClassName(h, name, name.Capacity);
            return name.ToString() == "Progman" || name.ToString() == "WorkerW" || name.ToString() == "Shell_TrayWnd";
        }
        internal static IntPtr Pack(Point p) { return new IntPtr(unchecked((p.Y << 16) | (p.X & 65535))); }
        internal static void BeginMove(IntPtr target, Point pointer) {
            ReleaseCapture();
            SetForegroundWindow(target);
            // Let Windows own capture, Escape cancellation, snapping, and cross-monitor movement.
            PostMessage(target, 0x0112, new IntPtr(0xF012), Pack(pointer));
        }
    }

    internal sealed class WindowOwner : IWin32Window {
        public IntPtr Handle { get; private set; }
        internal WindowOwner(IntPtr h) { Handle = h; }
    }
    internal sealed class RecoveryFixture : Form {
        protected override bool ShowWithoutActivation { get { return true; } }
        internal RecoveryFixture() {
            ShowInTaskbar = false; Opacity = 0; AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
        }
    }

    // Draw into a per-pixel-alpha surface. A binary WinForms Region clips off
    // anti-aliasing and produces the jagged, box-like caps in the first version.
    public static class Capsule {
        internal static long RenderCount;
        private static GraphicsPath Shape(RectangleF r) {
            var path = new GraphicsPath();
            float d = Math.Min(r.Width, r.Height);
            if (r.Width >= r.Height) {
                path.AddArc(r.Left, r.Top, d, d, 90, 180);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
            } else {
                path.AddArc(r.Left, r.Top, d, d, 180, 180);
                path.AddArc(r.Left, r.Bottom - d, d, d, 0, 180);
            }
            path.CloseFigure(); return path;
        }
        public static Bitmap Render(Size size, bool hover, string label = null) {
            RenderCount++;
            const int sampling = 4;
            using (var large = new Bitmap(size.Width * sampling, size.Height * sampling, PixelFormat.Format32bppPArgb)) {
                using (Graphics g = Graphics.FromImage(large)) {
                    g.Clear(Color.Transparent);
                    g.ScaleTransform(sampling, sampling);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    var rect = new RectangleF(.75f, .75f, size.Width - 1.5f, size.Height - 1.5f);
                    using (var path = Shape(rect))
                    using (var fill = new LinearGradientBrush(rect,
                        Color.FromArgb(242, hover ? 45 : 39, hover ? 45 : 39, hover ? 51 : 45),
                        Color.FromArgb(242, 31, 31, 35), size.Width >= size.Height ? 90f : 0f))
                    using (var border = new Pen(Color.FromArgb(hover ? 66 : 40, Color.White), .8f)) {
                        g.FillPath(fill, path); g.DrawPath(border, path);
                    }
                    if (!String.IsNullOrEmpty(label)) {
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        using (var font = new Font("Segoe UI", size.Height * .40f, FontStyle.Regular, GraphicsUnit.Pixel))
                        using (var ink = new SolidBrush(Color.FromArgb(235, Color.White)))
                        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                            g.DrawString(label, font, ink, rect, format);
                    }
                }
                var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(bitmap)) {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(large, new Rectangle(Point.Empty, size), 0, 0, large.Width, large.Height, GraphicsUnit.Pixel);
                }
                return bitmap;
            }
        }
    }

    internal sealed class Grip : Form {
        internal static long PresentCount;
        internal IntPtr Target;
        internal Edge Edge;
        private readonly Action<Grip> move;
        private readonly Action recover;
        internal bool Hover;
        internal double Reveal = .01;
        internal string Label;
        private Bitmap surface;
        private double fadeFrom, fadeGoal = -1;
        private long fadeStarted;
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams {
            get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000080 | 0x00080000; return p; }
        }
        internal Grip(Action<Grip> move, Action recover) {
            this.move = move; this.recover = recover;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
            BackColor = Color.FromArgb(23, 23, 27); ForeColor = Color.White;
            Cursor = Cursors.SizeAll; AccessibleName = "Ghost UI — move window";
            Text = "Ghost UI grip";
            MouseEnter += delegate { Hover = true; RenderSurface(); Present(); };
            MouseLeave += delegate { Hover = false; RenderSurface(); Present(); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Move window", null, delegate { Native.BeginMove(Target, Cursor.Position); });
            menu.Items.Add("Return to screen", null, delegate { recover(); });
            ContextMenuStrip = menu;
        }
        protected override void OnMouseDown(MouseEventArgs e) {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) move(this);
        }
        internal void Place(Placement placement, IntPtr target) {
            if (!Visible) fadeGoal = -1;
            Edge = placement.Edge; Target = target;
            Bounds = placement.Bounds;
            RenderSurface(); Present();
            if (!Visible) Show(new WindowOwner(target));
            Native.SetWindowPos(Handle, IntPtr.Zero, Left, Top, Width, Height, 0x0010);
        }
        internal void Fade(bool show, bool reduced) {
            double goal = show ? (Hover ? .96 : .87) : 0;
            long now = Stopwatch.GetTimestamp();
            if (goal != fadeGoal || (!Visible && show)) {
                fadeFrom = Reveal; fadeGoal = goal; fadeStarted = now;
            }
            double next = reduced ? goal : Motion.OpacityAt(fadeFrom, goal,
                (now - fadeStarted) * 1000d / Stopwatch.Frequency);
            if (next != Reveal) { Reveal = next; Present(); }
            if (!show && Reveal <= .001) Hide();
        }
        private void RenderSurface() {
            if (surface != null) surface.Dispose();
            surface = Capsule.Render(Size, Hover, Label);
        }
        private void Present() {
            if (surface == null || IsDisposed) return;
            PresentCount++;
            IntPtr dc = Native.CreateCompatibleDC(IntPtr.Zero);
            IntPtr bitmap = surface.GetHbitmap(Color.FromArgb(0));
            IntPtr previous = Native.SelectObject(dc, bitmap);
            try {
                Point destination = Location, origin = Point.Empty; Size size = Size;
                var blend = new Native.BLEND { ConstantAlpha = (byte)Math.Round(Reveal * 255), Format = 1 };
                if (!Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref destination, ref size, dc, ref origin, 0, ref blend, 2))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            } finally {
                Native.SelectObject(dc, previous); Native.DeleteObject(bitmap); Native.DeleteDC(dc);
            }
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override void Dispose(bool disposing) {
            if (disposing && surface != null) { surface.Dispose(); surface = null; }
            base.Dispose(disposing);
        }
    }

    internal sealed class Controller : Form {
        private readonly Timer timer = new Timer();
        private readonly Dictionary<Edge, Grip> grips = new Dictionary<Edge, Grip>();
        private readonly Grip rescue;
        private readonly Dictionary<Edge, DateTime> approached = new Dictionary<Edge, DateTime>();
        private readonly NotifyIcon tray;
        private readonly string diagnostics;
        private readonly Form demo;
        private readonly System.Threading.EventWaitHandle stop;
        private IntPtr target, lastTarget;
        private Rectangle lastBounds;
        private bool moving;
        private bool suspended;
        private volatile bool displaysChanged;
        private readonly EventHandler displayHandler;
        private DateTime lastNear = DateTime.MinValue;
        private DateTime lastReport = DateTime.MinValue;
        private DateTime lastRescueScan = DateTime.MinValue;
        private IntPtr rescueTarget;
        private readonly Dictionary<IntPtr, RescueReadiness> rescueReadiness = new Dictionary<IntPtr, RescueReadiness>();
        private string notice = "";
        private long tickCount;
        private double tickMs, rescueMs;
        internal int Moves;
        internal bool HotkeyMove, HotkeyRecover;
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }
        internal Controller(string diagnostics, Form demo, bool testMode = false) {
            this.diagnostics = diagnostics; this.demo = demo;
            stop = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset,
                "Local\\FirefoxGhostUIHandlesStop" + (testMode ? Guid.NewGuid().ToString("N") : demo != null ? "Demo" : ""));
            displayHandler = delegate { displaysChanged = true; };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += displayHandler;
            Text = "Ghost UI handles controller"; ShowInTaskbar = false;
            foreach (Edge edge in Enum.GetValues(typeof(Edge))) grips.Add(edge, new Grip(BeginMove, Recover));
            rescue = new Grip(delegate(Grip g) { Recover(g.Target); }, delegate { Recover(rescueTarget); }) {
                Label = "↩  Firefox", TopMost = true,
                AccessibleName = "Return Firefox to screen",
                Text = "Ghost UI — return Firefox"
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Move Firefox · Ctrl+Alt+F9", null, delegate { MoveByKeyboard(); });
            menu.Items.Add("Return Firefox to screen · Ctrl+Alt+F10", null, delegate { Recover(); });
            menu.Items.Add("Pause handles", null, delegate(object sender, EventArgs e) {
                suspended = !suspended; ((ToolStripMenuItem)sender).Checked = suspended; if (suspended) HideGrips(true);
            });
            menu.Items.Add("Exit", null, delegate { Application.Exit(); });
            tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "Ghost UI — adaptive window handles", ContextMenuStrip = menu, Visible = !testMode };
            tray.DoubleClick += delegate { Recover(); };
            IntPtr controllerHandle = Handle;
            if (!testMode) {
                HotkeyMove = Native.RegisterHotKey(controllerHandle, 1, 0x4003, 0x78); // Ctrl+Alt+F9, no repeat
                HotkeyRecover = Native.RegisterHotKey(controllerHandle, 2, 0x4003, 0x79); // Ctrl+Alt+F10
            }
            if (!testMode && (!HotkeyMove || !HotkeyRecover)) {
                notice = "Shortcut already registered. Use Ghost UI tray menu.";
                tray.ShowBalloonTip(6000, "Ghost UI", notice, ToolTipIcon.Warning);
            }
            timer.Interval = 30; timer.Tick += delegate { Tick(); }; if (!testMode) timer.Start();
            if (demo != null && !testMode) demo.FormClosed += delegate { Application.Exit(); };
        }
        internal static void CheckNativeRecovery(Action<bool,string> check) {
            // Exercise actual owned alpha windows and native coordinates, with
            // no input injection, no Firefox target, no hotkeys, and no focus change.
            IntPtr foreground = Native.GetForegroundWindow();
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            using (var fixture = new RecoveryFixture()) {
                fixture.Bounds = new Rectangle(work.Right + 600, work.Bottom + 600, 600, 400);
                fixture.Show();
                using (var controller = new Controller(null, fixture, true)) {
                    controller.lastTarget = fixture.Handle;
                    controller.target = IntPtr.Zero; // unrelated app retains focus
                    controller.UpdateRescue();
                    check(controller.rescue.Visible, "Lost unfocused native fixture must show return capsule");
                    check(controller.rescue.Target == fixture.Handle, "Return capsule must bind the exact lost HWND");
                    check(work.Contains(controller.rescue.Bounds), "Return capsule must stay on the physical monitor");
                    check(Native.GetForegroundWindow() == foreground, "Return capsule must not steal foreground focus");
                    controller.ReturnWindow(fixture.Handle, false);
                    check(work.Contains(Native.Bounds(fixture.Handle)), "Native return must put full painted frame onscreen");
                    controller.lastRescueScan = DateTime.MinValue;
                    for (int i=0; i<10; i++) controller.UpdateRescue();
                    check(!controller.rescue.Visible, "Return capsule must disappear once fixture is recovered");
                    check(Native.GetForegroundWindow() == foreground, "Background recovery must preserve foreground focus");
                    fixture.Bounds = new Rectangle(work.Right - 60, work.Top + 100, 600, 400);
                    controller.lastRescueScan = DateTime.MinValue;
                    for (int i=0; i<10; i++) controller.UpdateRescue();
                    check(!controller.rescue.Visible, "Inactive clipped fixture with an available grip must not show return capsule");
                    check(controller.FindTarget() == fixture.Handle, "Inactive clipped fixture must retain its ordinary grip target");
                    fixture.Bounds = new Rectangle(work.Left + 100, work.Top + 100, 320, 240);
                    controller.lastRescueScan = DateTime.MinValue;
                    controller.UpdateRescue();
                    check(!controller.rescue.Visible, "Small unfocused onscreen native fixture must not show return capsule");
                    check(controller.FindTarget() == IntPtr.Zero, "Small onscreen background fixture must not gain grip overlays");
                    controller.suspended = true;
                    controller.UpdateRescue();
                    check(!controller.rescue.Visible, "Pause must also hide the return capsule");
                }
            }
        }
        private bool Accept(IntPtr h) { return demo != null ? h == demo.Handle : Native.IsFirefox(h); }
        private IntPtr FindTarget() {
            IntPtr foreground = Native.GetForegroundWindow();
            if (Accept(foreground)) { lastTarget = foreground; return foreground; }
            if (grips.Values.Any(g => g.Visible && g.Handle == foreground)) return target;
            if (foreground != IntPtr.Zero && !Native.IsDesktop(foreground) && IsFullscreen(foreground)) return IntPtr.Zero;
            // Keep external recovery grips available after a clipped window loses
            // focus. Fully onscreen background windows never gain hover overlays.
            if (lastTarget != IntPtr.Zero && Accept(lastTarget) && !Native.IsIconic(lastTarget) && !Native.IsZoomed(lastTarget) && !IsFullscreen(lastTarget)) {
                Rectangle r = Native.Bounds(lastTarget);
                long visible = Geometry.VisibleArea(r, Screen.AllScreens.Select(s => s.WorkingArea).ToArray());
                if (visible > 0 && visible < (long)r.Width * r.Height) return lastTarget;
            }
            return IntPtr.Zero;
        }
        private IntPtr RecoveryTarget() {
            IntPtr current = FindTarget(); if (current != IntPtr.Zero) return current;
            if (lastTarget != IntPtr.Zero && Accept(lastTarget)) return lastTarget;
            IntPtr found = IntPtr.Zero;
            if (demo != null) return demo.Handle;
            Native.EnumWindows(delegate(IntPtr h, IntPtr p) { if (Accept(h)) { found = h; return false; } return true; }, IntPtr.Zero);
            return found;
        }
        private void HideGrips(bool hideRescue = false) {
            foreach (Grip grip in grips.Values) { grip.Hide(); grip.Reveal = .01; }
            approached.Clear();
            if (hideRescue) { rescue.Hide(); rescue.Reveal = .01; }
        }
        private void BeginMove(Grip grip) {
            if (!Accept(grip.Target) || Native.IsIconic(grip.Target)) return;
            moving = true; Moves++; lastTarget = grip.Target;
            HideGrips(true); Native.BeginMove(grip.Target, Cursor.Position);
        }
        private void MoveByKeyboard() {
            IntPtr h = RecoveryTarget(); if (h == IntPtr.Zero) return;
            HideGrips(true);
            if (IsFullscreen(h)) { Native.SetForegroundWindow(h); return; }
            if (Native.IsIconic(h) || Native.IsZoomed(h)) Native.ShowWindow(h, 9);
            Native.SetForegroundWindow(h);
            Native.PostMessage(h, 0x0112, new IntPtr(0xF010), IntPtr.Zero);
        }
        private void Recover() {
            Recover(RecoveryTarget());
        }
        private void Recover(IntPtr h) {
            if (h == IntPtr.Zero || !Accept(h)) return;
            HideGrips(true);
            if (IsFullscreen(h)) { Native.SetForegroundWindow(h); return; }
            if (Native.IsIconic(h) || Native.IsZoomed(h)) Native.ShowWindow(h, 9);
            ReturnWindow(h, true);
            lastTarget = h;
        }
        private bool NeedsRescue(IntPtr h, Rectangle[] screens) {
            if (h == IntPtr.Zero || !Accept(h) || Native.IsIconic(h) || Native.IsZoomed(h) || IsFullscreen(h)) return false;
            Rectangle bounds = Native.Bounds(h);
            Native.RECT frame;
            if (!Native.GetWindowRect(h, out frame)) return false;
            float scale = 1;
            try { scale = Math.Max(1, Native.GetDpiForWindow(h) / 96f); } catch (EntryPointNotFoundException) { }
            RescueReadiness readiness;
            if (!rescueReadiness.TryGetValue(h, out readiness)) rescueReadiness[h] = readiness = new RescueReadiness();
            bool ready = readiness.Ready(frame.Rectangle, Geometry.VisibleArea(bounds, screens) == 0,
                moving || (Native.GetAsyncKeyState(1) & 0x8000) != 0, DateTime.UtcNow);
            return ready && Geometry.NeedsRescue(bounds, frame.Rectangle, screens, scale);
        }
        private void UpdateRescue() {
            long started = Stopwatch.GetTimestamp();
            try { UpdateRescueCore(); }
            finally { rescueMs = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency; }
        }
        private void UpdateRescueCore() {
            IntPtr foreground = Native.GetForegroundWindow();
            if (suspended || (foreground != IntPtr.Zero && !Native.IsDesktop(foreground) && IsFullscreen(foreground))) {
                rescue.Hide(); return;
            }
            DateTime now = DateTime.UtcNow;
            Rectangle[] screens = Screen.AllScreens.Select(s => s.WorkingArea).ToArray();
            if ((now - lastRescueScan).TotalMilliseconds >= 250) {
                lastRescueScan = now;
                rescueTarget = NeedsRescue(lastTarget, screens) ? lastTarget : IntPtr.Zero;
                if (rescueTarget == IntPtr.Zero) {
                    Native.EnumWindows(delegate(IntPtr h, IntPtr p) {
                        if (NeedsRescue(h, screens)) { rescueTarget = h; return false; }
                        return true;
                    }, IntPtr.Zero);
                }
            }
            if (rescueTarget == IntPtr.Zero || !NeedsRescue(rescueTarget, screens)) {
                rescue.Hide(); rescue.Reveal = .01;
                return;
            }
            if (!rescue.Visible || rescue.Target != rescueTarget) {
                Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
                float scale = 1;
                try { scale = Native.GetDpiForWindow(rescueTarget) / 96f; } catch (EntryPointNotFoundException) { }
                int width = Math.Min(work.Width, (int)Math.Round(128 * Math.Max(1, scale)));
                int height = Math.Min(work.Height, (int)Math.Round(28 * Math.Max(1, scale)));
                var r = new Rectangle(work.Left + (work.Width - width) / 2,
                    work.Top + (int)Math.Round(8 * Math.Max(1, scale)), width, height);
                // This return control is independent of Firefox focus. It exists
                // only for a lost window or one without usable external grips, never over that
                // window's website. Clicking restores the specific captured HWND.
                rescue.Place(new Placement(Edge.Top, r), rescueTarget);
            }
            rescue.Fade(true, !SystemInformation.IsMenuAnimationEnabled);
        }
        private void ReturnWindow(IntPtr h, bool focus) {
            Rectangle restored = Native.Bounds(h);
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            Rectangle desired = Geometry.Recover(restored, work, 160);
            // SetWindowPos uses the full frame, DWM Bounds excludes invisible resize margins.
            Native.RECT raw; Native.GetWindowRect(h, out raw);
            Rectangle frame = raw.Rectangle;
            Native.SetWindowPos(h, IntPtr.Zero, desired.X - (restored.X - frame.X),
                desired.Y - (restored.Y - frame.Y), desired.Width + frame.Width - restored.Width,
                desired.Height + frame.Height - restored.Height, focus ? 0x0004u : 0x0014u);
            if (focus) Native.SetForegroundWindow(h);
        }
        private bool IsFullscreen(IntPtr h) {
            if (Native.IsIconic(h) || Native.IsZoomed(h)) return false;
            Rectangle r = Native.Bounds(h);
            // Fullscreen and an oversized window are different: oversized windows
            // must still be recoverable. Exact monitor-sized borderless windows
            // are treated as fullscreen; leave F11/video playback untouched.
            return Screen.AllScreens.Any(s => r == s.Bounds);
        }
        private void Tick() {
            long started = Stopwatch.GetTimestamp(); tickCount++;
            try { TickCore(); }
            finally { tickMs = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency; }
        }
        private void TickCore() {
            try {
                if (stop.WaitOne(0)) { Application.Exit(); return; }
                IntPtr next = FindTarget();
                if (moving) {
                    if ((Native.GetAsyncKeyState(1) & 0x8000) != 0) { Report(); return; }
                    moving = false; lastNear = DateTime.UtcNow;
                }
                if (displaysChanged) {
                    displaysChanged = false;
                    Rectangle[] screens = Screen.AllScreens.Select(s => s.Bounds).ToArray();
                    Native.EnumWindows(delegate(IntPtr h, IntPtr p) {
                        if (Accept(h) && !Native.IsIconic(h) && Geometry.VisibleArea(Native.Bounds(h), screens) == 0) ReturnWindow(h, false);
                        return true;
                    }, IntPtr.Zero);
                }
                if (next != target) { HideGrips(); target = next; lastBounds = Rectangle.Empty; }
                if (suspended || target == IntPtr.Zero || Native.IsIconic(target) || !Native.IsWindow(target)) { HideGrips(); UpdateRescue(); Report(); return; }
                Rectangle bounds = Native.Bounds(target);
                // Maximized/F11: no overlays on page or on neighbouring applications.
                if (Native.IsZoomed(target) || IsFullscreen(target)) { HideGrips(true); Report(); return; }
                Point pointer = Cursor.Position;
                float scale = 1;
                try { scale = Native.GetDpiForWindow(target) / 96f; } catch (EntryPointNotFoundException) { }
                if (scale <= 0) scale = 1;
                Rectangle[] monitors = Screen.AllScreens.Select(s => s.WorkingArea).ToArray();
                Native.RECT frame;
                Native.GetWindowRect(target, out frame);
                // Stay outside the invisible native resize border as well as
                // outside the painted frame. Resizing must remain available.
                List<Placement> options = Geometry.Grips(frame.Rectangle, monitors, pointer, scale);
                bool geometryChanged = bounds != lastBounds;
                if (geometryChanged) HideGrips();
                lastBounds = bounds;
                // Do not reveal while selecting text, dragging tabs, or resizing the native frame.
                bool buttonDown = (Native.GetAsyncKeyState(1) & 0x8000) != 0;
                DateTime now = DateTime.UtcNow;
                Placement nearest = options.OrderBy(p => Geometry.Distance(p.Bounds, pointer)).FirstOrDefault();
                // Keep exactly one capsule, including its fade-out. Do not switch
                // edges or chase the pointer while the user is reaching for it.
                Grip visibleGrip = grips.Values.FirstOrDefault(g => g.Visible);
                foreach (var pair in grips) {
                    Grip grip = pair.Value;
                    Placement placement = options.FirstOrDefault(p => p.Edge == pair.Key);
                    if (placement == null) { grip.Hide(); approached.Remove(pair.Key); continue; }
                    // Freeze a visible grip until it is dismissed; never chase the pointer.
                    if (grip.Visible) placement = new Placement(pair.Key, grip.Bounds);
                    bool near = Geometry.Distance(placement.Bounds, pointer) <= 18 * scale;
                    bool closest = visibleGrip != null ? visibleGrip == grip : nearest != null && nearest.Edge == pair.Key;
                    bool hovered = grip.Visible && grip.Bounds.Contains(pointer);
                    IntPtr under = Native.GetAncestor(Native.WindowFromPoint(pointer), 2);
                    bool unobstructed = under == target || grips.Values.Any(g => g.Visible && g.Handle == under);
                    // A popup or foreign window covering the Firefox hit area
                    // suppresses reveal; background clipped windows use hover too.
                    bool blocked = !unobstructed && (bounds.Contains(pointer) || Native.GetWindow(under, 4) == target);
                    if (blocked) near = false;
                    bool requested = !buttonDown && !blocked && closest && (hovered || near);
                    if (requested) {
                        if (!approached.ContainsKey(pair.Key)) approached[pair.Key] = now;
                        if ((now - approached[pair.Key]).TotalMilliseconds >= 120 || hovered) {
                            if (!grip.Visible) grip.Place(placement, target);
                            lastNear = now;
                        }
                    } else approached.Remove(pair.Key);
                    if (grip.Visible) grip.Fade(!buttonDown && !blocked && (requested || (now - lastNear).TotalMilliseconds < 220), !SystemInformation.IsMenuAnimationEnabled);
                }
                UpdateRescue(); Report();
            } catch (Exception e) {
                HideGrips(true); notice = e.GetType().Name; Report();
            }
        }
        private void Report() {
            if (String.IsNullOrEmpty(diagnostics) || (DateTime.UtcNow - lastReport).TotalMilliseconds < 150) return;
            lastReport = DateTime.UtcNow;
            var json = new StringBuilder("{\"target\":" + target.ToInt64() + ",\"moves\":" + Moves + ",\"moveHotkey\":" + HotkeyMove.ToString().ToLowerInvariant() + ",\"recoverHotkey\":" + HotkeyRecover.ToString().ToLowerInvariant());
            Rectangle rect = target != IntPtr.Zero ? Native.Bounds(target) : Rectangle.Empty;
            json.Append(",\"bounds\":" + JsonRect(rect) + ",\"grips\":[");
            bool first = true;
            foreach (var pair in grips) if (pair.Value.Visible) {
                if (!first) json.Append(','); first = false;
                double reveal = pair.Value.Reveal;
                json.Append("{\"edge\":\"" + pair.Key + "\",\"bounds\":" + JsonRect(pair.Value.Bounds) + ",\"opacity\":" + reveal.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
            }
            IntPtr rescueHandle = rescue.Target;
            json.Append("],\"rescue\":" + (rescue.Visible ? "{\"target\":" + rescueHandle.ToInt64() + ",\"bounds\":" + JsonRect(rescue.Bounds) + "}" : "null"));
            json.Append(",\"dpi\":" + Native.GetDpiForWindow(Handle) + ",\"workAreas\":[" + String.Join(",", Screen.AllScreens.Select(s => JsonRect(s.WorkingArea))) + "]");
            json.Append(",\"perf\":{\"ticks\":" + tickCount + ",\"renders\":" + Capsule.RenderCount + ",\"presents\":" + Grip.PresentCount +
                ",\"tickMs\":" + tickMs.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"rescueMs\":" + rescueMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
            json.Append(",\"notice\":\"" + notice.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}");
            try { File.WriteAllText(diagnostics, json.ToString(), new UTF8Encoding(false)); } catch (IOException) { }
        }
        private static string JsonRect(Rectangle r) { return String.Format("{{\"x\":{0},\"y\":{1},\"width\":{2},\"height\":{3}}}", r.X, r.Y, r.Width, r.Height); }
        protected override void WndProc(ref Message m) {
            if (m.Msg == 0x0312) { if (m.WParam.ToInt32() == 1) MoveByKeyboard(); else if (m.WParam.ToInt32() == 2) Recover(); }
            if (m.Msg == 0x8001) Application.Exit();
            base.WndProc(ref m);
        }
        protected override void Dispose(bool disposing) {
            if (disposing) {
                timer.Stop(); timer.Dispose();
                stop.Dispose();
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= displayHandler;
                Native.UnregisterHotKey(Handle, 1); Native.UnregisterHotKey(Handle, 2);
                foreach (Grip grip in grips.Values) grip.Dispose();
                rescue.Dispose();
                tray.Visible = false; tray.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class Program {
        [STAThread] private static int Main(string[] args) {
            Native.EnablePhysicalCoordinates();
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--self-test")) return SelfTests.Run();
            string render = Value(args, "--render-style");
            if (render != null) {
                using (var preview = new Bitmap(640, 240)) {
                    using (var g = Graphics.FromImage(preview)) {
                        g.Clear(Color.FromArgb(50, 49, 54));
                        using (var normal = Capsule.Render(new Size(160, 18), false))
                        using (var hover = Capsule.Render(new Size(160, 18), true)) {
                            g.DrawImageUnscaled(normal, 40, 35); g.DrawImageUnscaled(hover, 40, 90);
                            g.InterpolationMode = InterpolationMode.NearestNeighbor;
                            g.DrawImage(normal, new Rectangle(40, 155, 480, 54));
                        }
                    }
                    preview.Save(render, ImageFormat.Png);
                }
                return 0;
            }
            bool demoMode = args.Contains("--demo");
            bool created;
            using (var mutex = new System.Threading.Mutex(true, "Local\\FirefoxGhostUIHandles" + (demoMode ? "Demo" : ""), out created)) {
                if (!created) return 0;
                string diagnostics = Value(args, "--diagnostics");
                Form demo = null, otherDemo = null;
                if (demoMode) {
                    demo = new Form { Text = "Ghost UI — isolated drag test", StartPosition = FormStartPosition.Manual,
                        Bounds = new Rectangle(360, 260, 900, 560), BackColor = Color.FromArgb(22, 20, 32) };
                    var label = new Label { Text = "Ghost UI drag test\n\nApproach any edge to reveal a grip.\nDrag the grip to move this window.\nCtrl+Alt+F10 returns it to the pointer's monitor.",
                        ForeColor = Color.White, Font = new Font("Segoe UI", 18), Dock = DockStyle.Fill, Padding = new Padding(35) };
                    demo.Controls.Add(label);
                    var button = new Button { Text = "Page button — 0", Dock = DockStyle.Bottom, Height = 60 };
                    int clicks = 0; button.Click += delegate { button.Text = "Page button — " + (++clicks); };
                    demo.Controls.Add(button);
                    var poses = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38 };
                    foreach (string pose in new[] { "Normal", "Right", "Left", "Above", "Below", "Lost", "Maximize", "Lost + unfocus" }) {
                        string selected = pose;
                        var poseButton = new Button { Text = pose, AutoSize = true };
                        poseButton.Click += delegate {
                            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
                            demo.WindowState = FormWindowState.Normal;
                            var r = new Rectangle(work.Left + 300, work.Top + 200, 900, 560);
                            if (selected == "Right") r.X = work.Right - 140;
                            if (selected == "Left") r.X = work.Left - r.Width + 140;
                            if (selected == "Above") r.Y = work.Top - r.Height + 140;
                            if (selected == "Below") r.Y = work.Bottom - 140;
                            if (selected == "Lost" || selected == "Lost + unfocus") { r.X = work.Right + 600; r.Y = work.Bottom + 600; }
                            demo.Bounds = r;
                            if (selected == "Maximize") demo.WindowState = FormWindowState.Maximized;
                            if (selected == "Lost + unfocus") {
                                if (otherDemo == null || otherDemo.IsDisposed) {
                                    otherDemo = new Form { Text = "Ghost UI — other window fixture", StartPosition = FormStartPosition.Manual,
                                        Bounds = new Rectangle(work.Left + 200, work.Top + 150, 700, 400) };
                                    otherDemo.Controls.Add(new Label { Text = "Another window has focus.\nThe lost demo must retain its return capsule.",
                                        Dock = DockStyle.Fill, Font = new Font("Segoe UI", 16), Padding = new Padding(30) });
                                }
                                otherDemo.Show(); otherDemo.Activate();
                            }
                        };
                        poses.Controls.Add(poseButton);
                    }
                    demo.Controls.Add(poses); poses.BringToFront();
                    demo.Show(); Native.ShowWindow(demo.Handle, 5); demo.Activate();
                }
                using (var controller = new Controller(diagnostics, demo)) Application.Run();
                if (demo != null) demo.Dispose();
                if (otherDemo != null) otherDemo.Dispose();
                mutex.ReleaseMutex();
            }
            return 0;
        }
        private static string Value(string[] args, string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    }

    internal static class SelfTests {
        private static int checks;
        private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); checks++; }
        internal static int Run() {
            try {
                Rectangle[] monitors = { new Rectangle(-1920, 0, 1920, 1040), new Rectangle(0, 0, 1920, 1040), new Rectangle(1920, -500, 2560, 1400) };
                var poses = new[] { new Rectangle(300, 200, 1000, 600), new Rectangle(1800, 100, 1000, 600),
                    new Rectangle(-900, -400, 1000, 600), new Rectangle(300, 900, 1000, 600), new Rectangle(-2600, 100, 1000, 600),
                    new Rectangle(-6000, -4000, 1000, 600), new Rectangle(-10, -10, 6000, 2000) };
                foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f }) foreach (Rectangle pose in poses) {
                    var grips = Geometry.Grips(pose, monitors, new Point(1890, 500), scale);
                    Check(grips.Select(p => p.Edge).Distinct().Count() == grips.Count, "Duplicate edge");
                    foreach (var grip in grips) {
                        Check(!grip.Bounds.IntersectsWith(pose), "Grip overlaps website");
                        Check(monitors.Any(m => m.Contains(grip.Bounds)), "Grip in monitor gap");
                        Check(grip.Bounds.Width >= 8 && grip.Bounds.Height >= 8, "Grip too small");
                    }
                }
                var only = new[] { new Rectangle(0, 0, 1920, 1040) };
                Check(Geometry.Grips(new Rectangle(1800, 100, 900, 600), only, new Point(1801, 400), 1).Any(p => p.Edge == Edge.Left), "Right-clipped window lacks left grip");
                Check(Geometry.Grips(new Rectangle(-800, 100, 900, 600), only, new Point(99, 400), 1).Any(p => p.Edge == Edge.Right), "Left-clipped window lacks right grip");
                Check(Geometry.Grips(new Rectangle(400, -500, 900, 600), only, new Point(700, 99), 1).Any(p => p.Edge == Edge.Bottom), "Top-clipped window lacks bottom grip");
                Check(Geometry.Grips(new Rectangle(400, 950, 900, 600), only, new Point(700, 951), 1).Any(p => p.Edge == Edge.Top), "Bottom-clipped window lacks top grip");
                foreach (Rectangle pose in poses) foreach (Rectangle work in monitors) {
                    var recovered = Geometry.Recover(pose, work, 160);
                    Check(work.Contains(recovered), "Recovery leaves offscreen content");
                }
                Check(Geometry.VisibleArea(new Rectangle(1800, 100, 900, 600), only) == 120L * 600, "Right clipping area");
                Check(Geometry.VisibleArea(new Rectangle(-800, 100, 900, 600), only) == 100L * 600, "Left clipping area");
                Check(Geometry.VisibleArea(new Rectangle(3000, 3000, 900, 600), only) == 0, "Lost window detection");
                Check(Geometry.VisibleArea(new Rectangle(-100, -100, 2200, 1400), only) == 1920L * 1040, "Oversized visible window detection");
                double opacity = .01;
                for (int i = 0; i < 20; i++) opacity = Motion.NextOpacity(opacity, .82, false);
                Check(opacity == .82, "Reveal must settle without oscillating");
                Check(Motion.NextOpacity(opacity, .82, false) == opacity, "Steady grip must not flash");
                for (int i = 0; i < 20; i++) opacity = Motion.NextOpacity(opacity, 0, false);
                Check(opacity == 0, "Fade must finish");
                Check(Motion.NextOpacity(0, .82, true) == .82, "Reduced motion immediate reveal");
                Check(Motion.OpacityAt(.01, .87, 0) == .01, "Reveal starts at current opacity without a flash");
                Check(Math.Abs(Motion.OpacityAt(.01, .87, 210) - .87) < .00001, "Reveal settles after 210 ms");
                Check(Motion.OpacityAt(.87, 0, 180) == 0, "Dismissal reaches zero after 180 ms");
                Check(Motion.OpacityAt(.01, .87, 30) < .01 + (.86 * 30 / 210), "Reveal starts gently instead of jumping linearly");
                double prior = .01;
                for (int ms=0; ms<=300; ms+=10) {
                    double next = Motion.OpacityAt(.01, .87, ms);
                    Check(next >= prior && next <= .87, "Timed reveal must be monotonic and bounded");
                    prior = next;
                }
                prior = .87;
                for (int ms=0; ms<=300; ms+=10) {
                    double next = Motion.OpacityAt(.87, 0, ms);
                    Check(next <= prior && next >= 0, "Timed dismissal must be monotonic and bounded");
                    prior = next;
                }
                foreach (float dpi in new[] { 1f, 1.25f, 1.5f, 2f }) {
                    foreach (Rectangle pose in new[] { new Rectangle(100,100,320,240), new Rectangle(0,0,320,240),
                        new Rectangle(1800,100,900,600), new Rectangle(-800,100,900,600),
                        new Rectangle(400,-500,900,600), new Rectangle(400,950,900,600),
                        new Rectangle(1910,100,900,600), new Rectangle(300,100,900,600) }) {
                        Check(!Geometry.NeedsRescue(pose, pose, only, dpi), "Accessible small/clipped window must not show return capsule at DPI " + dpi);
                    }
                    Rectangle lost = new Rectangle(3000,100,900,600);
                    Check(Geometry.NeedsRescue(lost, lost, only, dpi), "Completely lost window needs return capsule");
                    Rectangle enclosing = new Rectangle(-100,-100,2200,1400);
                    Check(Geometry.NeedsRescue(enclosing, enclosing, only, dpi), "Oversized window without accessible edges needs rescue");
                    Rectangle sliver = new Rectangle(1919,1079,600,400);
                    Check(Geometry.NeedsRescue(sliver, sliver, only, dpi), "Corner sliver without space for a grip needs rescue");
                    Check(!Geometry.NeedsRescue(Rectangle.Empty, Rectangle.Empty, only, dpi), "Invalid window must not show return capsule");
                }
                Rectangle small = new Rectangle(100,100,320,240);
                Check(!Geometry.NeedsRescue(small, new Rectangle(92,92,336,256), only, 1.25f), "Invisible resize margins must not trigger rescue for a visible small window");
                Rectangle[] separated = { new Rectangle(-1600,0,1400,900), new Rectangle(0,0,1920,1040) };
                Rectangle gap = new Rectangle(-190,100,180,400);
                Check(Geometry.NeedsRescue(gap, gap, separated, 1), "Window lost in monitor gap needs rescue");
                Rectangle second = new Rectangle(-1500,100,320,240);
                Check(!Geometry.NeedsRescue(second, second, separated, 1.5f), "Small window on negative-coordinate monitor must stay quiet");
                var readiness = new RescueReadiness();
                DateTime instant = DateTime.UtcNow;
                Check(!readiness.Ready(small, false, false, instant), "Partial rescue waits for stable bounds");
                Check(!readiness.Ready(small, false, false, instant.AddMilliseconds(499)), "Partial rescue must not appear early");
                Check(readiness.Ready(small, false, false, instant.AddMilliseconds(500)), "Stable inaccessible window becomes recoverable after 500 ms");
                Check(!readiness.Ready(small, false, true, instant.AddSeconds(1)), "Held mouse suppresses rescue and restarts delay");
                Check(!readiness.Ready(small, false, false, instant.AddMilliseconds(1499)), "Mouse release retains settling delay");
                Check(readiness.Ready(small, false, false, instant.AddMilliseconds(1500)), "Settling ends after release");
                Check(readiness.Ready(small, true, false, instant), "Completely lost window skips settling delay");
                Check(!readiness.Ready(small, true, true, instant), "Even a lost window must not show rescue while dragging");
                foreach (Size size in new[] { new Size(128,14), new Size(160,18), new Size(192,21), new Size(14,128) }) {
                    using (Bitmap capsule = Capsule.Render(size, false)) {
                        Check(capsule.GetPixel(0,0).A == 0, "Capsule corner must be transparent");
                        Check(capsule.GetPixel(size.Width / 2,size.Height / 2).A > 200, "Capsule must have a filled body");
                        Check(capsule.GetPixel(size.Width-1,0).A == 0, "Capsule must have matching rounded ends");
                        bool blended = false;
                        for (int y = 0; y < size.Height; y++) for (int x = 0; x < size.Width; x++) {
                            byte alpha = capsule.GetPixel(x,y).A;
                            if (alpha > 0 && alpha < 180) blended = true;
                        }
                        Check(blended, "Capsule edge must retain per-pixel antialiasing");
                    }
                }
                // Native layered-surface smoke check on our own hidden fixture,
                // never on Firefox and without injecting any mouse/key input.
                using (var owner = new Form()) using (var grip = new Grip(delegate {}, delegate {})) {
                    IntPtr handle = owner.Handle;
                    Check(Native.GetAwarenessFromDpiAwarenessContext(Native.GetThreadDpiAwarenessContext()) == 2,
                        "Actual process must use per-monitor physical desktop coordinates");
                    grip.Place(new Placement(Edge.Top, new Rectangle(-12000,-12000,160,18)), handle);
                    grip.Fade(true, true);
                    Check(grip.Visible && grip.Reveal == .87, "Native alpha surface failed to present");
                    grip.Hide();
                }
                Controller.CheckNativeRecovery(Check);
                using (var current = Process.GetCurrentProcess()) {
                    Check(Native.ExecutableName((uint)current.Id).Equals(Path.GetFileName(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase),
                        "Direct native process identity must match the actual executable");
                }
                Check(Native.ExecutableName(UInt32.MaxValue) == String.Empty, "Missing process identity must be rejected safely");
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-results.json"), "{\"passed\":" + checks + "}");
                return 0;
            } catch (Exception e) {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-results.json"), "{\"error\":\"" + e.Message + "\"}");
                return 1;
            }
        }
    }
}
