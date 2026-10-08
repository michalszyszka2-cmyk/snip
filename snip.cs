// Background snipping tool. Sits in the tray; press Ctrl + ' + # (all held together),
// drag a box, and that part of the screen lands on the clipboard. Hold Shift while you
// drag and the box is recorded as a video (voice, optional webcam bubble, pause). Esc cancels.
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

// key=value lines in settings.txt next to the exe: mic, cam, cam_on, cam_shape.
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
    public static List<Mic> ListMics() { return ListDevices("audio"); }
    public static List<Mic> ListCams() { return ListDevices("video"); }

    public static List<Mic> ListDevices(string kind)
    {
        var mics = new List<Mic>();
        string exe = Find();
        if (exe == null) return mics;
        string err = Run(exe, "-hide_banner -list_devices true -f dshow -i dummy", 8000);
        Mic last = null;
        foreach (Match m in Regex.Matches(err, "\"([^\"]+)\"\\s+\\((audio|video|none)\\)|Alternative name \"([^\"]+)\""))
        {
            if (m.Groups[1].Success) { last = m.Groups[2].Value == kind ? new Mic { Name = m.Groups[1].Value } : null; if (last != null) mics.Add(last); }
            else if (last != null) { last.Alt = m.Groups[3].Value; last = null; }
        }
        return mics;
    }

    public static string Run(string exe, string args, int timeoutMs)
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
// (DirectShow), encodes H.264 + AAC. ffmpeg cannot pause, so each stretch between Pause and
// Resume is its own segment file in %TEMP%; Stop joins them losslessly (concat, -c copy)
// into the mp4 in Downloads. "q" on stdin ends a segment cleanly so it is playable.
static class Recorder
{
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    enum After { None, Pause, Finish, Cancel }
    static Process proc;            // the running segment, null while paused
    static string exe, segFile;
    static Rectangle area;
    static Mic mic;
    static readonly List<string> segs = new List<string>();
    static After after;
    static bool wantResume;
    static DateTime segStart;
    static TimeSpan done;
    public static volatile bool Active;
    public static bool Paused { get; private set; }
    public static string LastFile;
    // (file, null) saved; (null, error) failed; (null, null) discarded. Always on the UI thread.
    public static event Action<string, string> Finished;

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

    // Recorded time so far, not counting pauses.
    public static TimeSpan Elapsed
    {
        get { return done + (proc != null && !Paused && after == After.None ? DateTime.Now - segStart : TimeSpan.Zero); }
    }

    public static bool Start(Rectangle a, Mic m)
    {
        if (Active) return false;
        exe = Ffmpeg.Ensure();
        if (exe == null) return false;
        // encoders want even sizes
        a.Width &= ~1; a.Height &= ~1;
        if (a.Width < 2 || a.Height < 2) return false;
        area = a; mic = m;

        string file = Path.Combine(DownloadsDir(), "recording-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm") + ".mp4");
        for (int n = 2; File.Exists(file); n++)
            file = Path.Combine(DownloadsDir(), "recording-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm") + "-" + n + ".mp4");
        LastFile = file;
        segs.Clear(); done = TimeSpan.Zero; Paused = false; wantResume = false;
        if (!StartSegment()) return false;
        Active = true;
        return true;
    }

    static bool StartSegment()
    {
        segFile = Path.Combine(Path.GetTempPath(), "snip-seg-" + DateTime.Now.Ticks + ".mp4");
        var a = new StringBuilder("-hide_banner -loglevel error -y ");
        a.AppendFormat("-thread_queue_size 1024 -f gdigrab -framerate 30 -offset_x {0} -offset_y {1} -video_size {2}x{3} -draw_mouse 1 -i desktop ",
            area.X, area.Y, area.Width, area.Height);
        if (mic != null)
            a.AppendFormat("-thread_queue_size 1024 -rtbufsize 100M -f dshow -i \"audio={0}\" ", mic.Alt ?? mic.Name);
        a.Append("-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p -r 30 ");
        if (mic != null) a.Append("-c:a aac -b:a 160k -ac 1 ");
        a.Append('"').Append(segFile).Append('"');

        var psi = new ProcessStartInfo(exe, a.ToString())
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardError = true,
        };
        var errors = new StringBuilder();
        after = After.None;
        Process p;
        try { p = Process.Start(psi); }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start FFmpeg:\n\n" + ex.Message, "Snip", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        proc = p;
        segStart = DateTime.Now;
        p.EnableRaisingEvents = true;
        p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
        p.BeginErrorReadLine();
        p.Exited += (s, e) => ui.BeginInvoke(new Action(() => OnSegExit(p, errors)));
        return true;
    }

    // ffmpeg finishes the file after "q"; only kill it if it hangs
    static void Quit(Process p)
    {
        try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch { }
        var t = new System.Windows.Forms.Timer { Interval = 15000 };
        t.Tick += (s, e) => { t.Stop(); t.Dispose(); try { if (!p.HasExited) p.Kill(); } catch { } };
        t.Start();
    }

    public static void Pause()
    {
        if (!Active || Paused || proc == null || after != After.None) return;
        done += DateTime.Now - segStart;
        Paused = true; after = After.Pause;
        Quit(proc);
    }

    public static void Resume()
    {
        if (!Active || !Paused) return;
        Paused = false;
        if (proc != null) { wantResume = true; return; }   // the paused segment is still closing
        if (!StartSegment()) Done(null, "Could not resume the recording.");
    }

    public static void TogglePause() { if (Paused) Resume(); else Pause(); }

    public static void Stop()
    {
        if (!Active) return;
        wantResume = false;
        if (proc == null) { Finish(); return; }
        if (after == After.Finish || after == After.Cancel) return;
        if (after == After.None) { done += DateTime.Now - segStart; Quit(proc); }
        after = After.Finish;   // a pause already sent "q": just finish when it exits
    }

    // Throw the recording away (the X, or Start over).
    public static void Cancel()
    {
        if (!Active) return;
        wantResume = false;
        if (proc != null) { after = After.Cancel; try { proc.Kill(); } catch { } }
        else Done(null, null);
    }

    static void OnSegExit(Process p, StringBuilder errors)
    {
        if (p != proc) return;
        proc = null;
        int code = 0; try { code = p.ExitCode; } catch { }
        try { p.Dispose(); } catch { }
        After a = after;
        after = After.None;
        if (a == After.Cancel) { Done(null, null); return; }
        bool ok = File.Exists(segFile) && new FileInfo(segFile).Length > 0 && (code == 0 || a != After.None);
        if (!ok)
        {
            string err; lock (errors) err = errors.ToString().Trim();
            if (err.Length > 600) err = err.Substring(err.Length - 600);
            Done(null, err.Length > 0 ? err : "FFmpeg stopped (exit code " + code + ").");
            return;
        }
        segs.Add(segFile);
        if (a != After.Pause) { Finish(); return; }
        if (wantResume)
        {
            wantResume = false;
            if (!StartSegment()) Done(null, "Could not resume the recording.");
        }
    }

    // Join the segments into the final file; -c copy, so it takes a moment, not a re-encode.
    static void Finish()
    {
        if (segs.Count == 0) { Done(null, "Nothing was recorded."); return; }
        string list = Path.Combine(Path.GetTempPath(), "snip-concat-" + DateTime.Now.Ticks + ".txt");
        var lines = new List<string>();
        foreach (var f in segs) lines.Add("file '" + f.Replace("\\", "/").Replace("'", "'\\''") + "'");
        File.WriteAllLines(list, lines.ToArray());
        string err = Ffmpeg.Run(exe, "-hide_banner -loglevel error -y -f concat -safe 0 -i \"" + list + "\" -c copy -movflags +faststart \"" + LastFile + "\"", 300000);
        try { File.Delete(list); } catch { }
        if (File.Exists(LastFile) && new FileInfo(LastFile).Length > 0) Done(LastFile, null);
        else Done(null, "Could not join the recording.\n\n" + err.Trim());
    }

    static void Done(string file, string error)
    {
        foreach (var f in segs) try { File.Delete(f); } catch { }
        segs.Clear();
        try { if (segFile != null && File.Exists(segFile)) File.Delete(segFile); } catch { }
        if (file == null) try { if (LastFile != null && File.Exists(LastFile) && error != null) File.Delete(LastFile); } catch { }
        Active = false; Paused = false; after = After.None; wantResume = false;
        if (Finished != null) Finished(file, error);
    }
}

static class Dpi
{
    public static float Scale { get { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; } }
}

class TrayApp : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly KeyHook hook;
    readonly Control pump = new Control(); // marshals the hook callback onto the UI thread
    readonly Icon cameraIcon, recIcon;
    readonly ToolStripMenuItem stopItem, pauseItem, micMenu, camMenu;
    RecFrame frame;
    CamBubble bubble;
    Countdown countdown;
    Rectangle armedArea;
    bool busy, restarting;

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
        menu.Items.Add("Record the whole screen", null, (s, e) => RecordWholeScreen());
        menu.Items.Add("Record the camera only", null, (s, e) => ArmRecording(Rectangle.Empty, true));
        pauseItem = new ToolStripMenuItem("Pause recording", null, (s, e) => TogglePause());
        menu.Items.Add(pauseItem);
        stopItem = new ToolStripMenuItem("Stop recording  (Alt+S)", null, (s, e) => StopPressed());
        menu.Items.Add(stopItem);
        menu.Items.Add(new ToolStripSeparator());
        camMenu = new ToolStripMenuItem("Camera");
        menu.Items.Add(camMenu);
        micMenu = new ToolStripMenuItem("Microphone");
        menu.Items.Add(micMenu);
        menu.Items.Add("Open the recordings folder", null, (s, e) => Process.Start("explorer.exe", "\"" + Recorder.DownloadsDir() + "\""));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (s, e) => { Recorder.Stop(); CloseBubble(); tray.Visible = false; Application.Exit(); });
        menu.Items.Add("Uninstall", null, (s, e) => Installer.Uninstall());
        menu.Opening += (s, e) =>
        {
            stopItem.Visible = pauseItem.Visible = Recorder.Active;
            pauseItem.Text = Recorder.Paused ? "Resume recording" : "Pause recording";
            FillMics(); FillCams();
        };
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left && Recorder.Active) StopPressed(); };
        tray.BalloonTipClicked += (s, e) => { if (Recorder.LastFile != null && File.Exists(Recorder.LastFile)) Process.Start("explorer.exe", "/select,\"" + Recorder.LastFile + "\""); };
        Recorder.Finished += OnFinished;
        hook = new KeyHook(() => pump.BeginInvoke(new Action(() => Snip(false))),
                           () => pump.BeginInvoke(new Action(StopPressed)));

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
        if (toRecord.HasValue) ArmRecording(toRecord.Value, false);
    }

    // Show the frame with its bar; recording starts on Record, after a 3-2-1 countdown.
    // camOnly: the camera picture is put mid-screen and the frame goes round it.
    void ArmRecording(Rectangle area, bool camOnly)
    {
        if (Recorder.Active || frame != null || bubble != null) return;
        if (camOnly)
        {
            if (Ffmpeg.Ensure() == null) return;
            string cam = CurrentCam();
            if (cam == null) { MessageBox.Show("No camera found. Pick one under Camera in the tray menu.", "Snip", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            bubble = new CamBubble(cam, false);
            var wa = Screen.PrimaryScreen.WorkingArea;
            bubble.Location = new Point(wa.X + (wa.Width - bubble.Width) / 2, wa.Y + (wa.Height - bubble.Height) / 2 + 32);
            ShowBubble();
            area = bubble.Bounds;
        }
        else if (CamWanted()) OpenBubble(area);
        armedArea = area;
        frame = new RecFrame(area, !camOnly && CurrentCam() != null, bubble != null,
            MainPressed, TogglePause, Restart, ToggleCam, ClosePressed);
        frame.Show();
    }

    void RecordWholeScreen()
    {
        if (Recorder.Active || frame != null) return;
        if (CamWanted()) OpenBubble(Screen.PrimaryScreen.WorkingArea);
        if (!StartRecording(SystemInformation.VirtualScreen)) CloseBubble();
    }

    void MainPressed()
    {
        if (frame == null) return;
        switch (frame.State)
        {
            case RecFrame.Mode.Ready:
                frame.State = RecFrame.Mode.Counting;
                countdown = new Countdown(armedArea, () =>
                {
                    countdown = null;
                    // a beat after the countdown window has gone, so it is not in the first frame
                    var t = new System.Windows.Forms.Timer { Interval = 150 };
                    t.Tick += (s, e) =>
                    {
                        t.Stop(); t.Dispose();
                        if (frame == null || frame.State != RecFrame.Mode.Counting) return;
                        frame.State = StartRecording(armedArea) ? RecFrame.Mode.Recording : RecFrame.Mode.Ready;
                    };
                    t.Start();
                });
                countdown.Show();
                break;
            case RecFrame.Mode.Counting:
                AbortCountdown();
                frame.State = RecFrame.Mode.Ready;
                break;
            case RecFrame.Mode.Recording:
            case RecFrame.Mode.Paused:
                StopPressed();
                break;
        }
    }

    void StopPressed()
    {
        if (!Recorder.Active) return;
        if (frame != null) frame.State = RecFrame.Mode.Saving;
        Recorder.Stop();
    }

    void TogglePause()
    {
        if (!Recorder.Active) return;
        Recorder.TogglePause();
        if (frame != null) frame.State = Recorder.Paused ? RecFrame.Mode.Paused : RecFrame.Mode.Recording;
        tray.Text = Recorder.Paused ? "Snip  -  paused" : "Snip  -  recording, Alt+S to stop";
    }

    void Restart()
    {
        if (!Recorder.Active) return;
        restarting = true;
        Recorder.Cancel();
    }

    void ClosePressed()
    {
        if (frame == null) return;
        switch (frame.State)
        {
            case RecFrame.Mode.Recording:
            case RecFrame.Mode.Paused:
                if (MessageBox.Show(frame, "Throw this recording away?", "Snip", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    Recorder.Cancel();
                break;
            case RecFrame.Mode.Saving: break;
            default: CloseAll(); break;
        }
    }

    void ToggleCam()
    {
        if (frame == null) return;
        if (bubble != null) { CloseBubble(); Settings.Set("cam_on", "0"); }
        else { OpenBubble(armedArea); Settings.Set("cam_on", "1"); }
        frame.CamOn = bubble != null;
    }

    // Camera picture (circle, rounded square or 16:9) in the bottom-left corner of what is being recorded.
    void OpenBubble(Rectangle area)
    {
        string cam = CurrentCam();
        if (cam == null) return;
        bubble = new CamBubble(cam, true);
        int m = (int)(20 * Dpi.Scale);
        int y = Math.Max(area.Y, area.Bottom - m - bubble.Height);
        bubble.Location = new Point(area.X + m, y);
        ShowBubble();
    }

    void ShowBubble()
    {
        var b = bubble;
        b.Failed += () =>
        {
            bool camOnly = !b.Overlay;
            CloseBubble();
            if (frame != null) frame.CamOn = false;
            if (camOnly) { if (Recorder.Active) Recorder.Cancel(); else CloseAll(); }
            MessageBox.Show("Could not open the camera. Another app may be using it.", "Snip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        b.Show();
        b.StartFeed();
    }

    void CloseBubble()
    {
        if (bubble == null) return;
        bubble.Close(); bubble.Dispose(); bubble = null;
    }

    void AbortCountdown()
    {
        if (countdown == null) return;
        countdown.Abort(); countdown = null;
    }

    void CloseFrame()
    {
        if (frame == null) return;
        frame.Close(); frame.Dispose(); frame = null;
    }

    void CloseAll() { AbortCountdown(); CloseFrame(); CloseBubble(); }

    bool StartRecording(Rectangle area)
    {
        if (Recorder.Active) return false;
        if (!Recorder.Start(area, CurrentMic())) return false;
        tray.Icon = recIcon;
        tray.Text = "Snip  -  recording, Alt+S to stop";
        return true;
    }

    void OnFinished(string file, string error)
    {
        tray.Icon = cameraIcon;
        tray.Text = "Snip  -  Ctrl + ' + #";
        if (restarting && file == null && error == null)
        {
            restarting = false;
            if (frame != null) frame.State = RecFrame.Mode.Ready;
            return;
        }
        restarting = false;
        CloseAll();
        if (file != null)
        {
            SystemSounds.Asterisk.Play();
            tray.ShowBalloonTip(5000, "Saved to Downloads", Path.GetFileName(file), ToolTipIcon.None);
            Process.Start("explorer.exe", "/select,\"" + file + "\"");   // open Downloads with the new file selected
        }
        else if (error != null) MessageBox.Show("Recording failed.\n\n" + error, "Snip", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    void FillCams()
    {
        camMenu.DropDownItems.Clear();
        if (Ffmpeg.Find() == null)
        {
            camMenu.DropDownItems.Add("(FFmpeg is downloaded on the first recording)").Enabled = false;
            return;
        }
        string chosen = Settings.Get("cam");
        var none = new ToolStripMenuItem("No camera") { Checked = chosen == "" };
        none.Click += (s, e) => Settings.Set("cam", "");
        camMenu.DropDownItems.Add(none);
        foreach (var c in Ffmpeg.ListCams())
        {
            var cam = c;
            var it = new ToolStripMenuItem(c.Name) { Checked = chosen == null ? camMenu.DropDownItems.Count == 1 : chosen == (c.Alt ?? c.Name) };
            it.Click += (s, e) => { Settings.Set("cam", cam.Alt ?? cam.Name); Settings.Set("cam_on", "1"); };
            camMenu.DropDownItems.Add(it);
        }
        if (camMenu.DropDownItems.Count == 1) camMenu.DropDownItems.Add("(no camera found)").Enabled = false;
        // the shape of the camera on screen recordings (right-clicking the camera changes it too)
        camMenu.DropDownItems.Add(new ToolStripSeparator());
        string shape = CamBubble.SavedShape();
        for (int i = 0; i < CamBubble.Shapes.Length; i++)
        {
            string sh = CamBubble.Shapes[i];
            var it = new ToolStripMenuItem(CamBubble.ShapeNames[i]) { Checked = sh == shape };
            it.Click += (s, e) => { if (bubble != null && bubble.Overlay) bubble.SetShape(sh); else Settings.Set("cam_shape", sh); };
            camMenu.DropDownItems.Add(it);
        }
    }

    // Saved choice, else the first camera; null = none chosen or none plugged in.
    static string CurrentCam()
    {
        string chosen = Settings.Get("cam");
        if (chosen == "" || Ffmpeg.Find() == null) return null;
        var cams = Ffmpeg.ListCams();
        if (cams.Count == 0) return null;
        if (chosen != null)
            foreach (var c in cams) if ((c.Alt ?? c.Name) == chosen) return chosen;
        return cams[0].Alt ?? cams[0].Name;
    }

    // Show the camera on screen recordings unless it was switched off on the bar last time.
    static bool CamWanted() { return Settings.Get("cam_on") != "0" && CurrentCam() != null; }

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

// Red frame round the chosen area with a floating pill above it, in the style of the
// Windows Snipping Tool's bar: one round button that is Record and, once recording, Stop
// in the same place; a timer; then camera, pause and start-over icons and an X.
// Owner-drawn. The window region is only the pill and the border ring, so the inside is a
// real hole and nothing of the frame is in the video.
class RecFrame : Form
{
    public enum Mode { Ready, Counting, Recording, Paused, Saving }
    const int Border = 4, Strip = 64, PillH = 52;   // Strip = pill height + gap above the box
    const int MainId = 1, CloseId = 2, CamId = 3, PauseId = 4, RestartId = 5;
    readonly Rectangle area;      // screen coordinates of what ffmpeg records
    readonly bool hasCam;
    readonly Action onMain, onPause, onRestart, onCam, onClose;
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 500 };
    readonly Font font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
    readonly Font timeFont = new Font("Consolas", 12f, FontStyle.Bold);
    readonly ToolTip tip = new ToolTip { ShowAlways = true };
    Mode mode = Mode.Ready;
    bool camOn, blink, pressed;
    int hover;
    Rectangle pill, mainBtn, timeBox, camBtn, pauseBtn, restartBtn, closeBtn;
    int div1, div2;

    static readonly Color Red = Color.FromArgb(232, 52, 46), RedHover = Color.FromArgb(250, 76, 68), RedDark = Color.FromArgb(180, 36, 32);
    static readonly Color Amber = Color.FromArgb(255, 184, 77);
    static readonly Color Ink = Color.FromArgb(32, 32, 30), Edge = Color.FromArgb(70, 70, 66), Muted = Color.FromArgb(170, 170, 164);
    static readonly Color Glyph = Color.FromArgb(225, 225, 220), Dim = Color.FromArgb(92, 92, 88);

    public Mode State { get { return mode; } set { mode = value; Invalidate(pill); } }
    public bool CamOn { get { return camOn; } set { camOn = value; Invalidate(pill); } }

    public RecFrame(Rectangle area, bool hasCam, bool camOn, Action onMain, Action onPause, Action onRestart, Action onCam, Action onClose)
    {
        this.area = area; this.hasCam = hasCam; this.camOn = camOn;
        this.onMain = onMain; this.onPause = onPause; this.onRestart = onRestart; this.onCam = onCam; this.onClose = onClose;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Ink;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        // pill: [ (o) Record ] 0:00 | cam pause restart | x   - measured, so 200 % DPI clips nothing
        int labelW = Math.Max(TextRenderer.MeasureText("Stop & save", font).Width, TextRenderer.MeasureText("Starting...", font).Width);
        int timeW = TextRenderer.MeasureText("00:00", timeFont).Width + 16;
        int b = PillH - 12;
        int x = 6;
        mainBtn = new Rectangle(x, 6, 8 + (b - 16) + 8 + labelW + 16, b); x = mainBtn.Right + 12;
        timeBox = new Rectangle(x, 0, timeW, PillH); x = timeBox.Right + 4;
        div1 = x; x += 9;
        if (hasCam) { camBtn = new Rectangle(x, 6, b, b); x = camBtn.Right + 2; }
        pauseBtn = new Rectangle(x, 6, b, b); x = pauseBtn.Right + 2;
        restartBtn = new Rectangle(x, 6, b, b); x = restartBtn.Right + 4;
        div2 = x; x += 9;
        closeBtn = new Rectangle(x, 6, b, b); x = closeBtn.Right + 6;
        int pillW = x;

        int ringW = area.Width + 2 * Border;
        int pillX = Math.Max(0, (ringW - pillW) / 2);          // centred over the box
        mainBtn.Offset(pillX, 0); timeBox.Offset(pillX, 0); camBtn.Offset(pillX, 0); pauseBtn.Offset(pillX, 0);
        restartBtn.Offset(pillX, 0); closeBtn.Offset(pillX, 0); div1 += pillX; div2 += pillX;
        pill = new Rectangle(pillX, 0, pillW, PillH);
        int formW = Math.Max(ringW, pillX + pillW);
        Bounds = new Rectangle(area.X - Border, area.Y - Strip, formW, area.Height + Border + Strip);

        var path = new GraphicsPath(FillMode.Alternate);
        path.AddPath(Rounded(pill, PillH / 2), false);
        path.AddRectangle(new Rectangle(0, Strip - Border, ringW, area.Height + 2 * Border));
        path.AddRectangle(new Rectangle(Border, Strip, area.Width, area.Height));
        Region = new Region(path);

        tick.Tick += (s, e) => { blink = !blink; Invalidate(pill); };
        tick.Start();
    }

    internal static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath(); int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; }   // TOOLWINDOW, NOACTIVATE
    }
    protected override bool ShowWithoutActivation { get { return true; } }

    bool Live { get { return mode == Mode.Recording || mode == Mode.Paused; } }

    int Hit(Point p)
    {
        if (mode == Mode.Saving) return 0;
        if (mainBtn.Contains(p)) return MainId;
        if (closeBtn.Contains(p)) return CloseId;
        if (hasCam && camBtn.Contains(p)) return CamId;
        if (Live && pauseBtn.Contains(p)) return PauseId;
        if (Live && restartBtn.Contains(p)) return RestartId;
        return 0;
    }

    Rectangle RectOf(int id)
    {
        switch (id) { case MainId: return mainBtn; case CloseId: return closeBtn; case CamId: return camBtn; case PauseId: return pauseBtn; default: return restartBtn; }
    }

    string TipOf(int id)
    {
        switch (id)
        {
            case MainId: return mode == Mode.Ready ? "Start recording (3, 2, 1)" : mode == Mode.Counting ? "Cancel the countdown" : "Stop and save to Downloads  (Alt+S)";
            case CloseId: return Live ? "Throw this recording away" : "Close";
            case CamId: return camOn ? "Hide the camera" : "Show the camera";
            case PauseId: return mode == Mode.Paused ? "Resume" : "Pause";
            case RestartId: return "Start over";
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = Hit(e.Location);
        Cursor = h != 0 ? Cursors.Hand : Cursors.Default;
        if (h == hover) return;
        hover = h; Invalidate(pill);
        tip.Hide(this);
        if (h != 0) tip.Show(TipOf(h), this, RectOf(h).X, -34, 2500);   // above the bar, never over the recording
    }
    protected override void OnMouseLeave(EventArgs e) { tip.Hide(this); if (hover != 0 || pressed) { hover = 0; pressed = false; Invalidate(pill); } }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left && Hit(e.Location) != 0) { pressed = true; Invalidate(pill); } }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        bool was = pressed; pressed = false; Invalidate(pill);
        if (!was) return;
        tip.Hide(this);
        switch (Hit(e.Location))
        {
            case MainId: onMain(); break;
            case CloseId: onClose(); break;
            case CamId: onCam(); break;
            case PauseId: onPause(); break;
            case RestartId: onRestart(); break;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        const TextFormatFlags tf = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        // border ring round the recorded box
        using (var p = new Pen(Red, Border))
        {
            p.Alignment = PenAlignment.Inset;
            g.DrawRectangle(p, new Rectangle(0, Strip - Border, area.Width + 2 * Border, area.Height + 2 * Border));
        }

        // the pill
        using (var path = Rounded(pill, PillH / 2))
        using (var fill = new SolidBrush(Ink)) g.FillPath(fill, path);
        var inner = pill; inner.Inflate(-1, -1);
        using (var ip = Rounded(inner, PillH / 2 - 1))
        using (var edge = new Pen(Edge, 1)) g.DrawPath(edge, ip);

        // main button: (o) Record -> Starting... -> [square] Stop & save -> Saving...
        bool mainHot = hover == MainId;
        Color back = mode == Mode.Saving ? RedDark : pressed && mainHot ? RedDark : mainHot ? RedHover : Red;
        using (var path = Rounded(mainBtn, mainBtn.Height / 2))
        using (var br = new SolidBrush(back)) g.FillPath(br, path);
        int ic = mainBtn.Height - 16;
        var icon = new Rectangle(mainBtn.X + 8, mainBtn.Y + 8, ic, ic);
        using (var white = new SolidBrush(Color.White))
        {
            if (mode == Mode.Ready || mode == Mode.Counting)
            {
                g.FillEllipse(white, icon);
                using (var rp = new SolidBrush(back)) g.FillEllipse(rp, Rectangle.Inflate(icon, -3, -3));
                g.FillEllipse(white, Rectangle.Inflate(icon, -6, -6));
            }
            else using (var sq = Rounded(Rectangle.Inflate(icon, -3, -3), 3)) g.FillPath(white, sq);
        }
        string label = mode == Mode.Ready ? "Record" : mode == Mode.Counting ? "Starting..." : mode == Mode.Saving ? "Saving..." : "Stop & save";
        TextRenderer.DrawText(g, label, font, new Rectangle(icon.Right + 8, mainBtn.Y, mainBtn.Right - icon.Right - 8, mainBtn.Height), Color.White, tf);

        // timer: blinking red dot while recording, steady amber while paused
        var timeRect = timeBox;
        Color tc = Muted;
        if (Live || mode == Mode.Saving)
        {
            Color dot = mode == Mode.Paused ? Amber : blink || mode == Mode.Saving ? Red : RedDark;
            using (var br = new SolidBrush(dot)) g.FillEllipse(br, timeRect.X, PillH / 2 - 4, 8, 8);
            tc = mode == Mode.Paused ? Amber : Color.White;
        }
        timeRect.X += 14; timeRect.Width -= 14;
        TimeSpan t = Recorder.Elapsed;
        string time = Live || mode == Mode.Saving ? string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds) : "0:00";
        TextRenderer.DrawText(g, time, timeFont, timeRect, tc, tf);

        using (var dp = new Pen(Edge, 1))
        {
            g.DrawLine(dp, div1, 14, div1, PillH - 14);
            g.DrawLine(dp, div2, 14, div2, PillH - 14);
        }

        if (hasCam) DrawCam(g, camBtn, GlyphColor(CamId, mode != Mode.Saving));
        Color pc = GlyphColor(PauseId, Live);
        Backdrop(g, pauseBtn, PauseId, Live);
        DrawPause(g, pauseBtn, pc, mode == Mode.Paused);
        Backdrop(g, restartBtn, RestartId, Live);
        DrawRestart(g, restartBtn, GlyphColor(RestartId, Live));
        Backdrop(g, closeBtn, CloseId, mode != Mode.Saving);
        DrawX(g, closeBtn, GlyphColor(CloseId, mode != Mode.Saving));
    }

    Color GlyphColor(int id, bool enabled) { return !enabled ? Dim : hover == id ? Color.White : Glyph; }

    void Backdrop(Graphics g, Rectangle r, int id, bool enabled)
    {
        if (!enabled || hover != id) return;
        using (var br = new SolidBrush(pressed ? Color.FromArgb(80, 80, 76) : Color.FromArgb(58, 58, 55))) g.FillEllipse(br, r);
    }

    void DrawCam(Graphics g, Rectangle r, Color c)
    {
        Backdrop(g, r, CamId, mode != Mode.Saving);
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        using (var br = new SolidBrush(c))
        {
            using (var body = Rounded(new Rectangle(cx - 10, cy - 6, 14, 12), 3)) g.FillPath(br, body);
            g.FillPolygon(br, new[] { new Point(cx + 5, cy - 2), new Point(cx + 10, cy - 6), new Point(cx + 10, cy + 6), new Point(cx + 5, cy + 2) });
        }
        if (!camOn)
        {
            using (var cut = new Pen(Ink, 5)) g.DrawLine(cut, cx - 10, cy - 10, cx + 10, cy + 10);
            using (var ln = new Pen(c, 2)) g.DrawLine(ln, cx - 10, cy - 10, cx + 10, cy + 10);
        }
    }

    static void DrawPause(Graphics g, Rectangle r, Color c, bool paused)
    {
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        using (var br = new SolidBrush(c))
        {
            if (paused) g.FillPolygon(br, new[] { new Point(cx - 5, cy - 8), new Point(cx + 8, cy), new Point(cx - 5, cy + 8) });
            else
            {
                using (var a = Rounded(new Rectangle(cx - 7, cy - 8, 5, 16), 1)) g.FillPath(br, a);
                using (var b = Rounded(new Rectangle(cx + 2, cy - 8, 5, 16), 1)) g.FillPath(br, b);
            }
        }
    }

    static void DrawRestart(Graphics g, Rectangle r, Color c)
    {
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        using (var p = new Pen(c, 2.2f)) g.DrawArc(p, cx - 8, cy - 8, 16, 16, 60, 300);
        using (var br = new SolidBrush(c))
            g.FillPolygon(br, new[] { new Point(cx + 3, cy - 2), new Point(cx + 13, cy - 2), new Point(cx + 8, cy + 4) });
    }

    static void DrawX(Graphics g, Rectangle r, Color c)
    {
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, k = 5;
        using (var p = new Pen(c, 2f)) { g.DrawLine(p, cx - k, cy - k, cx + k, cy + k); g.DrawLine(p, cx - k, cy + k, cx + k, cy - k); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { tick.Dispose(); tip.Dispose(); font.Dispose(); timeFont.Dispose(); }
        base.Dispose(disposing);
    }
}

// Webcam picture in a borderless always-on-top window: round (on top of a screen recording,
// like Loom's bubble; drag it anywhere, double-click to change its size) or a plain 16:9
// rectangle (camera-only recording). Being an ordinary window on screen, it is simply part of
// what gdigrab records. The picture comes from a second ffmpeg as raw BGRA frames on stdout,
// mirrored like a mirror; the window is layered so the circle has smooth edges.
class CamBubble : Form
{
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    // Overlay = the camera on top of a screen recording; otherwise it is the whole recording (camera only).
    public readonly bool Overlay;
    public static readonly string[] Shapes = { "circle", "square", "wide" };
    public static readonly string[] ShapeNames = { "Circle", "Rounded square", "Rounded rectangle (16:9)" };
    public static string SavedShape() { string v = Settings.Get("cam_shape"); return Array.IndexOf(Shapes, v) >= 0 ? v : "circle"; }
    string shape;
    public event Action Failed;           // the camera could not be opened
    readonly string device;
    readonly int[] sizes;
    int sizeIx = 1;
    int fw, fh;                            // picture size = window size, in pixels
    Process proc;
    volatile bool painting;
    Point grab; bool dragging;

    public CamBubble(string device, bool overlay)
    {
        this.device = device; Overlay = overlay; shape = SavedShape();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        float k = Dpi.Scale;
        sizes = new[] { (int)(150 * k), (int)(220 * k), (int)(320 * k) };
        SetPictureSize();
        if (Overlay)
        {
            var menu = new ContextMenuStrip();
            for (int i = 0; i < Shapes.Length; i++)
            {
                string sh = Shapes[i];
                var it = new ToolStripMenuItem(ShapeNames[i]);
                it.Click += (s, e) => SetShape(sh);
                menu.Items.Add(it);
            }
            menu.Opening += (s, e) => { for (int i = 0; i < Shapes.Length; i++) ((ToolStripMenuItem)menu.Items[i]).Checked = Shapes[i] == shape; };
            ContextMenuStrip = menu;
        }
    }

    // Change the shape in place, keeping the centre; remembered for next time.
    public void SetShape(string s)
    {
        Settings.Set("cam_shape", s);
        if (!Overlay || s == shape) return;
        shape = s;
        var c = new Point(Left + Width / 2, Top + Height / 2);
        SetPictureSize();
        Location = new Point(c.X - fw / 2, c.Y - fh / 2);
        Present(null);
        StartFeed();
    }

    void SetPictureSize()
    {
        if (Overlay && shape == "wide") { fh = (sizes[sizeIx] * 3 / 4) & ~1; fw = (fh * 16 / 9) & ~1; }
        else if (Overlay) fw = fh = sizes[sizeIx] & ~1;
        else
        {
            fw = Math.Min(1280, (int)(Screen.PrimaryScreen.WorkingArea.Width * 0.6)) & ~1;
            fh = (fw * 9 / 16) & ~1;
        }
        Size = new Size(fw, fh);
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80 | 0x08000000 | 0x8; return cp; }   // LAYERED, TOOLWINDOW, NOACTIVATE, TOPMOST
    }
    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void OnShown(EventArgs e) { base.OnShown(e); Present(null); }

    public void StartFeed() { StopFeed(); Launch(true); }

    void StopFeed()
    {
        var p = proc; proc = null;
        if (p != null) try { if (!p.HasExited) p.Kill(); } catch { }
    }

    // hd: ask for 1280x720 MJPEG (sharp, 30 fps on most webcams); if the camera refuses, retry with its default.
    void Launch(bool hd)
    {
        string exe = Ffmpeg.Find();
        if (exe == null) { Fail(); return; }
        string crop = Overlay && shape != "wide" ? "crop=w='min(iw,ih)':h='min(iw,ih)'" : "crop=w='min(iw,ih*16/9)':h='min(ih,iw*9/16)'";
        string args = "-hide_banner -loglevel error -f dshow -rtbufsize 100M " +
            (hd ? "-vcodec mjpeg -video_size 1280x720 -framerate 30 " : "") +
            "-i \"video=" + device + "\" -vf \"" + crop + ",scale=" + fw + ":" + fh + ",hflip\" -pix_fmt bgra -f rawvideo pipe:1";
        var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        Process p;
        try { p = Process.Start(psi); } catch { Fail(); return; }
        proc = p;
        p.ErrorDataReceived += (s, e) => { };
        p.BeginErrorReadLine();
        int w = fw, h = fh;
        new Thread(() => Read(p, w, h, hd)) { IsBackground = true }.Start();
    }

    void Read(Process p, int w, int h, bool hd)
    {
        int len = w * h * 4, frames = 0;
        var buf = new byte[len];
        try
        {
            var stream = p.StandardOutput.BaseStream;
            while (true)
            {
                int got = 0;
                while (got < len) { int n = stream.Read(buf, got, len - got); if (n <= 0) break; got += n; }
                if (got < len) break;
                frames++;
                if (painting) continue;               // the window is still busy with the last one
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                Marshal.Copy(buf, 0, d.Scan0, len);
                bmp.UnlockBits(d);
                painting = true;
                try { BeginInvoke(new Action(() => { try { if (p == proc && w == fw && h == fh) Present(bmp); } finally { bmp.Dispose(); painting = false; } })); }
                catch { bmp.Dispose(); return; }
            }
        }
        catch { }
        if (frames == 0)
            try { BeginInvoke(new Action(() => { if (p != proc) return; proc = null; if (hd) Launch(false); else Fail(); })); } catch { }
    }

    void Fail() { StopFeed(); if (Failed != null) Failed(); }

    GraphicsPath Shape(Rectangle r)
    {
        var p = new GraphicsPath();
        if (!Overlay) p.AddRectangle(r);
        else if (shape == "circle") p.AddEllipse(r);
        else return RecFrame.Rounded(r, Math.Min(fw, fh) / 8);
        return p;
    }

    // Compose picture + ring into a 32-bit bitmap and hand it to the layered window.
    void Present(Bitmap pic)
    {
        if (IsDisposed || !IsHandleCreated) return;
        using (var canvas = new Bitmap(fw, fh, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                var r = new Rectangle(0, 0, fw - 1, fh - 1);
                using (var path = Shape(r))
                {
                    if (pic != null) using (var tb = new TextureBrush(pic)) g.FillPath(tb, path);
                    else using (var b = new SolidBrush(Color.FromArgb(48, 48, 45))) g.FillPath(b, path);
                }
                if (pic == null)
                    TextRenderer.DrawText(g, "Camera...", new Font("Segoe UI", 10f), new Rectangle(0, 0, fw, fh), Color.FromArgb(190, 190, 184),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (Overlay && shape == "circle")
                {
                    float ring = 3 * Dpi.Scale;
                    using (var pen = new Pen(Color.FromArgb(235, 255, 255, 255), ring))
                        g.DrawEllipse(pen, ring / 2, ring / 2, fw - 1 - ring, fh - 1 - ring);
                }
            }
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = canvas.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hb);
            var size = canvas.Size; var src = Point.Empty; var top = Location;
            var bf = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screen, ref top, ref size, mem, ref src, 0, ref bf, 2);
            SelectObject(mem, old); DeleteObject(hb); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Overlay && e.Button == MouseButtons.Left) { dragging = true; grab = e.Location; Cursor = Cursors.SizeAll; }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!Overlay) return;
        Cursor = Cursors.SizeAll;
        if (dragging) { var c = Control.MousePosition; Location = new Point(c.X - grab.X, c.Y - grab.Y); }
    }
    protected override void OnMouseUp(MouseEventArgs e) { dragging = false; }

    // small -> medium -> large, keeping its centre where it is
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (!Overlay) return;
        var c = new Point(Left + Width / 2, Top + Height / 2);
        sizeIx = (sizeIx + 1) % sizes.Length;
        SetPictureSize();
        Location = new Point(c.X - fw / 2, c.Y - fh / 2);
        Present(null);
        StartFeed();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) StopFeed();
        base.Dispose(disposing);
    }
}

// 3, 2, 1 in a dark circle over the middle of the box, before recording starts.
class Countdown : Form
{
    readonly System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 800 };
    readonly Font f;
    int n = 3;

    public Countdown(Rectangle area, Action done)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(32, 32, 30);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        int d = (int)(140 * Dpi.Scale);
        Bounds = new Rectangle(area.X + (area.Width - d) / 2, area.Y + (area.Height - d) / 2, d, d);
        var p = new GraphicsPath(); p.AddEllipse(0, 0, d, d);
        Region = new Region(p);
        f = new Font("Segoe UI", d * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel);
        t.Tick += (s, e) =>
        {
            if (--n > 0) { Invalidate(); return; }
            t.Stop(); Close(); done();
        };
        Shown += (s, e) => t.Start();
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; }   // TOOLWINDOW, NOACTIVATE
    }
    protected override bool ShowWithoutActivation { get { return true; } }

    public void Abort() { t.Stop(); Close(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = 5 * Dpi.Scale;
        using (var pen = new Pen(Color.FromArgb(232, 52, 46), w)) g.DrawEllipse(pen, w / 2, w / 2, Width - w - 1, Height - w - 1);
        TextRenderer.DrawText(g, n.ToString(), f, ClientRectangle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { t.Dispose(); f.Dispose(); }
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
            if (Recording) return "Drag a box to record  ·  then press Record above it  ·  Esc cancels";
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
