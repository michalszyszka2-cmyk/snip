// Background snipping tool. Sits in the tray; press Ctrl + ' + # (all held together),
// drag a box, and that part of the screen lands on the clipboard. Esc cancels.
// Build: build.cmd  ->  snip.exe. The exe installs itself: run it from anywhere and it
// copies itself to %LOCALAPPDATA%\Programs\Snip, adds Startup + Desktop shortcuts, starts.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main()
    {
        // Before any window: sharp text at the real screen scale, modern controls.
        SetProcessDPIAware();
        Application.EnableVisualStyles();

        if (!Installer.IsInstalledCopy()) { Installer.Install(); return; }

        bool first;
        using (new Mutex(true, "snip-tray-hotkey", out first))
        {
            if (!first) return; // already running
            Application.Run(new TrayApp());
        }
    }
}

static class Installer
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Snip");
    public static readonly string Exe = Path.Combine(Dir, "snip.exe");
    static string StartupLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Snip.lnk"); } }
    static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Snip.lnk"); } }
    static string Self { get { return Process.GetCurrentProcess().MainModule.FileName; } }

    public static bool IsInstalledCopy()
    {
        return string.Equals(Path.GetFullPath(Self), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase);
    }

    public static void Install()
    {
        try
        {
            StopRunning();
            Directory.CreateDirectory(Dir);
            File.Copy(Self, Exe, true);
            MakeShortcut(StartupLink);
            MakeShortcut(DesktopLink);
            Process.Start(Exe);
            MessageBox.Show("Snip is installed and running.\n\nPress Ctrl + ' + # together, drag a box, then paste with Ctrl+V.\n" +
                "It starts with Windows. Right-click the camera icon by the clock to quit or uninstall.",
                "Snip", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Snip could not be installed:\n\n" + ex.Message, "Snip", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public static void Uninstall()
    {
        if (MessageBox.Show("Remove Snip from this computer?", "Snip", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        foreach (var link in new[] { StartupLink, DesktopLink }) if (File.Exists(link)) File.Delete(link);
        // the running exe can't delete itself; a short-lived cmd removes the folder after we exit
        Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + Dir + "\"")
            { CreateNoWindow = true, UseShellExecute = false });
        Application.Exit();
    }

    // An older copy holds the file open, so close it before overwriting.
    static void StopRunning()
    {
        int me = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcessesByName("snip"))
        {
            if (p.Id == me) continue;
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
    }

    static void MakeShortcut(string path)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(t);
        object lnk = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
        Type lt = lnk.GetType();
        lt.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { Exe });
        lt.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { "Snip - Ctrl + ' + #" });
        lt.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
    }

    // Windows 11 hides new tray icons behind the ^ arrow. Once our icon has been
    // registered, mark it as always shown (same as dragging it onto the taskbar).
    public static void PromoteTrayIcon()
    {
        using (var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
        {
            if (root == null) return;
            foreach (string name in root.GetSubKeyNames())
                using (var k = root.OpenSubKey(name, true))
                {
                    if (k == null) continue;
                    string path = k.GetValue("ExecutablePath") as string;
                    if (path != null && string.Equals(path, Exe, StringComparison.OrdinalIgnoreCase) && k.GetValue("IsPromoted") == null)
                        k.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                }
        }
    }
}

class TrayApp : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly KeyHook hook;
    readonly Control pump = new Control(); // marshals the hook callback onto the UI thread
    bool busy;

    public TrayApp()
    {
        pump.CreateControl();
        tray = new NotifyIcon { Icon = MakeIcon(), Text = "Snip  -  Ctrl + ' + #", Visible = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Snip now", null, (s, e) => Snip());
        menu.Items.Add("Quit", null, (s, e) => { tray.Visible = false; Application.Exit(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Uninstall", null, (s, e) => Installer.Uninstall());
        tray.ContextMenuStrip = menu;
        hook = new KeyHook(() => pump.BeginInvoke(new Action(Snip)));

        // Windows registers the icon a moment after it first appears
        var promote = new System.Windows.Forms.Timer { Interval = 3000 };
        promote.Tick += (s, e) => { promote.Stop(); try { Installer.PromoteTrayIcon(); } catch { } };
        promote.Start();
    }

    void Snip()
    {
        if (busy) return;
        busy = true;
        try { using (var f = new Overlay()) f.ShowDialog(); }
        finally { busy = false; }
    }

    // Microsoft Fluent 3D camera (MIT), embedded as camera.ico at build time.
    static Icon MakeIcon()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using (var stream = asm.GetManifestResourceStream("camera.ico"))
            return new Icon(stream, SystemInformation.SmallIconSize);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { hook.Dispose(); tray.Visible = false; tray.Dispose(); pump.Dispose(); }
        base.Dispose(disposing);
    }
}

// Low-level keyboard hook: fires once when Ctrl, ' and # are all down at the same time.
// Ordinary keys can't be a Windows hotkey, so we watch the keyboard ourselves.
class KeyHook : IDisposable
{
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern short VkKeyScan(char c);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);

    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, VK_CONTROL = 0x11;
    readonly HookProc proc; // kept alive so the GC can't collect the callback
    IntPtr handle;
    readonly Action fire;
    readonly int vkQuote, vkHash; // resolved from the current keyboard layout
    bool fired;

    // The hook lives on its own thread with its own message loop. Windows drops a
    // low-level hook whose thread is slow to answer, and the UI thread is busy for a
    // moment while it grabs a 4K screenshot - right when the chord keys are released.
    public KeyHook(Action fire)
    {
        this.fire = fire;
        vkQuote = VkKeyScan('\'') & 0xFF;
        vkHash = VkKeyScan('#') & 0xFF;
        proc = Callback;
        var ready = new ManualResetEvent(false);
        var t = new Thread(() =>
        {
            handle = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            ready.Set();
            // Windows silently drops the hook if this process is ever frozen for a moment
            // (antivirus scanning a new exe, sleep/resume). Re-arm it every 30 seconds.
            var rearm = new System.Windows.Forms.Timer { Interval = 30000 };
            rearm.Tick += (s, e) =>
            {
                IntPtr fresh = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
                if (fresh == IntPtr.Zero) return;
                IntPtr old = handle;
                handle = fresh;
                UnhookWindowsHookEx(old);
            };
            rearm.Start();
            Application.Run(); // message loop for the hook; ends with the process
        });
        t.IsBackground = true;
        t.Start();
        ready.WaitOne();
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam, vk = Marshal.ReadInt32(lParam);
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            if (vk == vkQuote || vk == vkHash)
            {
                if (down)
                {
                    bool ctrl = IsDown(VK_CONTROL);
                    bool quote = vk == vkQuote || IsDown(vkQuote);
                    bool hash = vk == vkHash || IsDown(vkHash);
                    if (ctrl && quote && hash)
                    {
                        if (!fired) { fired = true; fire(); }
                        return (IntPtr)1; // swallow the key so it isn't typed
                    }
                }
                else fired = false; // chord released; allow the next one
            }
        }
        return CallNextHookEx(handle, code, wParam, lParam);
    }

    static bool IsDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
    public void Dispose() { UnhookWindowsHookEx(handle); }
}

// Full-screen frozen, dimmed copy of the desktop. Drag a box; the box is copied bright.
class Overlay : Form
{
    readonly Bitmap shot, dimmed;
    Point start;
    Rectangle sel;
    bool dragging;

    public Overlay()
    {
        Rectangle area = SystemInformation.VirtualScreen;
        // Pre-multiplied 32bpp is the format GDI+ can blit without converting, so
        // both bitmaps are kept in it; the overlay repaints on every mouse move.
        shot = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size);

        dimmed = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(dimmed))
        {
            g.DrawImageUnscaled(shot, 0, 0);
            using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                g.FillRectangle(dim, 0, 0, area.Width, area.Height);
        }

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = area;
        TopMost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); }

    protected override void OnMouseDown(MouseEventArgs e) { start = e.Location; dragging = true; sel = Rectangle.Empty; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!dragging) return;
        Rectangle old = sel;
        sel = Between(start, e.Location);
        // Repaint only where the box was and where it is now, not the whole 4K screen.
        Rectangle dirty = old.IsEmpty ? sel : Rectangle.Union(old, sel);
        dirty.Inflate(2, 2);
        Invalidate(dirty);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        dragging = false;
        sel = Between(start, e.Location);
        if (sel.Width > 2 && sel.Height > 2)
        {
            using (var crop = shot.Clone(sel, PixelFormat.Format24bppRgb))
            using (var fit = Shrink(crop, 1568)) Clipboard.SetImage(fit);
            SystemSounds.Asterisk.Play();
        }
        Close();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Rectangle c = e.ClipRectangle;
        e.Graphics.DrawImage(dimmed, c, c, GraphicsUnit.Pixel);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (sel.Width == 0) return;
        Rectangle part = Rectangle.Intersect(sel, e.ClipRectangle);
        if (!part.IsEmpty) e.Graphics.DrawImage(shot, part, part, GraphicsUnit.Pixel);
        using (var p = new Pen(Color.White, 1)) e.Graphics.DrawRectangle(p, sel);
    }

    // Claude downsizes anything whose long side is over 1568px, so shrink to that
    // first: same image to Claude, a fraction of the upload. Smaller boxes are untouched.
    static Bitmap Shrink(Bitmap src, int maxSide)
    {
        int longSide = Math.Max(src.Width, src.Height);
        if (longSide <= maxSide) return new Bitmap(src);
        double k = (double)maxSide / longSide;
        var dst = new Bitmap(Math.Max(1, (int)Math.Round(src.Width * k)), Math.Max(1, (int)Math.Round(src.Height * k)),
            PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        }
        return dst;
    }

    static Rectangle Between(Point a, Point b)
    {
        return new Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { shot.Dispose(); dimmed.Dispose(); }
        base.Dispose(disposing);
    }
}
