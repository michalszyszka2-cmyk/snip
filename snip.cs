// Background snipping tool. Sits in the tray; press Ctrl + ' + # (all held together),
// drag a box, and that part of the screen lands on the clipboard. Hold Shift while you
// drag and the box is recorded as a video (with your voice) until Alt+S. Esc cancels.
// Build: build.cmd  ->  snip.exe. The exe installs itself: run it from anywhere and it
// copies itself to %LOCALAPPDATA%\Programs\Snip, adds Startup + Desktop shortcuts, starts.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Media;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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
                "Hold Shift while dragging to record that box as a video; Alt+S stops.\n" +
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

// key=value lines in settings.txt next to the exe. Only the microphone choice so far.
static class Settings
{
    static readonly string File_ = Path.Combine(Installer.Dir, "settings.txt");
    static readonly Dictionary<string, string> map = new Dictionary<string, string>();
    static bool loaded;

    public static string Get(string key)
    {
        Load();
        string v; return map.TryGetValue(key, out v) ? v : null;
    }

    public static void Set(string key, string value)
    {
        Load();
        if (value == null) map.Remove(key); else map[key] = value;
        var sb = new StringBuilder();
        foreach (var kv in map) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
        try { File.WriteAllText(File_, sb.ToString(), new UTF8Encoding(false)); } catch { }
    }

    static void Load()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (!File.Exists(File_)) return;
            foreach (string line in File.ReadAllLines(File_))
            {
                int i = line.IndexOf('=');
                if (i > 0) map[line.Substring(0, i)] = line.Substring(i + 1);
            }
        }
        catch { }
    }
}

class Mic
{
    public string Name, Alt;   // the friendly name, and the stable "@device_cm_..." id ffmpeg takes
    public override string ToString() { return Name; }
}

// ffmpeg does the recording: finds an installed copy, or offers to download one once.
static class Ffmpeg
{
    const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    static readonly string OwnCopy = Path.Combine(Installer.Dir, @"ffmpeg\ffmpeg.exe");
    static string found;

    public static string Find()
    {
        if (found != null && File.Exists(found)) return found;
        var tries = new List<string> { OwnCopy };
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            if (dir.Trim().Length > 0) tries.Add(Path.Combine(dir.Trim(), "ffmpeg.exe"));
        // winget puts it here even when PATH has not caught up yet
        string winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Packages");
        try
        {
            if (Directory.Exists(winget))
                foreach (string pkg in Directory.GetDirectories(winget, "*FFmpeg*"))
                    tries.AddRange(Directory.GetFiles(pkg, "ffmpeg.exe", SearchOption.AllDirectories));
        }
        catch { }
        foreach (string p in tries)
            try { if (File.Exists(p)) return found = Path.GetFullPath(p); } catch { }
        return null;
    }

    // Ask once, download (about 115 MB), keep only ffmpeg.exe. Returns the path or null.
    public static string Ensure()
    {
        string p = Find();
        if (p != null) return p;
        if (MessageBox.Show("Recording needs FFmpeg, a free video encoder, which is not on this computer.\n\n" +
                "Download it now? About 115 MB, from gyan.dev, kept inside Snip's own folder.",
                "Snip", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return null;
        using (var f = new DownloadForm()) f.ShowDialog();
        return Find();
    }

    class DownloadForm : Form
    {
        readonly ProgressBar bar = new ProgressBar { Dock = DockStyle.Bottom, Height = 22, Maximum = 1000 };
        readonly Label label = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0) };
        readonly WebClient web = new WebClient();
        readonly string zip = Path.Combine(Path.GetTempPath(), "snip-ffmpeg.zip");

        public DownloadForm()
        {
            Text = "Snip - downloading FFmpeg"; Width = 420; Height = 110;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; TopMost = true;
            Controls.Add(label); Controls.Add(bar);
            label.Text = "Starting…";
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            web.DownloadProgressChanged += (s, e) =>
            {
                bar.Value = Math.Min(1000, (int)(e.ProgressPercentage * 10));
                label.Text = string.Format("Downloading… {0} / {1} MB", e.BytesReceived >> 20, e.TotalBytesToReceive >> 20);
            };
            web.DownloadFileCompleted += (s, e) =>
            {
                if (e.Cancelled) { Close(); return; }
                if (e.Error != null) { MessageBox.Show(this, "Download failed:\n\n" + e.Error.Message, "Snip"); Close(); return; }
                label.Text = "Unpacking…"; Refresh();
                try { Unpack(); }
                catch (Exception ex) { MessageBox.Show(this, "Could not unpack FFmpeg:\n\n" + ex.Message, "Snip"); }
                Close();
            };
            Shown += (s, e) => web.DownloadFileAsync(new Uri(DownloadUrl), zip);
            FormClosing += (s, e) => { if (web.IsBusy) web.CancelAsync(); };
        }

        void Unpack()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OwnCopy));
            using (var z = ZipFile.OpenRead(zip))
                foreach (var entry in z.Entries)
                    if (entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)) { entry.ExtractToFile(OwnCopy, true); break; }
            try { File.Delete(zip); } catch { }
            if (!File.Exists(OwnCopy)) throw new Exception("ffmpeg.exe was not in the archive.");
        }
    }

    // "ffmpeg -list_devices" prints each DirectShow device with its alternative (stable) name on the next line.
    public static List<Mic> ListMics()
    {
        var mics = new List<Mic>();
        string exe = Find();
        if (exe == null) return mics;
        string err = Run(exe, "-hide_banner -list_devices true -f dshow -i dummy", 8000);
        Mic last = null;
        foreach (Match m in Regex.Matches(err, "\"([^\"]+)\"\\s+\\((audio|video|none)\\)|Alternative name \"([^\"]+)\""))
        {
            if (m.Groups[1].Success) { last = m.Groups[2].Value == "audio" ? new Mic { Name = m.Groups[1].Value } : null; if (last != null) mics.Add(last); }
            else if (last != null) { last.Alt = m.Groups[3].Value; last = null; }
        }
        return mics;
    }

    static string Run(string exe, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        using (var p = Process.Start(psi))
        {
            string err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) try { p.Kill(); } catch { }
            return err;
        }
    }
}

// One recording at a time: ffmpeg grabs the screen area (gdigrab) and the chosen mic
// (DirectShow), encodes H.264 + AAC straight into an mp4 in Downloads. "q" on its stdin
// ends it cleanly so the file is playable.
static class Recorder
{
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    static Process proc;
    static StringBuilder errors;
    static bool stopping;
    public static volatile bool Active;
    public static string LastFile;
    public static event Action<string, string> Finished;   // (file or null, error or null) on the UI thread

    static readonly Control ui = new Control();
    public static void Init() { ui.CreateControl(); }   // call on the UI thread first, so Finished fires there

    public static string DownloadsDir()
    {
        IntPtr p;
        if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) == 0)
        {
            string s = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p);
            if (Directory.Exists(s)) return s;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public static bool Start(Rectangle area, Mic mic)
    {
        if (Active) return false;
        string exe = Ffmpeg.Ensure();
        if (exe == null) return false;
        // encoders want even sizes
        area.Width &= ~1; area.Height &= ~1;
        if (area.Width < 2 || area.Height < 2) return false;

        string file = Path.Combine(DownloadsDir(), "recording-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm") + ".mp4");
        for (int n = 2; File.Exists(file); n++)
            file = Path.Combine(DownloadsDir(), "recording-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm") + "-" + n + ".mp4");

        var a = new StringBuilder("-hide_banner -loglevel error -y ");
        a.AppendFormat("-thread_queue_size 1024 -f gdigrab -framerate 30 -offset_x {0} -offset_y {1} -video_size {2}x{3} -draw_mouse 1 -i desktop ",
            area.X, area.Y, area.Width, area.Height);
        if (mic != null)
            a.AppendFormat("-thread_queue_size 1024 -rtbufsize 100M -f dshow -i \"audio={0}\" ", mic.Alt ?? mic.Name);
        a.Append("-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p -r 30 ");
        if (mic != null) a.Append("-c:a aac -b:a 160k -ac 1 ");
        a.Append("-movflags +faststart \"").Append(file).Append('"');

        var psi = new ProcessStartInfo(exe, a.ToString())
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardError = true,
        };
        errors = new StringBuilder();
        stopping = false;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start FFmpeg:\n\n" + ex.Message, "Snip", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        LastFile = file;
        Active = true;
        proc.EnableRaisingEvents = true;
        proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
        proc.BeginErrorReadLine();
        proc.Exited += (s, e) => ui.BeginInvoke(new Action(OnExit));
        return true;
    }

    public static void Stop()
    {
        if (!Active || stopping) return;
        stopping = true;
        try { proc.StandardInput.Write("q"); proc.StandardInput.Flush(); } catch { }
        // ffmpeg finishes the file after "q"; only kill it if it hangs
        var t = new System.Windows.Forms.Timer { Interval = 15000 };
        t.Tick += (s, e) => { t.Stop(); try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { } };
        t.Start();
    }

    static void OnExit()
    {
        var p = proc; proc = null;
        Active = false;
        string err;
        lock (errors) err = errors.ToString().Trim();
        int code = 0; try { code = p.ExitCode; } catch { }
        bool ok = File.Exists(LastFile) && new FileInfo(LastFile).Length > 0 && (code == 0 || stopping);
        if (!ok)
        {
            try { if (File.Exists(LastFile)) File.Delete(LastFile); } catch { }
            if (err.Length > 600) err = err.Substring(err.Length - 600);
            if (Finished != null) Finished(null, err.Length > 0 ? err : "FFmpeg stopped (exit code " + code + ").");
        }
        else if (Finished != null) Finished(LastFile, null);
        try { p.Dispose(); } catch { }
    }
}

class TrayApp : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly KeyHook hook;
    readonly Control pump = new Control(); // marshals the hook callback onto the UI thread
    readonly Icon cameraIcon, recIcon;
    readonly ToolStripMenuItem stopItem, micMenu;
    RecFrame frame;
    bool busy;

    public TrayApp()
    {
        pump.CreateControl();
        Recorder.Init();
        cameraIcon = MakeIcon();
        recIcon = MakeRecIcon();
        tray = new NotifyIcon { Icon = cameraIcon, Text = "Snip  -  Ctrl + ' + #", Visible = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Snip now", null, (s, e) => Snip(false));
        menu.Items.Add("Record an area", null, (s, e) => Snip(true));
        menu.Items.Add("Record the whole screen", null, (s, e) => StartRecording(SystemInformation.VirtualScreen));
        stopItem = new ToolStripMenuItem("Stop recording  (Alt+S)", null, (s, e) => Recorder.Stop());
        menu.Items.Add(stopItem);
        menu.Items.Add(new ToolStripSeparator());
        micMenu = new ToolStripMenuItem("Microphone");
        menu.Items.Add(micMenu);
        menu.Items.Add("Open the recordings folder", null, (s, e) => Process.Start("explorer.exe", "\"" + Recorder.DownloadsDir() + "\""));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (s, e) => { Recorder.Stop(); tray.Visible = false; Application.Exit(); });
        menu.Items.Add("Uninstall", null, (s, e) => Installer.Uninstall());
        menu.Opening += (s, e) => { stopItem.Visible = Recorder.Active; FillMics(); };
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left && Recorder.Active) Recorder.Stop(); };
        tray.BalloonTipClicked += (s, e) => { if (Recorder.LastFile != null && File.Exists(Recorder.LastFile)) Process.Start("explorer.exe", "/select,\"" + Recorder.LastFile + "\""); };
        Recorder.Finished += OnFinished;
        hook = new KeyHook(() => pump.BeginInvoke(new Action(() => Snip(false))),
                           () => pump.BeginInvoke(new Action(Recorder.Stop)));

        // Windows registers the icon a moment after it first appears
        var promote = new System.Windows.Forms.Timer { Interval = 3000 };
        promote.Tick += (s, e) => { promote.Stop(); try { Installer.PromoteTrayIcon(); } catch { } };
        promote.Start();
    }

    void Snip(bool record)
    {
        if (busy) return;
        busy = true;
        Rectangle? toRecord = null;
        try
        {
            using (var f = new Overlay(record && !Recorder.Active))
            {
                f.ShowDialog();
                toRecord = f.RecordArea;
            }
        }
        finally { busy = false; }
        // the overlay has closed, so its frozen screenshot is not in the video
        if (toRecord.HasValue) StartRecording(toRecord.Value);
    }

    void StartRecording(Rectangle area)
    {
        if (Recorder.Active) return;
        if (!Recorder.Start(area, CurrentMic())) return;
        frame = new RecFrame(area);
        frame.Show();
        tray.Icon = recIcon;
        tray.Text = "Snip  -  recording, Alt+S to stop";
        tray.ShowBalloonTip(3000, "Recording", "Stop with Alt+S, or click the red tray icon.", ToolTipIcon.None);
    }

    void OnFinished(string file, string error)
    {
        if (frame != null) { frame.Close(); frame.Dispose(); frame = null; }
        tray.Icon = cameraIcon;
        tray.Text = "Snip  -  Ctrl + ' + #";
        if (file != null)
        {
            SystemSounds.Asterisk.Play();
            tray.ShowBalloonTip(5000, "Saved to Downloads", Path.GetFileName(file) + "  (click to open)", ToolTipIcon.None);
        }
        else MessageBox.Show("Recording failed.\n\n" + error, "Snip", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // The mic list comes from ffmpeg, so it is only known once ffmpeg is.
    void FillMics()
    {
        micMenu.DropDownItems.Clear();
        if (Ffmpeg.Find() == null)
        {
            micMenu.DropDownItems.Add("(FFmpeg is downloaded on the first recording)").Enabled = false;
            return;
        }
        string chosen = Settings.Get("mic");
        var none = new ToolStripMenuItem("No microphone") { Checked = chosen == "" };
        none.Click += (s, e) => Settings.Set("mic", "");
        micMenu.DropDownItems.Add(none);
        foreach (var m in Ffmpeg.ListMics())
        {
            var mic = m;
            var it = new ToolStripMenuItem(m.Name) { Checked = chosen == null ? micMenu.DropDownItems.Count == 1 : chosen == (m.Alt ?? m.Name) };
            it.Click += (s, e) => Settings.Set("mic", mic.Alt ?? mic.Name);
            micMenu.DropDownItems.Add(it);
        }
        if (micMenu.DropDownItems.Count == 1) micMenu.DropDownItems.Add("(no microphone found)").Enabled = false;
    }

    // Saved choice, else the first mic Windows lists; null means record without sound.
    static Mic CurrentMic()
    {
        string chosen = Settings.Get("mic");
        if (chosen == "") return null;
        var mics = Ffmpeg.ListMics();
        if (mics.Count == 0) return null;
        if (chosen != null)
            foreach (var m in mics) if ((m.Alt ?? m.Name) == chosen) return m;
        return mics[0];
    }

    // Microsoft Fluent 3D camera (MIT), embedded as camera.ico at build time.
    static Icon MakeIcon()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using (var stream = asm.GetManifestResourceStream("camera.ico"))
            return new Icon(stream, SystemInformation.SmallIconSize);
    }

    // A red dot: shown in the tray while recording.
    static Icon MakeRecIcon()
    {
        var sz = SystemInformation.SmallIconSize;
        using (var bmp = new Bitmap(sz.Width, sz.Height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(Color.FromArgb(240, 68, 56))) g.FillEllipse(b, 1, 1, sz.Width - 3, sz.Height - 3);
                using (var p = new Pen(Color.White, 1.5f)) g.DrawEllipse(p, 1, 1, sz.Width - 3, sz.Height - 3);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { hook.Dispose(); tray.Visible = false; tray.Dispose(); pump.Dispose(); }
        base.Dispose(disposing);
    }
}

// Low-level keyboard hook: fires once when Ctrl, ' and # are all down at the same time,
// and Alt+S while a recording runs. Ordinary keys can't be a Windows hotkey, so we watch
// the keyboard ourselves.
class KeyHook : IDisposable
{
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern short VkKeyScan(char c);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);

    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_S = 0x53;
    readonly HookProc proc; // kept alive so the GC can't collect the callback
    IntPtr handle;
    readonly Action fire, stop;
    readonly int vkQuote, vkHash; // resolved from the current keyboard layout
    bool fired;

    // The hook lives on its own thread with its own message loop. Windows drops a
    // low-level hook whose thread is slow to answer, and the UI thread is busy for a
    // moment while it grabs a 4K screenshot - right when the chord keys are released.
    public KeyHook(Action fire, Action stop)
    {
        this.fire = fire;
        this.stop = stop;
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
            else if (vk == VK_S && down && Recorder.Active && IsDown(VK_MENU))
            {
                stop();
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(handle, code, wParam, lParam);
    }

    static bool IsDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
    public void Dispose() { UnhookWindowsHookEx(handle); }
}

// Red border round the area being recorded, with a blinking REC badge and timer. It sits
// just OUTSIDE the box and is click-through, so it never shows up in the video itself.
class RecFrame : Form
{
    const int Border = 4, Pad = 44;
    readonly Rectangle area;      // screen coordinates of what ffmpeg records
    readonly DateTime started = DateTime.Now;
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 500 };
    readonly Font font = new Font("Segoe UI", 10f, FontStyle.Bold);
    bool blink;

    public RecFrame(Rectangle area)
    {
        this.area = area;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(1, 2, 3);
        TransparencyKey = BackColor;   // everything painted in this colour is a hole
        Bounds = new Rectangle(area.X - Border, area.Y - Pad, area.Width + 2 * Border, area.Height + Border + Pad);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        tick.Tick += (s, e) => { blink = !blink; Invalidate(Badge); };
        tick.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x20 | 0x80 | 0x08000000;   // WS_EX_TRANSPARENT (click-through), TOOLWINDOW, NOACTIVATE
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    Rectangle Box { get { return new Rectangle(Border, Pad, area.Width, area.Height); } }
    Rectangle Badge { get { return new Rectangle(Border, Pad - 36, TextRenderer.MeasureText("REC  00:00   Alt+S", font).Width + 44, 30); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle box = Box;
        using (var p = new Pen(Color.FromArgb(240, 68, 56), Border))
        {
            p.Alignment = PenAlignment.Inset;
            g.DrawRectangle(p, new Rectangle(0, Pad - Border, box.Width + 2 * Border, box.Height + 2 * Border));
        }
        Rectangle b = Badge;
        using (var bg = new SolidBrush(Color.FromArgb(29, 29, 27))) g.FillRectangle(bg, b);
        using (var dot = new SolidBrush(blink ? Color.FromArgb(240, 68, 56) : Color.FromArgb(120, 50, 45))) g.FillEllipse(dot, b.X + 10, b.Y + 9, 12, 12);
        TimeSpan t = DateTime.Now - started;
        string text = string.Format("REC  {0}:{1:00}   Alt+S", (int)t.TotalMinutes, t.Seconds);
        TextRenderer.DrawText(g, text, font, new Rectangle(b.X + 28, b.Y, b.Width - 28, b.Height), Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { tick.Dispose(); font.Dispose(); }
        base.Dispose(disposing);
    }
}

// Full-screen frozen, dimmed copy of the desktop. Drag a box; the box is copied bright.
// With Shift held (or opened from "Record an area") the box becomes a recording instead.
class Overlay : Form
{
    readonly Bitmap shot, dimmed;
    readonly bool recordMode;
    Point start;
    Rectangle sel;
    bool dragging;
    public Rectangle? RecordArea;   // set when the user asked for a recording

    public Overlay(bool recordMode)
    {
        this.recordMode = recordMode;
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

    bool Recording { get { return recordMode || (ModifierKeys & Keys.Shift) == Keys.Shift; } }

    string Hint
    {
        get
        {
            if (Recorder.Active) return "Recording in progress  ·  drag a box to copy it  ·  Esc cancels";
            if (Recording) return "Drag a box to record it as a video  ·  Alt+S stops  ·  Esc cancels";
            return "Drag a box to copy it  ·  hold Shift to record a video instead  ·  Esc cancels";
        }
    }

    static readonly Font hintFont = new Font("Segoe UI", 11f, FontStyle.Bold);

    // The hint sits at the top of the primary screen, where the eye goes first, sized to its text.
    Rectangle HintBox
    {
        get
        {
            Rectangle p = Screen.PrimaryScreen.Bounds;
            int w = TextRenderer.MeasureText(Hint, hintFont).Width + 56, h = hintFont.Height + 20;
            return new Rectangle(p.Left - Left + (p.Width - w) / 2, p.Top - Top + 18, w, h);
        }
    }

    // Shift changes the text and so the pill's width: repaint the whole strip it can occupy.
    Rectangle HintBand { get { Rectangle p = Screen.PrimaryScreen.Bounds; return new Rectangle(p.Left - Left, p.Top - Top, p.Width, 100); } }

    protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
        if (e.KeyCode == Keys.ShiftKey) Invalidate(HintBand);
    }
    protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.ShiftKey) Invalidate(HintBand); }

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
        bool record = Recording && !Recorder.Active;
        if (record && sel.Width >= 40 && sel.Height >= 40)
        {
            // screen coordinates: the overlay's origin is the virtual screen's origin
            RecordArea = new Rectangle(sel.X + Left, sel.Y + Top, sel.Width, sel.Height);
        }
        else if (!record && sel.Width > 2 && sel.Height > 2)
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
        if (sel.Width > 0)
        {
            Rectangle part = Rectangle.Intersect(sel, e.ClipRectangle);
            if (!part.IsEmpty) e.Graphics.DrawImage(shot, part, part, GraphicsUnit.Pixel);
            using (var p = new Pen(Recording ? Color.FromArgb(240, 68, 56) : Color.White, Recording ? 2 : 1)) e.Graphics.DrawRectangle(p, sel);
        }
        Rectangle h = HintBox;
        if (!h.IntersectsWith(e.ClipRectangle)) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var path = Rounded(h, h.Height / 2))
        using (var bg = new SolidBrush(Color.FromArgb(230, 29, 29, 27)))
        {
            e.Graphics.FillPath(bg, path);
            TextRenderer.DrawText(e.Graphics, Hint, hintFont, h, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
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
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
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
