// Background snipping tool. Sits in the tray; press Ctrl + ' + # (all held together),
// drag a box, and that part of the screen lands on the clipboard. Hold Ctrl while you
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
    static void Main(string[] args)
    {
        // Before any window: sharp text at the real screen scale, modern controls.
        SetProcessDPIAware();
        Application.EnableVisualStyles();

        // Settings > Apps runs the installed exe with --uninstall
        if (args.Length > 0 && args[0] == "--uninstall") { Installer.Uninstall(); return; }
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
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Snip";

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
            try { RegisterUninstall(); } catch { }
            Process.Start(Exe);
            MessageBox.Show("Snip is installed and running.\n\nPress Ctrl + ' + # together, drag a box, then paste with Ctrl+V.\n" +
                "Hold Ctrl while dragging to record that box as a video; Alt+S stops.\n" +
                "It starts with Windows. Click the camera icon by the clock for the menu.\n" +
                "To remove it, use Windows Settings > Apps, like any program.",
                "Snip", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Snip could not be installed:\n\n" + ex.Message, "Snip", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Shows Snip in Settings > Apps > Installed apps, with Uninstall running this exe with --uninstall.
    static void RegisterUninstall()
    {
        using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "Snip");
            k.SetValue("DisplayIcon", Exe);
            k.SetValue("DisplayVersion", "1.2");
            k.SetValue("Publisher", "Michal Szyszka");
            k.SetValue("InstallLocation", Dir);
            k.SetValue("UninstallString", "\"" + Exe + "\" --uninstall");
            k.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), Microsoft.Win32.RegistryValueKind.DWord);
        }
    }

    // Run by Settings > Apps (a second copy of the exe, while the tray one is still up).
    public static void Uninstall()
    {
        if (MessageBox.Show("Remove Snip from this computer?\n\nThis closes Snip and deletes its folder, shortcuts and settings. " +
                "Your recordings in Downloads stay.", "Snip", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        StopRunning();
        foreach (var link in new[] { StartupLink, DesktopLink }) if (File.Exists(link)) File.Delete(link);
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        // this exe is inside the folder and can't delete itself; a short-lived cmd removes it after we exit
        Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + Dir + "\"")
            { CreateNoWindow = true, UseShellExecute = false });
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

// key=value lines in settings.txt next to the exe: mic, cam, cam_on, cam_shape, cam_bg, cam_bg_pic, phone_on, phone_bg, phone_bg_pic.
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
        string zip = Path.Combine(Path.GetTempPath(), "snip-ffmpeg.zip");
        using (var f = new DownloadForm("Snip - downloading FFmpeg", DownloadUrl, zip, () => Unpack(zip))) f.ShowDialog();
        return Find();
    }

    static void Unpack(string zip)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OwnCopy));
        using (var z = ZipFile.OpenRead(zip))
            foreach (var entry in z.Entries)
                if (entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)) { entry.ExtractToFile(OwnCopy, true); break; }
        try { File.Delete(zip); } catch { }
        if (!File.Exists(OwnCopy)) throw new Exception("ffmpeg.exe was not in the archive.");
    }

    // "ffmpeg -list_devices" prints each DirectShow device with its alternative (stable) name on the next line.
    public static List<Mic> ListMics() { return ListDevices("audio"); }
    public static List<Mic> ListCams() { return ListDevices("video"); }

    static string listCache; static DateTime listAt;

    public static List<Mic> ListDevices(string kind)
    {
        var mics = new List<Mic>();
        string exe = Find();
        if (exe == null) return mics;
        // the panel asks for cameras and mics several times per rebuild: one ffmpeg run serves them all
        string err;
        if (listCache != null && (DateTime.Now - listAt).TotalSeconds < 3) err = listCache;
        else { err = listCache = Run(exe, "-hide_banner -list_devices true -f dshow -i dummy", 8000); listAt = DateTime.Now; }
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

// One recording at a time: ffmpeg grabs the screen area (gdigrab; for a framed box that can be
// dragged mid-recording, Snip's own Grabber feeds it the frames) and the chosen mic
// (DirectShow), encodes H.264 + AAC. ffmpeg cannot pause, so each stretch between Pause and
// Resume is its own segment file in %TEMP%; Stop joins them losslessly (concat, -c copy)
// into the mp4 in Downloads. "q" on stdin ends a segment cleanly so it is playable.
// A small progress window that downloads one file to disk, then runs an optional follow-up step
// (unpacking, say) before it closes. Closing it part-way cancels the download.
class DownloadForm : Form
{
    readonly ProgressBar bar = new ProgressBar { Dock = DockStyle.Bottom, Height = 22, Maximum = 1000 };
    readonly Label label = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0) };
    readonly WebClient web = new WebClient();

    public DownloadForm(string title, string url, string dest, Action afterwards)
    {
        Text = title; Width = 420; Height = 110;
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
            if (afterwards != null)
            {
                label.Text = "Unpacking…"; Refresh();
                try { afterwards(); }
                catch (Exception ex) { MessageBox.Show(this, "Could not unpack the download:\n\n" + ex.Message, "Snip"); }
            }
            Close();
        };
        Shown += (s, e) => { Directory.CreateDirectory(Path.GetDirectoryName(dest)); web.DownloadFileAsync(new Uri(url), dest); };
        FormClosing += (s, e) => { if (web.IsBusy) web.CancelAsync(); };
    }
}

static class Recorder
{
    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    enum After { None, Pause, Finish, Cancel }
    static Process proc;            // the running segment, null while paused
    static Grabber grab;            // feeds proc its frames when the box can move
    static string exe, segFile;
    static Rectangle area;
    static bool movable;
    static readonly object areaLock = new object();
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

    // movable: the box may be dragged mid-recording, so Snip grabs the frames itself (Grabber)
    // instead of gdigrab, which only ever films the rectangle it was started with.
    public static bool Start(Rectangle a, Mic m, bool canMove)
    {
        if (Active) return false;
        exe = Ffmpeg.Ensure();
        if (exe == null) return false;
        // encoders want even sizes
        a.Width &= ~1; a.Height &= ~1;
        if (a.Width < 2 || a.Height < 2) return false;
        area = a; mic = m; movable = canMove;

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
        // Grabber frames are stamped with the wall clock as ffmpeg reads them, the same clock gdigrab uses,
        // so the mic shift below lines them up the same way.
        if (movable)
            a.AppendFormat("-thread_queue_size 1024 -f rawvideo -pix_fmt bgr0 -video_size {0}x{1} -framerate 30 -use_wallclock_as_timestamps 1 -i - ",
                area.Width, area.Height);
        else
            a.AppendFormat("-thread_queue_size 1024 -f gdigrab -framerate 30 -offset_x {0} -offset_y {1} -video_size {2}x{3} -draw_mouse 1 -i desktop ",
                area.X, area.Y, area.Width, area.Height);
        if (mic != null)
        {
            // gdigrab stamps frames with the epoch clock; dshow stamps audio with the system tick clock
            // (ms since boot), and ffmpeg would otherwise reset each input to zero and lose the ~1 s the
            // mic takes to start. Shift the audio onto the epoch clock so -copyts keeps the real offset.
            double tickToEpoch = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds - GetTickCount64() / 1000.0;
            a.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                "-itsoffset {0:F3} -thread_queue_size 1024 -rtbufsize 100M -f dshow -i \"audio={1}\" ", tickToEpoch, mic.Alt ?? mic.Name);
        }
        a.Append("-copyts -avoid_negative_ts make_zero -c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p -r 30 ");
        if (mic != null) a.Append("-c:a aac -b:a 160k -ac 1 ");
        if (mic != null && movable) a.Append("-shortest ");   // no "q" with frames on stdin: the end of the frames ends the mic too
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
        try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }   // the mic must never wait for the camera effect
        p.Exited += (s, e) => ui.BeginInvoke(new Action(() => OnSegExit(p, errors)));
        grab = movable ? new Grabber(p.StandardInput.BaseStream, area.Size, Where) : null;
        if (grab != null) grab.Start();
        return true;
    }

    static Point Where() { lock (areaLock) return area.Location; }

    // The box was dragged: the next frame comes from its new place. Size never changes.
    public static void MoveTo(Point p) { lock (areaLock) area.Location = p; }

    // ffmpeg finishes the file after "q" (or, fed by the Grabber, at the end of its input); only kill it if it hangs
    static void Quit(Process p)
    {
        if (grab != null) { grab.Stop(); grab = null; }   // stdin carries frames there, so no "q"
        else try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch { }
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
        if (proc != null) { after = After.Cancel; if (grab != null) { grab.Stop(); grab = null; } try { proc.Kill(); } catch { } }
        else Done(null, null);
    }

    static void OnSegExit(Process p, StringBuilder errors)
    {
        if (p != proc) return;
        proc = null;
        if (grab != null) { grab.Stop(); grab = null; }   // ffmpeg died on its own
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

// Copies the recording box off the screen 30 times a second, mouse pointer drawn in, and writes
// the frames to ffmpeg's stdin, reading the box's place afresh each frame so it can be dragged
// mid-recording. CAPTUREBLT takes in layered windows such as the camera bubble, as gdigrab does.
// If ffmpeg falls behind, the write blocks and frames are skipped, never bunched up. Stop()
// closes stdin, which ends ffmpeg's input and so finishes the segment file.
class Grabber
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out ICONINFO ii);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public Point pos; }
    [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    const int Fps = 30;
    readonly Stream sink;
    readonly int w, h;
    readonly Func<Point> where;
    volatile bool stop;
    IntPtr lastCursor; Point hotspot;

    public Grabber(Stream sink, Size size, Func<Point> where) { this.sink = sink; w = size.Width; h = size.Height; this.where = where; }

    public void Start() { new Thread(Run) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "snip-grab" }.Start(); }
    public void Stop() { stop = true; }

    void Run()
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bits;
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };   // top-down, BGRx
        IntPtr dib = CreateDIBSection(mem, ref bi, 0, out bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(mem, dib);
        var buf = new byte[w * h * 4];
        timeBeginPeriod(1);   // Sleep to the millisecond, not the 15.6 ms tick
        var clock = Stopwatch.StartNew();
        long n = 0;
        try
        {
            while (!stop)
            {
                Point o = where();
                BitBlt(mem, 0, 0, w, h, screen, o.X, o.Y, SRCCOPY | CAPTUREBLT);
                DrawCursor(mem, o);
                Marshal.Copy(bits, buf, 0, buf.Length);
                sink.Write(buf, 0, buf.Length);
                sink.Flush();
                n++;
                double now = clock.Elapsed.TotalMilliseconds, due = n * 1000.0 / Fps;
                if (due > now) Thread.Sleep((int)(due - now));
                else n = (long)(now * Fps / 1000);   // behind: carry on from now, don't burst to catch up
            }
        }
        catch (IOException) { }               // ffmpeg went away (cancelled or failed)
        catch (ObjectDisposedException) { }
        finally
        {
            timeEndPeriod(1);
            SelectObject(mem, old); DeleteObject(dib); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
            try { sink.Close(); } catch { }
        }
    }

    void DrawCursor(IntPtr dc, Point origin)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
        if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;   // 1 = CURSOR_SHOWING
        if (ci.hCursor != lastCursor)
        {
            ICONINFO ii;
            if (!GetIconInfo(ci.hCursor, out ii)) return;
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            lastCursor = ci.hCursor; hotspot = new Point(ii.xHotspot, ii.yHotspot);
        }
        DrawIconEx(dc, ci.pos.X - origin.X - hotspot.X, ci.pos.Y - origin.Y - hotspot.Y, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);   // DI_NORMAL
    }
}

// The tray's one panel: every choice in view, no submenus. Opens on a click of the tray
// icon, closes when it loses focus or on Esc, and follows the Windows light/dark setting.
// Picks apply at once and the panel stays open; actions that open something close it first.
class TrayPanel : Form
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    public class Item
    {
        public string Kind = "radio";   // action, rec, header, radio, chips, note, footer
        public string Text, Hint;
        public bool Checked, Enabled = true, CloseFirst;
        public Action Click;
        public Item Trail;              // small link at the right of an action or header row
        public List<Item> Chips;        // chips, rec and footer rows
        public Rectangle Rect;
    }

    readonly Func<List<Item>> build;
    readonly float s = Dpi.Scale;
    readonly Font font, bold, small, mono;
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 500 };
    readonly bool dark;
    readonly Color bg, text, muted, line, hoverFill, chipFill, accent, red, amber;
    List<Item> items = new List<Item>();
    Item hover, pressed;
    bool wasLive;

    public TrayPanel(Func<List<Item>> build)
    {
        this.build = build;
        dark = SystemDark();
        accent = Accent();
        bg = dark ? Color.FromArgb(32, 32, 30) : Color.FromArgb(249, 249, 249);
        text = dark ? Color.FromArgb(240, 240, 236) : Color.FromArgb(27, 27, 27);
        muted = dark ? Color.FromArgb(158, 158, 152) : Color.FromArgb(104, 104, 100);
        line = dark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(0, 0, 0);
        hoverFill = dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0);
        chipFill = dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0);
        red = Color.FromArgb(232, 52, 46); amber = Color.FromArgb(255, 184, 77);
        font = new Font("Segoe UI", 10f);
        bold = new Font("Segoe UI", 10f, FontStyle.Bold);
        small = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        mono = new Font("Consolas", 11f, FontStyle.Bold);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = bg;
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Rebuild();
        tick.Tick += (s_, e) =>
        {
            bool live = Recorder.Active;
            if (live != wasLive) Rebuild(); else if (live) Invalidate();
        };
        tick.Start();
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; cp.ClassStyle |= 0x20000; return cp; }   // TOOLWINDOW, DROPSHADOW
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2;   // DWMWCP_ROUND; Windows 10 says no, so the region below does it there
        if (DwmSetWindowAttribute(Handle, 33, ref round, 4) != 0) Region = new Region(RecFrame.Rounded(new Rectangle(0, 0, Width, Height), S(8)));
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); SetForegroundWindow(Handle); Activate(); }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Close(); }
    protected override void OnFormClosed(FormClosedEventArgs e) { tick.Stop(); base.OnFormClosed(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); base.OnKeyDown(e); }

    // The taskbar and its flyouts follow SystemUsesLightTheme, not the apps setting.
    static bool SystemDark()
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                return !(v is int) || (int)v == 0;
            }
        }
        catch { return true; }
    }

    static Color Accent()
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
            {
                object v = k == null ? null : k.GetValue("AccentColor");
                if (v is int) { uint c = (uint)(int)v; return Color.FromArgb((int)(c & 0xFF), (int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF)); }   // stored as ABGR
            }
        }
        catch { }
        return Color.FromArgb(0, 120, 212);
    }

    int S(double v) { return (int)Math.Round(v * s); }

    // Ask the app for the rows again, lay them out, size and place the window.
    public void Rebuild()
    {
        if (IsDisposed) return;
        items = build();
        wasLive = Recorder.Active;
        hover = pressed = null;
        int pad = S(10), w = S(336), y = S(8);
        foreach (var it in items)
        {
            if (it.Kind == "header" && y > S(8)) y += S(6);
            int h;
            switch (it.Kind)
            {
                case "action": h = S(40); break;
                case "rec": h = S(46); break;
                case "header": h = S(30); break;
                case "radio": h = S(30); break;
                case "note": h = S(26); break;
                case "footer": h = S(40); break;
                case "chips": h = LayoutChips(it, pad, y, w - 2 * pad); break;
                default: h = S(8); break;
            }
            it.Rect = new Rectangle(pad, y, w - 2 * pad, h);
            if (it.Kind == "rec" || it.Kind == "footer") LayoutRow(it);
            y += h;
        }
        Place(w, y + S(8));
        Invalidate();
    }

    // Chips flow left to right under a small label and wrap; returns the row height.
    int LayoutChips(Item row, int x0, int y0, int width)
    {
        int gap = S(6), chipH = S(28), x = 0, y = string.IsNullOrEmpty(row.Text) ? 0 : S(22);
        foreach (var c in row.Chips)
        {
            int cw = TextRenderer.MeasureText(c.Text, bold, Size.Empty, TextFormatFlags.NoPrefix).Width + S(22);
            if (x > 0 && x + cw > width) { x = 0; y += chipH + gap; }
            c.Rect = new Rectangle(x0 + x, y0 + y, cw, chipH);
            x += cw + gap;
        }
        return y + chipH + S(6);
    }

    // Rec strip: time at the left, buttons from the right. Footer: first button left, the rest right.
    void LayoutRow(Item row)
    {
        int chipH = S(30), gap = S(6), y = row.Rect.Y + (row.Rect.Height - chipH) / 2;
        int right = row.Rect.Right;
        for (int i = row.Chips.Count - 1; i >= 0; i--)
        {
            var c = row.Chips[i];
            int cw = TextRenderer.MeasureText(c.Text, bold, Size.Empty, TextFormatFlags.NoPrefix).Width + S(24)
                   + (string.IsNullOrEmpty(c.Hint) ? 0 : TextRenderer.MeasureText(c.Hint, small).Width + S(8));
            if (row.Kind == "footer" && i == 0) { c.Rect = new Rectangle(row.Rect.X, y, cw, chipH); break; }
            c.Rect = new Rectangle(right - cw, y, cw, chipH);
            right -= cw + gap;
        }
    }

    // Beside the cursor, inside the working area, on the side away from the taskbar.
    void Place(int w, int h)
    {
        var cur = Cursor.Position;
        var sc = Screen.FromPoint(cur);
        var wa = sc.WorkingArea; var b = sc.Bounds;
        int m = S(12), x, y;
        if (wa.Bottom < b.Bottom) { y = wa.Bottom - m - h; x = cur.X - w / 2; }
        else if (wa.Top > b.Top) { y = wa.Top + m; x = cur.X - w / 2; }
        else if (wa.Left > b.Left) { x = wa.Left + m; y = cur.Y - h / 2; }
        else { x = wa.Right - m - w; y = cur.Y - h / 2; }
        x = Math.Max(wa.Left + m, Math.Min(x, wa.Right - m - w));
        y = Math.Max(wa.Top + m, Math.Min(y, wa.Bottom - m - h));
        Bounds = new Rectangle(x, y, w, h);
        if (Region != null) Region = new Region(RecFrame.Rounded(new Rectangle(0, 0, w, h), S(8)));
    }

    Item Hit(Point p)
    {
        foreach (var it in items)
        {
            if (it.Chips != null) { foreach (var c in it.Chips) if (c.Rect.Contains(p)) return c; continue; }
            if (it.Trail != null && it.Trail.Rect.Contains(p)) return it.Trail;
            if ((it.Kind == "action" || it.Kind == "radio") && it.Rect.Contains(p)) return it;
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = Hit(e.Location);
        if (h != hover) { hover = h; Invalidate(); }
        Cursor = h != null && h.Enabled ? Cursors.Hand : Cursors.Default;
    }
    protected override void OnMouseLeave(EventArgs e) { if (hover != null) { hover = null; Invalidate(); } }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = Hit(e.Location); Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        var h = Hit(e.Location);
        var p = pressed; pressed = null;
        if (h == null || h != p || !h.Enabled || h.Click == null) { Invalidate(); return; }
        if (h.CloseFirst) { Close(); h.Click(); return; }
        h.Click();
        if (!IsDisposed && Visible) Rebuild();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(bg);
        var tf = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        foreach (var it in items)
        {
            var r = it.Rect;
            switch (it.Kind)
            {
                case "action":
                    Backdrop(g, r, it);
                    TextRenderer.DrawText(g, it.Text, bold, Inset(r, S(12), 0), it.Enabled ? text : muted, tf);
                    if (it.Trail != null) DrawTrail(g, it, r);
                    else if (it.Hint != null) TextRenderer.DrawText(g, it.Hint, font, Inset(r, 0, S(12)), muted, tf | TextFormatFlags.Right);
                    break;
                case "rec":
                    {
                        bool paused = Recorder.Paused;
                        var t = Recorder.Elapsed;
                        using (var br = new SolidBrush(paused ? amber : red)) g.FillEllipse(br, r.X + S(12), r.Y + r.Height / 2 - S(5), S(10), S(10));
                        string time = string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds) + (paused ? "  paused" : "");
                        TextRenderer.DrawText(g, time, mono, Inset(r, S(30), 0), paused ? amber : text, tf);
                        foreach (var c in it.Chips) DrawButton(g, c, c.Text == "Stop & save");
                        break;
                    }
                case "header":
                    Rule(g, r);
                    TextRenderer.DrawText(g, it.Text.ToUpperInvariant(), small, Inset(r, S(12), 0), muted, tf);
                    if (it.Trail != null) DrawTrail(g, it, r);
                    break;
                case "radio":
                    Backdrop(g, r, it);
                    if (it.Checked) DrawCheck(g, new Rectangle(r.X + S(10), r.Y + (r.Height - S(14)) / 2, S(14), S(14)), it.Enabled ? accent : muted);
                    TextRenderer.DrawText(g, it.Text, it.Checked ? bold : font, Inset(r, S(32), S(8)), it.Enabled ? text : muted, tf);
                    break;
                case "note":
                    TextRenderer.DrawText(g, it.Text, font, Inset(r, S(12), 0), muted, tf);
                    break;
                case "chips":
                    if (!string.IsNullOrEmpty(it.Text))
                        TextRenderer.DrawText(g, it.Text, font, new Rectangle(r.X + S(12), r.Y, r.Width, S(22)), muted, tf);
                    foreach (var c in it.Chips) DrawChip(g, c);
                    break;
                case "footer":
                    Rule(g, r);
                    foreach (var c in it.Chips) DrawButton(g, c, false);
                    break;
            }
        }
        using (var pen = new Pen(Color.FromArgb(dark ? 40 : 30, line)))
        using (var p = RecFrame.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), S(8))) g.DrawPath(pen, p);
    }

    static Rectangle Inset(Rectangle r, int left, int right) { return new Rectangle(r.X + left, r.Y, r.Width - left - right, r.Height); }

    void Rule(Graphics g, Rectangle r)
    {
        using (var pen = new Pen(Color.FromArgb(dark ? 28 : 24, line))) g.DrawLine(pen, r.X, r.Y + S(2), r.Right, r.Y + S(2));
    }

    void Backdrop(Graphics g, Rectangle r, Item it)
    {
        if (hover != it || !it.Enabled) return;
        using (var br = new SolidBrush(pressed == it ? chipFill : hoverFill)) using (var p = RecFrame.Rounded(r, S(5))) g.FillPath(br, p);
    }

    // A link-looking word at the right end of the row, measured so it can be hit.
    void DrawTrail(Graphics g, Item row, Rectangle r)
    {
        var t = row.Trail;
        int tw = TextRenderer.MeasureText(t.Text, font).Width + S(16);
        t.Rect = new Rectangle(r.Right - tw, r.Y + (r.Height - S(26)) / 2, tw, S(26));
        if (hover == t && t.Enabled) using (var br = new SolidBrush(hoverFill)) using (var p = RecFrame.Rounded(t.Rect, S(5))) g.FillPath(br, p);
        TextRenderer.DrawText(g, t.Text, font, t.Rect, t.Enabled ? accent : muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    void DrawChip(Graphics g, Item c)
    {
        var r = c.Rect;
        Color fill = c.Checked ? (c.Enabled ? accent : muted) : hover == c && c.Enabled ? Color.FromArgb(dark ? 48 : 30, line) : chipFill;
        using (var br = new SolidBrush(fill)) using (var p = RecFrame.Rounded(r, r.Height / 2)) g.FillPath(br, p);
        Color tc = c.Checked ? Color.White : c.Enabled ? text : muted;
        TextRenderer.DrawText(g, c.Text, c.Checked ? bold : font, r, tc,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    // Rec strip and footer buttons: text on a quiet backdrop; red and bold for Stop & save.
    void DrawButton(Graphics g, Item c, bool strong)
    {
        var r = c.Rect;
        bool on = hover == c && c.Enabled;
        Color fill = strong ? (on ? Color.FromArgb(250, 76, 68) : red) : on ? hoverFill : Color.Transparent;
        if (fill != Color.Transparent) using (var br = new SolidBrush(fill)) using (var p = RecFrame.Rounded(r, S(6))) g.FillPath(br, p);
        Color tc = strong ? Color.White : c.Enabled ? text : muted;
        var tf = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        var f = strong ? bold : font;
        int x = r.X + S(12);
        TextRenderer.DrawText(g, c.Text, f, new Rectangle(x, r.Y, r.Width, r.Height), tc, tf);
        if (!string.IsNullOrEmpty(c.Hint))
        {
            x += TextRenderer.MeasureText(c.Text, f, Size.Empty, TextFormatFlags.NoPrefix).Width + S(8);
            TextRenderer.DrawText(g, c.Hint, small, new Rectangle(x, r.Y, r.Width, r.Height), strong ? Color.FromArgb(220, 255, 255, 255) : muted, tf);
        }
    }

    static void DrawCheck(Graphics g, Rectangle r, Color c)
    {
        using (var pen = new Pen(c, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(pen, new[] { new PointF(r.X + r.Width * 0.1f, r.Y + r.Height * 0.55f), new PointF(r.X + r.Width * 0.4f, r.Y + r.Height * 0.85f), new PointF(r.X + r.Width * 0.92f, r.Y + r.Height * 0.2f) });
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
    TrayPanel panel;
    RecFrame frame;
    CamBubble bubble;
    PhoneBubble phone;      // the iPhone's picture, a small second bubble beside the webcam
    Countdown countdown;
    Rectangle armedArea;
    Rectangle? drawnArea;   // the box drawn with Ctrl-drag, kept while the bar is switched to just the camera
    bool busy, restarting;

    public TrayApp()
    {
        pump.CreateControl();
        Recorder.Init();
        cameraIcon = MakeIcon();
        recIcon = MakeRecIcon();
        tray = new NotifyIcon { Icon = cameraIcon, Text = "Snip  -  Ctrl + ' + #", Visible = true };
        // left click while recording stops it (the red dot); any other click opens the panel
        tray.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left && Recorder.Active) StopPressed();
            else if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right) TogglePanel();
        };
        tray.BalloonTipClicked += (s, e) => { if (Recorder.LastFile != null && File.Exists(Recorder.LastFile)) Process.Start("explorer.exe", "/select,\"" + Recorder.LastFile + "\""); };
        Recorder.Finished += OnFinished;
        if (PhoneWanted()) PhoneCam.Start(pump);
        PhoneCam.Shot += f => tray.ShowBalloonTip(4000, (Regex.IsMatch(f, @"\.(mov|mp4|webm)$") ? "Video" : "Picture") + " from the iPhone - paste with Ctrl+V", Path.GetFileName(f), ToolTipIcon.None);
        PhoneCam.Changed += () => { if (panel != null && !panel.IsDisposed) panel.Rebuild(); };
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
    void ArmRecording(Rectangle area, bool camOnly, bool fromTray = true)
    {
        if (Recorder.Active || frame != null || bubble != null) return;
        if (camOnly)
        {
            if (Ffmpeg.Ensure() == null) return;
            string cam = CurrentCam();
            if (cam == null) { MessageBox.Show("No camera found. Pick one in the tray panel.", "Snip", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            bubble = new CamBubble(cam, false);
            var wa = Screen.PrimaryScreen.WorkingArea;
            bubble.Location = new Point(wa.X + (wa.Width - bubble.Width) / 2, wa.Y + (wa.Height - bubble.Height) / 2 + 32);
            ShowBubble();
            area = bubble.Bounds;
        }
        else if (CamWanted()) OpenBubble(area);
        if (!camOnly && PhoneWanted()) OpenPhone(area);
        armedArea = area;
        if (!camOnly) drawnArea = area;
        else if (fromTray) drawnArea = null;   // the tray's "Record just the camera": no drawn area to go back to
        bool haveCam = CurrentCam() != null;
        frame = new RecFrame(area, !camOnly && haveCam, bubble != null, camOnly, haveCam && (!camOnly || drawnArea.HasValue),
            !camOnly, phone != null,
            MainPressed, TogglePause, Restart, ToggleCam, ClosePressed, ToggleSolo, TogglePhone);
        frame.Moved += (dx, dy) =>   // the box was dragged: record the new place, and the camera goes with it
        {
            armedArea.Offset(dx, dy);
            if (Recorder.Active) Recorder.MoveTo(armedArea.Location);   // mid-recording: the video follows
            if (bubble != null) bubble.Location = new Point(bubble.Left + dx, bubble.Top + dy);
            if (phone != null) phone.Location = new Point(phone.Left + dx, phone.Top + dy);
        };
        frame.Show();
    }

    void RecordWholeScreen()
    {
        if (Recorder.Active || frame != null) return;
        if (CamWanted()) OpenBubble(Screen.PrimaryScreen.WorkingArea);
        if (PhoneWanted()) OpenPhone(Screen.PrimaryScreen.WorkingArea);
        if (!StartRecording(SystemInformation.VirtualScreen, false)) { CloseBubble(); ClosePhone(); }
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
                        frame.State = StartRecording(armedArea, true) ? RecFrame.Mode.Recording : RecFrame.Mode.Ready;
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

    // The bar's "just the camera" switch, before Record: swap the drawn box for the camera picture and back.
    void ToggleSolo()
    {
        if (frame == null || frame.State != RecFrame.Mode.Ready) return;
        bool toSolo = !frame.Solo;
        Rectangle back = drawnArea ?? armedArea;
        CloseAll();
        ArmRecording(back, toSolo, false);
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

    // The bar's iPhone switch: add or remove the phone's bubble, before Record or while recording.
    void TogglePhone()
    {
        if (frame == null) return;
        if (phone != null) { ClosePhone(); SetPhone(false); }
        else { SetPhone(true); OpenPhone(armedArea); }
        frame.PhoneOn = phone != null;
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

    void CloseAll() { AbortCountdown(); CloseFrame(); CloseBubble(); ClosePhone(); }

    // The phone's bubble sits to the right of the webcam's, bottoms level; alone, where the webcam would be.
    void OpenPhone(Rectangle area)
    {
        if (phone != null) return;
        phone = new PhoneBubble();
        int m = (int)(20 * Dpi.Scale);
        if (bubble != null) phone.Location = new Point(Math.Min(bubble.Right + m / 2, area.Right - m - phone.Width), bubble.Bottom - phone.Height);
        else phone.Location = new Point(area.X + m, Math.Max(area.Y, area.Bottom - m - phone.Height));
        phone.Show();
    }

    void ClosePhone()
    {
        if (phone == null) return;
        phone.Close(); phone.Dispose(); phone = null;
    }

    static bool PhoneWanted() { return Settings.Get("phone_on") == "1"; }
    void SetPhone(bool on)
    {
        Settings.Set("phone_on", on ? "1" : "0");
        if (on) PhoneCam.Start(pump); else PhoneCam.Stop();
    }

    bool StartRecording(Rectangle area, bool canMove)
    {
        if (Recorder.Active) return false;
        if (!Recorder.Start(area, CurrentMic(), canMove)) return false;
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
            // Put the file itself on the clipboard, as Ctrl+C in Explorer would, so Ctrl+V
            // in WhatsApp, Slack, Teams or Gmail attaches the video.
            bool copied = false;
            try
            {
                var files = new System.Collections.Specialized.StringCollection();
                files.Add(file);
                Clipboard.SetFileDropList(files);
                copied = true;
            }
            catch (ExternalException) { }   // clipboard held by another app; the file is still in Downloads
            tray.ShowBalloonTip(5000, copied ? "Saved to Downloads and copied - paste with Ctrl+V" : "Saved to Downloads", Path.GetFileName(file), ToolTipIcon.None);
        }
        else if (error != null) MessageBox.Show("Recording failed.\n\n" + error, "Snip", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    void TogglePanel()
    {
        if (panel != null && !panel.IsDisposed) { panel.Close(); panel = null; return; }
        if (busy) return;
        panel = new TrayPanel(PanelRows);
        panel.Show();
    }

    // Everything the tray offers, as rows of the one panel. Called again after every pick.
    List<TrayPanel.Item> PanelRows()
    {
        var rows = new List<TrayPanel.Item>();
        bool live = Recorder.Active;
        bool idle = !live && frame == null && bubble == null;   // nothing armed or running
        if (live)
            rows.Add(new TrayPanel.Item { Kind = "rec", Chips = new List<TrayPanel.Item> {
                new TrayPanel.Item { Text = Recorder.Paused ? "Resume" : "Pause", Click = TogglePause },
                new TrayPanel.Item { Text = "Stop & save", Hint = "Alt+S", Click = StopPressed } } });
        rows.Add(new TrayPanel.Item { Kind = "action", Text = "Snip now", Hint = "Ctrl + ' + #", CloseFirst = true, Click = () => Snip(false) });
        rows.Add(new TrayPanel.Item { Kind = "action", Text = "Record an area", Enabled = idle, CloseFirst = true, Click = () => Snip(true),
            Trail = new TrayPanel.Item { Text = "or the whole screen", Enabled = idle, CloseFirst = true, Click = RecordWholeScreen } });

        bool haveFfmpeg = Ffmpeg.Find() != null;
        rows.Add(new TrayPanel.Item { Kind = "header", Text = "Camera",
            Trail = new TrayPanel.Item { Text = "Record just the camera", Enabled = idle && haveFfmpeg && CurrentCam() != null, CloseFirst = true,
                Click = () => ArmRecording(Rectangle.Empty, true) } });
        if (!haveFfmpeg) rows.Add(new TrayPanel.Item { Kind = "note", Text = "The camera and microphone lists appear after the first recording (FFmpeg)." });
        else
        {
            string used = CurrentCam();
            var cams = Ffmpeg.ListCams();
            rows.Add(new TrayPanel.Item { Text = "No camera", Checked = used == null, Enabled = idle, Click = () => Settings.Set("cam", "") });
            for (int i = 0; i < cams.Count; i++)
            {
                var cam = cams[i];
                rows.Add(new TrayPanel.Item { Text = cam.Name, Checked = (cam.Alt ?? cam.Name) == used, Enabled = idle,
                    Click = () => { Settings.Set("cam", cam.Alt ?? cam.Name); Settings.Set("cam_on", "1"); } });
            }
            if (cams.Count == 0) rows.Add(new TrayPanel.Item { Kind = "note", Text = "No camera found." });

            // the shape of the camera on screen recordings (right-clicking the camera changes it too)
            var shapes = new List<TrayPanel.Item>();
            string shape = CamBubble.SavedShape();
            for (int i = 0; i < CamBubble.Shapes.Length; i++)
            {
                string sh = CamBubble.Shapes[i];
                shapes.Add(new TrayPanel.Item { Text = CamBubble.ShortShapeNames[i], Checked = sh == shape,
                    Click = () => { if (bubble != null && bubble.Overlay) bubble.SetShape(sh); else Settings.Set("cam_shape", sh); } });
            }
            rows.Add(new TrayPanel.Item { Kind = "chips", Text = "Shape", Chips = shapes });
            rows.Add(new TrayPanel.Item { Kind = "chips", Text = "Background", Chips = Bg.Cam.PanelChips() });
        }

        bool phoneOn = PhoneWanted();
        rows.Add(new TrayPanel.Item { Kind = "header", Text = "iPhone camera",
            Trail = new TrayPanel.Item { Text = !phoneOn ? "off" : PhoneCam.Error != null ? "not running" : PhoneCam.Connected ? "connected" : "not connected", Enabled = false } });
        rows.Add(new TrayPanel.Item { Text = "Off", Checked = !phoneOn, Enabled = idle, Click = () => SetPhone(false) });
        rows.Add(new TrayPanel.Item { Text = "On, as a small second bubble", Checked = phoneOn, Enabled = idle, Click = () => SetPhone(true) });
        if (phoneOn)
        {
            rows.Add(new TrayPanel.Item { Kind = "chips", Text = "Background", Chips = Bg.Phone.PanelChips() });
            if (PhoneCam.Error != null) rows.Add(new TrayPanel.Item { Kind = "note", Text = "Could not start: " + PhoneCam.Error });
            else if (PhoneCam.Connected) rows.Add(new TrayPanel.Item { Kind = "note", Text = "The iPhone is sending its picture." });
            else
            {
                rows.Add(new TrayPanel.Item { Kind = "note", Text = "iPhone Safari: " + PhoneCam.Address });
                rows.Add(new TrayPanel.Item { Kind = "note", Text = "First time: " + PhoneCam.SetupAddress });
                rows.Add(new TrayPanel.Item { Kind = "note", Text = "Only on Wi-Fi you trust." });
            }
        }

        rows.Add(new TrayPanel.Item { Kind = "header", Text = "Microphone" });
        if (haveFfmpeg)
        {
            var usedMic = CurrentMic();
            var mics = Ffmpeg.ListMics();
            rows.Add(new TrayPanel.Item { Text = "No microphone", Checked = usedMic == null, Enabled = idle, Click = () => Settings.Set("mic", "") });
            for (int i = 0; i < mics.Count; i++)
            {
                var mic = mics[i];
                rows.Add(new TrayPanel.Item { Text = mic.Name, Checked = usedMic != null && (mic.Alt ?? mic.Name) == (usedMic.Alt ?? usedMic.Name), Enabled = idle,
                    Click = () => Settings.Set("mic", mic.Alt ?? mic.Name) });
            }
            if (mics.Count == 0) rows.Add(new TrayPanel.Item { Kind = "note", Text = "No microphone found." });
        }

        rows.Add(new TrayPanel.Item { Kind = "footer", Chips = new List<TrayPanel.Item> {
            new TrayPanel.Item { Text = "Open the recordings folder", CloseFirst = true, Click = () => Process.Start("explorer.exe", "\"" + Recorder.DownloadsDir() + "\"") },
            new TrayPanel.Item { Text = "Quit", CloseFirst = true, Click = () => { Recorder.Stop(); CloseBubble(); ClosePhone(); tray.Visible = false; Application.Exit(); } } } });
        return rows;
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
    bool hashHeld; // # is down with Ctrl, and we swallowed it
    int hashTick;  // when, so a lost key-up can't leave it stuck

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
                    // A key we swallowed never shows in GetAsyncKeyState, so remember # ourselves.
                    bool hash = vk == vkHash || IsDown(vkHash) || (hashHeld && Environment.TickCount - hashTick < 1500);
                    if (ctrl && quote && hash)
                    {
                        if (!fired) { fired = true; fire(); }
                        return (IntPtr)1; // swallow the key so it isn't typed
                    }
                    // # first: Ctrl+# must not reach the app. It is Ctrl+\ to anything that goes by
                    // key position, which closes the side pane in Claude desktop. AltGr reads as
                    // Ctrl+Alt, and AltGr combos are real characters on some layouts, so leave those.
                    if (vk == vkHash && ctrl && !IsDown(VK_MENU))
                    {
                        hashHeld = true;
                        hashTick = Environment.TickCount;
                        return (IntPtr)1;
                    }
                }
                else
                {
                    fired = false; // chord released; allow the next one
                    if (vk == vkHash) hashHeld = false;
                }
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
    const int MainId = 1, CloseId = 2, CamId = 3, PauseId = 4, RestartId = 5, SoloId = 6, PhoneId = 7;
    Rectangle area;               // screen coordinates of what ffmpeg records
    public event Action<int, int> Moved;   // dx, dy: the box was dragged
    bool dragging; Point dragFrom, dragLoc;
    readonly bool hasCam;
    readonly bool solo, hasSolo;  // solo: this frame records just the camera; hasSolo: the switch is on the bar
    readonly bool hasPhone;       // the iPhone switch is on the bar (not on a camera-only frame)
    readonly Action onMain, onPause, onRestart, onCam, onClose, onSolo, onPhone;
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 500 };
    readonly Font font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
    readonly Font timeFont = new Font("Consolas", 12f, FontStyle.Bold);
    readonly ToolTip tip = new ToolTip { ShowAlways = true };
    Mode mode = Mode.Ready;
    bool camOn, phoneOn, blink, pressed;
    int hover;
    Rectangle pill, mainBtn, timeBox, camBtn, phoneBtn, soloBtn, pauseBtn, restartBtn, closeBtn;
    int div1, div2;

    static readonly Color Red = Color.FromArgb(232, 52, 46), RedHover = Color.FromArgb(250, 76, 68), RedDark = Color.FromArgb(180, 36, 32);
    static readonly Color Amber = Color.FromArgb(255, 184, 77);
    static readonly Color Ink = Color.FromArgb(32, 32, 30), Edge = Color.FromArgb(70, 70, 66), Muted = Color.FromArgb(170, 170, 164);
    static readonly Color Glyph = Color.FromArgb(225, 225, 220), Dim = Color.FromArgb(92, 92, 88);

    public Mode State { get { return mode; } set { mode = value; Invalidate(pill); } }
    public bool CamOn { get { return camOn; } set { camOn = value; Invalidate(pill); } }
    public bool PhoneOn { get { return phoneOn; } set { phoneOn = value; Invalidate(pill); } }
    public bool Solo { get { return solo; } }

    public RecFrame(Rectangle area, bool hasCam, bool camOn, bool solo, bool hasSolo, bool hasPhone, bool phoneOn,
        Action onMain, Action onPause, Action onRestart, Action onCam, Action onClose, Action onSolo, Action onPhone)
    {
        this.area = area; this.hasCam = hasCam; this.camOn = camOn; this.solo = solo; this.hasSolo = hasSolo;
        this.hasPhone = hasPhone; this.phoneOn = phoneOn;
        this.onMain = onMain; this.onPause = onPause; this.onRestart = onRestart; this.onCam = onCam; this.onClose = onClose; this.onSolo = onSolo; this.onPhone = onPhone;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Ink;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        // pill: [ (o) Record ] 0:00 | cam phone solo pause restart | x   - measured, so 200 % DPI clips nothing
        int labelW = Math.Max(TextRenderer.MeasureText("Stop & save", font).Width, TextRenderer.MeasureText("Starting...", font).Width);
        int timeW = TextRenderer.MeasureText("00:00", timeFont).Width + 16;
        int b = PillH - 12;
        int x = 6;
        mainBtn = new Rectangle(x, 6, 8 + (b - 16) + 8 + labelW + 16, b); x = mainBtn.Right + 12;
        timeBox = new Rectangle(x, 0, timeW, PillH); x = timeBox.Right + 4;
        div1 = x; x += 9;
        if (hasCam) { camBtn = new Rectangle(x, 6, b, b); x = camBtn.Right + 2; }
        if (hasPhone) { phoneBtn = new Rectangle(x, 6, b, b); x = phoneBtn.Right + 2; }
        if (hasSolo) { soloBtn = new Rectangle(x, 6, b, b); x = soloBtn.Right + 2; }
        pauseBtn = new Rectangle(x, 6, b, b); x = pauseBtn.Right + 2;
        restartBtn = new Rectangle(x, 6, b, b); x = restartBtn.Right + 4;
        div2 = x; x += 9;
        closeBtn = new Rectangle(x, 6, b, b); x = closeBtn.Right + 6;
        int pillW = x;

        int ringW = area.Width + 2 * Border;
        int pillX = Math.Max(0, (ringW - pillW) / 2);          // centred over the box
        mainBtn.Offset(pillX, 0); timeBox.Offset(pillX, 0); camBtn.Offset(pillX, 0); phoneBtn.Offset(pillX, 0); soloBtn.Offset(pillX, 0); pauseBtn.Offset(pillX, 0);
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
        if (hasPhone && phoneBtn.Contains(p)) return PhoneId;
        if (hasSolo && mode == Mode.Ready && soloBtn.Contains(p)) return SoloId;
        if (Live && pauseBtn.Contains(p)) return PauseId;
        if (Live && restartBtn.Contains(p)) return RestartId;
        return 0;
    }

    Rectangle RectOf(int id)
    {
        switch (id) { case MainId: return mainBtn; case CloseId: return closeBtn; case CamId: return camBtn; case PhoneId: return phoneBtn; case SoloId: return soloBtn; case PauseId: return pauseBtn; default: return restartBtn; }
    }

    string TipOf(int id)
    {
        switch (id)
        {
            case MainId: return mode == Mode.Ready ? "Start recording (3, 2, 1)" : mode == Mode.Counting ? "Cancel the countdown" : "Stop and save to Downloads  (Alt+S)";
            case CloseId: return Live ? "Throw this recording away" : "Close";
            case CamId: return camOn ? "Hide the camera" : "Show the camera";
            case PhoneId: return phoneOn ? "Remove the iPhone camera" : "Add the iPhone camera";
            case SoloId: return solo ? "Back to the area you drew" : "Record just the camera";
            case PauseId: return mode == Mode.Paused ? "Resume" : "Pause";
            case RestartId: return "Start over";
        }
        return null;
    }

    // The box can be moved before Record and while recording or paused (the video follows it):
    // hold the bar (anywhere but its buttons) or the red edge and drag. Not during the countdown or saving.
    bool CanDrag(Point p) { return (mode == Mode.Ready || Live) && Hit(p) == 0; }

    void DragTo(Point c)
    {
        var vs = SystemInformation.VirtualScreen;
        int ax = Math.Max(vs.X, Math.Min(vs.Right - area.Width, dragLoc.X + c.X - dragFrom.X + Border));
        int ay = Math.Max(vs.Y, Math.Min(vs.Bottom - area.Height, dragLoc.Y + c.Y - dragFrom.Y + Strip));
        int dx = ax - area.X, dy = ay - area.Y;
        if (dx == 0 && dy == 0) return;
        area.Offset(dx, dy);
        Location = new Point(area.X - Border, area.Y - Strip);
        if (Moved != null) Moved(dx, dy);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging) { DragTo(Cursor.Position); return; }
        int h = Hit(e.Location);
        Cursor = h != 0 ? Cursors.Hand : CanDrag(e.Location) ? Cursors.SizeAll : Cursors.Default;
        if (h == hover) return;
        hover = h; Invalidate(pill);
        tip.Hide(this);
        if (h != 0) tip.Show(TipOf(h), this, RectOf(h).X, -34, 2500);   // above the bar, never over the recording
    }
    protected override void OnMouseLeave(EventArgs e) { tip.Hide(this); if (hover != 0 || pressed) { hover = 0; pressed = false; Invalidate(pill); } }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (Hit(e.Location) != 0) { pressed = true; Invalidate(pill); }
        else if (CanDrag(e.Location)) { dragging = true; dragFrom = PointToScreen(e.Location); dragLoc = Location; tip.Hide(this); }
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (dragging) { DragTo(Cursor.Position); dragging = false; return; }   // the last move can arrive after the release
        bool was = pressed; pressed = false; Invalidate(pill);
        if (!was) return;
        tip.Hide(this);
        switch (Hit(e.Location))
        {
            case MainId: onMain(); break;
            case CloseId: onClose(); break;
            case CamId: onCam(); break;
            case PhoneId: onPhone(); break;
            case SoloId: onSolo(); break;
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
        if (hasPhone) DrawPhone(g, phoneBtn, GlyphColor(PhoneId, mode != Mode.Saving));
        if (hasSolo) DrawSolo(g, soloBtn, GlyphColor(SoloId, mode == Mode.Ready));
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

    // A phone outline; struck through, like the camera, while the iPhone bubble is off.
    void DrawPhone(Graphics g, Rectangle r, Color c)
    {
        Backdrop(g, r, PhoneId, mode != Mode.Saving);
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        using (var p = new Pen(c, 2f))
        using (var body = Rounded(new Rectangle(cx - 6, cy - 10, 12, 20), 3)) g.DrawPath(p, body);
        using (var br = new SolidBrush(c)) g.FillEllipse(br, cx - 2, cy + 5, 4, 3);
        if (!phoneOn)
        {
            using (var cut = new Pen(Ink, 5)) g.DrawLine(cut, cx - 10, cy - 10, cx + 10, cy + 10);
            using (var ln = new Pen(c, 2)) g.DrawLine(ln, cx - 10, cy - 10, cx + 10, cy + 10);
        }
    }

    // Head and shoulders in a frame: "just the camera". Lit red while this frame records only the camera.
    void DrawSolo(Graphics g, Rectangle r, Color c)
    {
        if (solo) using (var br = new SolidBrush(mode == Mode.Ready ? Red : RedDark)) g.FillEllipse(br, r);
        else Backdrop(g, r, SoloId, mode == Mode.Ready);
        if (solo) c = Color.White;
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        using (var p = new Pen(c, 1.8f))
        using (var frameBox = Rounded(new Rectangle(cx - 10, cy - 9, 20, 18), 3)) g.DrawPath(p, frameBox);
        using (var br = new SolidBrush(c))
        {
            g.FillEllipse(br, cx - 3, cy - 6, 7, 7);
            g.FillPie(br, cx - 7, cy + 2, 15, 12, 180, 180);
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
    public static readonly string[] ShortShapeNames = { "Circle", "Rounded square", "Wide 16:9" };
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
        var menu = new ContextMenuStrip();
        menu.Items.Add("-");
        menu.Opening += (s, e) =>
        {
            menu.Items.Clear();
            if (Overlay)
            {
                for (int i = 0; i < Shapes.Length; i++)
                {
                    string sh = Shapes[i];
                    var it = new ToolStripMenuItem(ShapeNames[i]) { Checked = sh == shape };
                    it.Click += (s2, e2) => SetShape(sh);
                    menu.Items.Add(it);
                }
                menu.Items.Add(new ToolStripSeparator());
            }
            Bg.Cam.AddMenuItems(menu.Items);
            e.Cancel = false;
        };
        ContextMenuStrip = menu;
        Bg.Cam.Warm();
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
                if (painting) continue;               // still busy with the last one: drop this frame
                painting = true;
                var frame = (byte[])buf.Clone();
                // background effect off this thread, so the pipe from ffmpeg keeps draining
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Bitmap bmp = null;
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;   // the recording comes first
                    try
                    {
                        try { Bg.Cam.Apply(frame, w, h); } catch { }
                        bmp = Img.FromBytes(frame, w, h);
                        var b = bmp;
                        BeginInvoke(new Action(() => { try { if (p == proc && w == fw && h == fh) Present(b); } finally { b.Dispose(); painting = false; } }));
                    }
                    catch { if (bmp != null) bmp.Dispose(); painting = false; }
                });
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

// Background blur / picture behind the person on camera. A portrait-matting model (MODNet, Apache-2.0,
// packed into the exe) finds the person; Windows' own ML runtime (WinML) runs it, on the GPU when it can.
// One background effect per camera (webcam, iPhone), each with its own setting and model state.
static class Bg
{
    public static readonly BgEffect Cam = new BgEffect("cam");
    public static readonly BgEffect Phone = new BgEffect("phone");
}

class BgEffect
{
    public static readonly string[] Modes = { "none", "blur", "picture" };
    Bitmap picture;                 // the chosen background picture, as loaded
    readonly object picLock = new object();
    Bitmap picScaled;               // ...and scaled to cover the last frame size
    public volatile string Mode;
    readonly string prefix;                // settings prefix: "cam" or "phone"
    readonly Segmenter seg = new Segmenter();   // each camera its own model state

    public BgEffect(string prefix) { this.prefix = prefix; Mode = Load(); }

    // Load the model now if an effect is on, so the first frames are not plain.
    public void Warm() { if (Mode != "none") seg.Warm(); }

    string Load()
    {
        string m = Settings.Get(prefix + "_bg");
        if (m == "picture" && !LoadPicture(Settings.Get(prefix + "_bg_pic"))) m = "none";
        return Array.IndexOf(Modes, m) >= 0 ? m : "none";
    }

    // Ready-made backgrounds packed into the exe (generated with Seedream, ours to ship): setting value, menu name, chip name.
    static readonly string[][] BuiltIn = {
        new[] { "builtin:office", "Office with bookshelf", "Office" },
        new[] { "builtin:meeting-room", "Bright meeting room", "Meeting room" },
        new[] { "builtin:evening-studio", "Evening studio", "Evening studio" },
    };

    // path: a file, or "builtin:<name>" for one of the pictures above.
    bool LoadPicture(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        bool builtIn = path.StartsWith("builtin:");
        if (!builtIn && !File.Exists(path)) return false;
        try
        {
            Bitmap b;
            Stream src = builtIn
                ? System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("bg-" + path.Substring(8) + ".jpg")
                : new MemoryStream(File.ReadAllBytes(path));
            if (src == null) return false;
            using (var s = src)
            using (var img = Image.FromStream(s)) b = new Bitmap(img);
            lock (picLock) { picture = b; picScaled = null; }
            return true;
        }
        catch { return false; }
    }

    // The three menu entries, for the tray's Camera menu and the camera's right-click menu.
    public void AddMenuItems(ToolStripItemCollection items)
    {
        string pic = Settings.Get(prefix + "_bg_pic") ?? "";
        bool ownPic = Mode == "picture" && pic != "" && !pic.StartsWith("builtin:");
        string picName = ownPic ? "Your own picture (" + Path.GetFileName(pic) + ")..." : "Your own picture...";
        var none = new ToolStripMenuItem("No background effect") { Checked = Mode == "none" };
        none.Click += (s, e) => Set("none");
        var blur = new ToolStripMenuItem("Blur the background") { Checked = Mode == "blur" };
        blur.Click += (s, e) => Set("blur");
        var ready = new ToolStripMenuItem("Professional background") { Checked = Mode == "picture" && pic.StartsWith("builtin:") };
        foreach (var bi in BuiltIn)
        {
            string key = bi[0];
            var it = new ToolStripMenuItem(bi[1]) { Checked = Mode == "picture" && pic == key };
            it.Click += (s, e) => UseBuiltIn(key);
            ready.DropDownItems.Add(it);
        }
        var picture_ = new ToolStripMenuItem(picName) { Checked = ownPic };
        picture_.Click += (s, e) => ChoosePicture();
        if (Segmenter.Broken) { blur.Enabled = ready.Enabled = picture_.Enabled = false; blur.Text += "  (not available on this PC)"; }
        else if (!Segmenter.HaveModel) blur.Text += "  (downloads a 15 MB model first)";
        items.Add(none); items.Add(blur); items.Add(ready); items.Add(picture_);
    }

    // The background choices as chips for the tray panel. Ones that may prompt (model download, file dialog) close it first.
    public List<TrayPanel.Item> PanelChips()
    {
        string pic = Settings.Get(prefix + "_bg_pic") ?? "";
        bool ownPic = Mode == "picture" && pic != "" && !pic.StartsWith("builtin:");
        bool ok = !Segmenter.Broken, prompt = !Segmenter.HaveModel;
        var chips = new List<TrayPanel.Item>();
        chips.Add(new TrayPanel.Item { Text = "None", Checked = Mode == "none", Click = () => Set("none") });
        chips.Add(new TrayPanel.Item { Text = ok ? (prompt ? "Blur  (15 MB download)" : "Blur") : "Blur  (not on this PC)", Checked = Mode == "blur", Enabled = ok, CloseFirst = prompt, Click = () => Set("blur") });
        foreach (var bi in BuiltIn)
        {
            string key = bi[0];
            chips.Add(new TrayPanel.Item { Text = bi[2], Checked = Mode == "picture" && pic == key, Enabled = ok, CloseFirst = prompt, Click = () => UseBuiltIn(key) });
        }
        chips.Add(new TrayPanel.Item { Text = ownPic ? "Your picture: " + Path.GetFileName(pic) : "Your own picture...", Checked = ownPic, Enabled = ok, CloseFirst = true, Click = ChoosePicture });
        return chips;
    }

    void UseBuiltIn(string key)
    {
        if (!LoadPicture(key)) { MessageBox.Show("Could not open that background.", "Snip", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        Settings.Set(prefix + "_bg_pic", key);
        Set("picture");
    }

    void Set(string m)
    {
        if (m != "none" && !Segmenter.Ensure()) return;
        Mode = m; Settings.Set(prefix + "_bg", m);
        if (m != "none") seg.Warm();
    }

    void ChoosePicture()
    {
        using (var d = new OpenFileDialog { Title = "Choose a background picture", Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All files|*.*" })
        {
            string old = Settings.Get(prefix + "_bg_pic");
            if (!string.IsNullOrEmpty(old) && File.Exists(old)) d.InitialDirectory = Path.GetDirectoryName(old);
            else d.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (d.ShowDialog() != DialogResult.OK) return;
            if (!LoadPicture(d.FileName)) { MessageBox.Show("Could not open that picture.", "Snip", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            Settings.Set(prefix + "_bg_pic", d.FileName);
            Set("picture");
        }
    }

    // px: one BGRA frame, changed in place. Does nothing until the model has produced a first mask.
    public void Apply(byte[] px, int w, int h)
    {
        string mode = Mode;
        if (mode == "none") return;
        seg.Offer(px, w, h);
        var m = seg.Latest;
        if (m == null) return;
        byte[] al = Feather(m, w, h);
        byte[] bg; int bw = w, bh = h;
        if (mode == "blur") bg = BlurSmall(px, w, h, out bw, out bh);
        else bg = Picture(w, h);
        if (bg == null) return;
        Blend(px, w, h, al, bg, bw, bh);
    }

    // Bilinear lookup tables for one axis: destination index -> two source indices and a 0..256 weight.
    class Axis
    {
        public readonly int[] I0, I1, F;
        public Axis(int dst, int src)
        {
            I0 = new int[dst]; I1 = new int[dst]; F = new int[dst];
            float k = (float)src / dst;
            for (int i = 0; i < dst; i++)
            {
                float s = (i + 0.5f) * k - 0.5f;
                if (s < 0) s = 0;
                int a = (int)s; if (a > src - 1) a = src - 1;
                I0[i] = a; I1[i] = Math.Min(a + 1, src - 1); F[i] = (int)((s - a) * 256);
            }
        }
    }
    static readonly Dictionary<long, Axis> axes = new Dictionary<long, Axis>();
    static Axis Ax(int dst, int src)
    {
        long key = ((long)dst << 32) | (uint)src;
        lock (axes) { Axis a; if (!axes.TryGetValue(key, out a)) axes[key] = a = new Axis(dst, src); return a; }
    }

    // The model's small mask grown to frame size with a soft edge: pulled in a touch (the model
    // tends to grab a sliver of background), scaled up, then blurred a few pixels so the cut
    // never reads as a hard outline.
    static byte[] Feather(Segmenter.MaskData m, int w, int h)
    {
        int mw = m.W, mh = m.H; float[] a = m.A;
        var e = new byte[mw * mh];
        for (int y = 0; y < mh; y++)
            for (int x = 0; x < mw; x++)
            {
                float v = 1;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y + dy; if (yy < 0 || yy >= mh) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx; if (xx < 0 || xx >= mw) continue;
                        float t = a[yy * mw + xx]; if (t < v) v = t;
                    }
                }
                e[y * mw + x] = (byte)(v <= 0 ? 0 : v >= 1 ? 255 : v * 255);
            }
        var ax = Ax(w, mw); var ay = Ax(h, mh);
        var full = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int ra = ay.I0[y] * mw, rb = ay.I1[y] * mw, fy = ay.F[y], row = y * w;
            for (int x = 0; x < w; x++)
            {
                int x0 = ax.I0[x], x1 = ax.I1[x], fx = ax.F[x];
                int top = e[ra + x0] * (256 - fx) + e[ra + x1] * fx;
                int bot = e[rb + x0] * (256 - fx) + e[rb + x1] * fx;
                full[row + x] = (byte)((top * (256 - fy) + bot * fy) >> 16);
            }
        }
        int r = Math.Max(1, Math.Min(w, h) / 320);
        full = BoxGray(full, w, h, r); full = BoxGray(full, w, h, r);
        return full;
    }

    // One separable box blur over a one-channel image (edges clamped).
    static byte[] BoxGray(byte[] src, int w, int h, int r)
    {
        var t = new byte[src.Length]; var o = new byte[src.Length];
        int n = 2 * r + 1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w, sum = 0;
            for (int i = -r; i <= r; i++) sum += src[row + Math.Min(Math.Max(i, 0), w - 1)];
            for (int x = 0; x < w; x++)
            {
                t[row + x] = (byte)(sum / n);
                sum += src[row + Math.Min(x + r + 1, w - 1)] - src[row + Math.Max(x - r, 0)];
            }
        }
        for (int x = 0; x < w; x++)
        {
            int sum = 0;
            for (int i = -r; i <= r; i++) sum += t[Math.Min(Math.Max(i, 0), h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                o[y * w + x] = (byte)(sum / n);
                sum += t[Math.Min(y + r + 1, h - 1) * w + x] - t[Math.Max(y - r, 0) * w + x];
            }
        }
        return o;
    }

    // The soft mask decides, per pixel, how much of the background copy shows through.
    static void Blend(byte[] px, int w, int h, byte[] al, byte[] bg, int bw, int bh)
    {
        bool small = bw != w || bh != h;
        var bx = Ax(w, bw); var by = Ax(h, bh);
        System.Threading.Tasks.Parallel.For(0, h, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 2 }, y =>
        {
            int ba = by.I0[y] * bw * 4, bb = by.I1[y] * bw * 4, gy = by.F[y];
            int row = y * w * 4, arow = y * w;
            for (int x = 0; x < w; x++)
            {
                int a = al[arow + x];
                if (a >= 254) continue;
                int j = row + x * 4, ia = 255 - a;
                if (small)
                {
                    int p0 = bx.I0[x] * 4, p1 = bx.I1[x] * 4, gx = bx.F[x];
                    for (int c = 0; c < 3; c++)
                    {
                        int t = bg[ba + p0 + c] * (256 - gx) + bg[ba + p1 + c] * gx;
                        int u = bg[bb + p0 + c] * (256 - gx) + bg[bb + p1 + c] * gx;
                        int b = (t * (256 - gy) + u * gy) >> 16;
                        px[j + c] = (byte)((px[j + c] * a + b * ia + 127) / 255);
                    }
                }
                else
                    for (int c = 0; c < 3; c++) px[j + c] = (byte)((px[j + c] * a + bg[j + c] * ia + 127) / 255);
            }
        });
    }

    // The frame shrunk by area-averaging and box-blurred three times (close to a Gaussian);
    // Blend grows it back bilinearly, which at this size reads as a smooth lens blur.
    const int BlurShrink = 6;
    static byte[] BlurSmall(byte[] px, int w, int h, out int tw, out int th)
    {
        int k = BlurShrink;
        tw = Math.Max(2, w / k); th = Math.Max(2, h / k);
        var t = new byte[tw * th * 4];
        int area = k * k;
        for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                int b = 0, g = 0, r = 0;
                for (int yy = y * k; yy < y * k + k; yy++)
                {
                    int i = (yy * w + x * k) * 4;
                    for (int xx = 0; xx < k; xx++, i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; }
                }
                int o = (y * tw + x) * 4;
                t[o] = (byte)(b / area); t[o + 1] = (byte)(g / area); t[o + 2] = (byte)(r / area); t[o + 3] = 255;
            }
        int rad = Math.Max(2, tw / 40);
        for (int pass = 0; pass < 3; pass++) t = BoxColor(t, tw, th, rad);
        return t;
    }

    // Separable box blur over a BGRA image (edges clamped).
    static byte[] BoxColor(byte[] src, int w, int h, int r)
    {
        var t = new byte[src.Length]; var o = new byte[src.Length];
        int n = 2 * r + 1;
        for (int y = 0; y < h; y++)
            for (int c = 0; c < 3; c++)
            {
                int row = y * w * 4 + c, sum = 0;
                for (int i = -r; i <= r; i++) sum += src[row + Math.Min(Math.Max(i, 0), w - 1) * 4];
                for (int x = 0; x < w; x++)
                {
                    t[row + x * 4] = (byte)(sum / n);
                    sum += src[row + Math.Min(x + r + 1, w - 1) * 4] - src[row + Math.Max(x - r, 0) * 4];
                }
            }
        for (int x = 0; x < w; x++)
            for (int c = 0; c < 3; c++)
            {
                int col = x * 4 + c, sum = 0;
                for (int i = -r; i <= r; i++) sum += t[Math.Min(Math.Max(i, 0), h - 1) * w * 4 + col];
                for (int y = 0; y < h; y++)
                {
                    o[y * w * 4 + col] = (byte)(sum / n);
                    sum += t[Math.Min(y + r + 1, h - 1) * w * 4 + col] - t[Math.Max(y - r, 0) * w * 4 + col];
                }
            }
        for (int i = 3; i < o.Length; i += 4) o[i] = 255;
        return o;
    }

    // The picture scaled to cover the frame (cropped, never stretched).
    byte[] Picture(int w, int h)
    {
        lock (picLock)
        {
            if (picture == null) return null;
            if (picScaled == null || picScaled.Width != w || picScaled.Height != h)
            {
                if (picScaled != null) picScaled.Dispose();
                float k = Math.Max((float)w / picture.Width, (float)h / picture.Height);
                int sw = (int)Math.Ceiling(picture.Width * k), sh = (int)Math.Ceiling(picture.Height * k);
                picScaled = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(picScaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(picture, (w - sw) / 2, (h - sh) / 2, sw, sh);
                }
            }
            return Img.Bytes(picScaled);
        }
    }
}

// Runs Robust Video Matting on each frame and hands back a frame-size 0..1 mask (1 = person).
// The model remembers the last frames (its "recurrent state"), which is what keeps the outline
// steady from one frame to the next. It is GPL-3.0, so it is not packed into snip.exe: Snip asks
// once and downloads it from the authors' GitHub release into its own folder.
class Segmenter
{
    const string ModelUrl = "https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx";
    const long ModelSize = 14975696;
    static readonly string ModelPath = Path.Combine(Installer.Dir, "rvm_mobilenetv3_fp32.onnx");
    Windows.AI.MachineLearning.LearningModelSession session;
    volatile bool loading;
    public static volatile bool Broken;

    public static bool HaveModel { get { try { return new FileInfo(ModelPath).Length == ModelSize; } catch { return false; } } }

    // Ask once and download the model. True when it is on disk.
    public static bool Ensure()
    {
        if (HaveModel) return true;
        if (MessageBox.Show("The background effect needs an AI model that finds you in the picture: Robust Video Matting, " +
                "free under the GPL-3.0 licence, which is not on this computer.\n\n" +
                "Download it now? About 15 MB, from github.com, kept inside Snip's own folder.",
                "Snip", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
        using (var f = new DownloadForm("Snip - downloading the background model", ModelUrl, ModelPath, null)) f.ShowDialog();
        if (HaveModel) return true;
        try { File.Delete(ModelPath); } catch { }
        return false;
    }

    // Load the model in the background; the first run on the GPU takes a few seconds, so do it now.
    public void Warm()
    {
        if (session != null || loading || Broken || !HaveModel) return;
        loading = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var model = Windows.AI.MachineLearning.LearningModel.LoadFromFilePath(ModelPath);
                Windows.AI.MachineLearning.LearningModelSession s;
                try { s = new Windows.AI.MachineLearning.LearningModelSession(model, new Windows.AI.MachineLearning.LearningModelDevice(Windows.AI.MachineLearning.LearningModelDeviceKind.DirectXHighPerformance)); Reset(); Run(s, new float[3 * 64 * 64], 64, 64, 1); }
                catch { s = new Windows.AI.MachineLearning.LearningModelSession(model, new Windows.AI.MachineLearning.LearningModelDevice(Windows.AI.MachineLearning.LearningModelDeviceKind.Cpu)); }
                Reset();
                session = s;
            }
            catch { Broken = true; }
            finally { loading = false; }
        });
    }

    // The four recurrent tensors, fed back in on the next frame; zeros to start from scratch.
    Windows.AI.MachineLearning.TensorFloat r1, r2, r3, r4;
    void Reset()
    {
        r1 = Zero(); r2 = Zero(); r3 = Zero(); r4 = Zero();
    }
    static Windows.AI.MachineLearning.TensorFloat Zero() { return Windows.AI.MachineLearning.TensorFloat.CreateFromArray(new long[] { 1, 1, 1, 1 }, new float[1]); }

    float[] Run(Windows.AI.MachineLearning.LearningModelSession s, float[] src, int w, int h, float ratio)
    {
        var b = new Windows.AI.MachineLearning.LearningModelBinding(s);
        b.Bind("src", Windows.AI.MachineLearning.TensorFloat.CreateFromArray(new long[] { 1, 3, h, w }, src));
        b.Bind("r1i", r1); b.Bind("r2i", r2); b.Bind("r3i", r3); b.Bind("r4i", r4);
        b.Bind("downsample_ratio", Windows.AI.MachineLearning.TensorFloat.CreateFromArray(new long[] { 1 }, new[] { ratio }));
        var r = s.Evaluate(b, "");
        r1 = (Windows.AI.MachineLearning.TensorFloat)r.Outputs["r1o"]; r2 = (Windows.AI.MachineLearning.TensorFloat)r.Outputs["r2o"];
        r3 = (Windows.AI.MachineLearning.TensorFloat)r.Outputs["r3o"]; r4 = (Windows.AI.MachineLearning.TensorFloat)r.Outputs["r4o"];
        return System.Linq.Enumerable.ToArray(((Windows.AI.MachineLearning.TensorFloat)r.Outputs["pha"]).GetAsVectorView());
    }

    public class MaskData { public float[] A; public int W, H; }
    public volatile MaskData Latest;     // newest person mask (0..1), frame-sized
    volatile bool busy;
    float[] input; int lastW, lastH;

    // Hand over a frame; it is only copied and worked on when the last one is finished.
    long lastRun;
    public void Offer(byte[] px, int w, int h)
    {
        var s = session;
        if (s == null) { Warm(); return; }
        if (busy) return;
        // about 15 runs a second is plenty: the model carries the outline between runs, and the
        // recording's own ffmpeg needs the CPU more than we do
        long now = Environment.TickCount;
        if (now - lastRun < 60) return;
        lastRun = now;
        busy = true;
        var copy = (byte[])px.Clone();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            try { var m = Mask(s, copy, w, h); if (m != null) Latest = m; } catch { } finally { busy = false; }
        });
    }

    MaskData Mask(Windows.AI.MachineLearning.LearningModelSession s, byte[] px, int w, int h)
    {
        int plane = w * h;
        if (w != lastW || h != lastH) { input = new float[3 * plane]; Reset(); lastW = w; lastH = h; Latest = null; }
        for (int i = 0, j = 0; i < plane; i++, j += 4)
        {
            input[i] = px[j + 2] / 255f;              // R
            input[plane + i] = px[j + 1] / 255f;      // G
            input[2 * plane + i] = px[j] / 255f;      // B
        }
        // the model looks at a shrunk copy (about 320 px across is plenty for a webcam) and
        // refines the edge back at full size
        float ratio = Math.Min(1f, Math.Max(0.25f, 320f / Math.Max(w, h)));
        return new MaskData { A = Run(s, input, w, h, ratio), W = w, H = h };
    }
}

// BGRA byte arrays <-> bitmaps, and smooth resizing without dark edges.
static class Img
{
    public static Bitmap FromBytes(byte[] px, int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        if (d.Stride == w * 4) Marshal.Copy(px, 0, d.Scan0, w * h * 4);
        else for (int y = 0; y < h; y++) Marshal.Copy(px, y * w * 4, d.Scan0 + y * d.Stride, w * 4);
        b.UnlockBits(d);
        return b;
    }

    public static byte[] Bytes(Bitmap b)
    {
        int w = b.Width, h = b.Height;
        var px = new byte[w * h * 4];
        var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        if (d.Stride == w * 4) Marshal.Copy(d.Scan0, px, 0, px.Length);
        else for (int y = 0; y < h; y++) Marshal.Copy(d.Scan0 + y * d.Stride, px, y * w * 4, w * 4);
        b.UnlockBits(d);
        return px;
    }

    public static Bitmap Scale(Bitmap src, int w, int h)
    {
        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        using (var ia = new ImageAttributes())
        {
            ia.SetWrapMode(WrapMode.TileFlipXY);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        }
        return dst;
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
// With Ctrl held (or opened from "Record an area") the box becomes a recording instead.
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

    bool Recording { get { return recordMode || (ModifierKeys & Keys.Control) == Keys.Control; } }

    string Hint
    {
        get
        {
            if (Recorder.Active) return "Recording in progress  ·  drag a box to copy it  ·  Esc cancels";
            if (Recording) return "Drag a box to record  ·  then press Record above it  ·  Esc cancels";
            return "Drag a box to copy it  ·  hold Ctrl to record a video instead  ·  Esc cancels";
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

    // Ctrl changes the text and so the pill's width: repaint the whole strip it can occupy.
    Rectangle HintBand { get { Rectangle p = Screen.PrimaryScreen.Bounds; return new Rectangle(p.Left - Left, p.Top - Top, p.Width, 100); } }

    protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
        if (e.KeyCode == Keys.ControlKey) Invalidate(HintBand);
    }
    protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.ControlKey) Invalidate(HintBand); }

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
