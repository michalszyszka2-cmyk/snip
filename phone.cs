// The iPhone as a second camera. Snip runs a tiny HTTPS server on the Wi-Fi; the phone opens
// https://<pc>.local:8787 in Safari (phone.html, packed into the exe), which sends its camera
// as JPEG frames over a WebSocket. PhoneBubble shows the latest frame in a small layered
// window beside the webcam bubble, so it is simply part of what gets recorded.
//
// Safari only hands a page the camera over HTTPS, so Snip makes itself a certificate once
// (phone.pfx next to the exe) and the phone installs it one time from the plain-HTTP setup
// page at http://<pc>.local:8788 (Safari treats a .cer download as a profile to install).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class PhoneCam
{
    public const int Port = 8787, SetupPort = 8788;
    const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    static X509Certificate2 cert;
    static TcpListener tls, plain;
    static readonly List<TcpClient> open = new List<TcpClient>();
    static Control ui;                      // frames and status are raised on the UI thread
    static int phones;                       // connected phone pages (Interlocked)
    static volatile bool decoding;
    public static event Action<Bitmap> Frame;   // on the UI thread; the bitmap is the receiver's to dispose
    public static event Action Changed;         // a phone connected or went away
    public static event Action<string> Shot;    // a picture or video sent from the phone's /shot page: saved here, on the clipboard
    public static string Error { get; private set; }

    public static bool Connected { get { return phones > 0; } }
    public static string Host { get { return Environment.MachineName.ToLowerInvariant() + ".local"; } }
    public static string Address { get { return "https://" + Host + ":" + Port; } }
    public static string SetupAddress { get { return "http://" + Host + ":" + SetupPort; } }

    // Only while the iPhone camera is switched on: anyone on the same network can reach these ports.
    public static void Start(Control marshal)
    {
        if (tls != null) return;
        ui = marshal;
        Error = null;
        try
        {
            cert = LoadOrMakeCert();
            tls = new TcpListener(IPAddress.Any, Port); tls.Start();
            plain = new TcpListener(IPAddress.Any, SetupPort); plain.Start();
        }
        catch (Exception ex) { Stop(); Error = ex.Message; return; }
        var secure = tls; var setup = plain;
        new Thread(() => Accept(secure, true)) { IsBackground = true }.Start();
        new Thread(() => Accept(setup, false)) { IsBackground = true }.Start();
    }

    // Closes both ports and drops any phone still connected.
    public static void Stop()
    {
        Error = null;
        var a = tls; var b = plain;
        tls = null; plain = null;
        if (a != null) try { a.Stop(); } catch { }
        if (b != null) try { b.Stop(); } catch { }
        lock (open) { foreach (var c in open) try { c.Close(); } catch { } open.Clear(); }
    }

    // The LAN addresses the phone might use, for the certificate; the .local name is the one shown.
    static List<IPAddress> LanAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(a.Address);
            }
        }
        catch { }
        return list;
    }

    // One self-signed certificate, kept next to the exe; remade when it is near its end or the
    // PC's addresses have changed (the phone then has to install it again).
    static X509Certificate2 LoadOrMakeCert()
    {
        string path = Path.Combine(Installer.Dir, "phone.pfx");
        const string pwd = "snip";
        var ips = LanAddresses();
        if (File.Exists(path))
        {
            try
            {
                var c = new X509Certificate2(File.ReadAllBytes(path), pwd, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
                bool fresh = c.NotAfter > DateTime.Now.AddDays(30);
                string san = "";
                foreach (var ext in c.Extensions) if (ext.Oid.Value == "2.5.29.17") san = ext.Format(false);
                bool covers = true;
                foreach (var ip in ips) if (san.IndexOf(ip.ToString(), StringComparison.Ordinal) < 0) covers = false;
                if (fresh && covers) return c;
            }
            catch { }
        }
        using (var rsa = new RSACng(2048))
        {
            var req = new CertificateRequest("CN=Snip on " + Environment.MachineName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyCertSign, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(Host); san.AddDnsName(Environment.MachineName.ToLowerInvariant()); san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            foreach (var ip in ips) san.AddIpAddress(ip);
            req.CertificateExtensions.Add(san.Build());
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
            using (var made = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(800)))   // iOS: at most 825 days
            {
                byte[] pfx = made.Export(X509ContentType.Pfx, pwd);
                File.WriteAllBytes(path, pfx);
                return new X509Certificate2(pfx, pwd, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
            }
        }
    }

    static void Accept(TcpListener l, bool secure)
    {
        while (true)
        {
            TcpClient c;
            try { c = l.AcceptTcpClient(); } catch { return; }
            lock (open) open.Add(c);
            new Thread(() =>
            {
                try { Serve(c, secure); } catch { }
                finally { lock (open) open.Remove(c); try { c.Close(); } catch { } }
            }) { IsBackground = true }.Start();
        }
    }

    static void Serve(TcpClient c, bool secure)
    {
        c.NoDelay = true;
        Stream s = c.GetStream();
        if (secure)
        {
            var ssl = new SslStream(s, false);
            ssl.AuthenticateAsServer(cert, false, SslProtocols.Tls12, false);
            s = ssl;
        }
        s.ReadTimeout = 15000;
        // request line + headers
        var head = new MemoryStream();
        var one = new byte[1];
        while (head.Length < 16384)
        {
            if (s.Read(one, 0, 1) <= 0) return;
            head.WriteByte(one[0]);
            long n = head.Length;
            if (n >= 4) { var b = head.GetBuffer(); if (b[n - 4] == '\r' && b[n - 3] == '\n' && b[n - 2] == '\r' && b[n - 1] == '\n') break; }
        }
        string text = Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length);
        string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return;
        string[] rq = lines[0].Split(' ');
        if (rq.Length < 2) return;
        string path = rq[1].Split('?')[0];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++) { int k = lines[i].IndexOf(':'); if (k > 0) headers[lines[i].Substring(0, k).Trim()] = lines[i].Substring(k + 1).Trim(); }

        if (path == "/icon-camera.png" || path == "/icon-shot.png" || path == "/apple-touch-icon.png")
        { Reply(s, "200 OK", "image/png", HomeIcon(path == "/icon-shot.png"), null); return; }
        if (path == "/shot")
        {
            if (rq[0] == "POST") { TakeShot(s, headers); return; }
            Reply(s, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(ShotPage), null); return;
        }
        if (path == "/snip.cer") { Reply(s, "200 OK", "application/x-x509-ca-cert", cert.Export(X509ContentType.Cert), "Content-Disposition: attachment; filename=\"snip.cer\"\r\n"); return; }
        if (!secure) { Reply(s, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(SetupPage()), null); return; }
        if (path == "/" || path == "/index.html") { Reply(s, "200 OK", "text/html; charset=utf-8", Resource("phone.html"), null); return; }
        if (path == "/ws" && headers.ContainsKey("Sec-WebSocket-Key"))
        {
            string accept;
            using (var sha = SHA1.Create()) accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(headers["Sec-WebSocket-Key"] + WsGuid)));
            byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
            s.Write(ok, 0, ok.Length);
            s.ReadTimeout = Timeout.Infinite;
            Interlocked.Increment(ref phones); Notify();
            try { Frames(s); }
            finally { Interlocked.Decrement(ref phones); Notify(); }
            return;
        }
        Reply(s, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("Not here."), null);
    }

    // A picture or video sent from the phone: streamed to Downloads, then put on the clipboard
    // (pictures as a picture and a file, videos as a file - paste with Ctrl+V either way).
    static void TakeShot(Stream s, Dictionary<string, string> headers)
    {
        long len; string cl;
        if (!headers.TryGetValue("Content-Length", out cl) || !long.TryParse(cl, out len) || len <= 0 || len > 4000000000L)
        { Reply(s, "400 Bad Request", "text/plain", Encoding.ASCII.GetBytes("Nothing sent, or over 4 GB."), null); return; }
        string type; headers.TryGetValue("Content-Type", out type); type = (type ?? "").ToLowerInvariant();
        bool video = type.StartsWith("video/");
        string ext = type.Contains("png") ? ".png" : type.Contains("heic") ? ".heic" : type.Contains("quicktime") ? ".mov" :
                     type.Contains("mp4") ? ".mp4" : type.Contains("webm") ? ".webm" : video ? ".mov" : ".jpg";
        string file = Path.Combine(Recorder.DownloadsDir(), "iphone-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ext);
        string part = file + ".part";
        s.ReadTimeout = 60000;
        try
        {
            using (var f = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buf = new byte[1 << 20]; long left = len;
                while (left > 0) { int n = s.Read(buf, 0, (int)Math.Min(buf.Length, left)); if (n <= 0) throw new IOException("cut off"); f.Write(buf, 0, n); left -= n; }
            }
            File.Move(part, file);
        }
        catch { try { File.Delete(part); } catch { } throw; }
        Reply(s, "200 OK", "text/plain", Encoding.ASCII.GetBytes("ok"), null);
        ui.BeginInvoke(new Action(() =>
        {
            try
            {
                var d = new DataObject();
                if (!video && len < 60000000)
                    try { using (var m = new MemoryStream(File.ReadAllBytes(file))) using (var img = Image.FromStream(m)) d.SetImage(new Bitmap(img)); } catch { }   // HEIC will not decode: file only
                d.SetFileDropList(new System.Collections.Specialized.StringCollection { file });
                Clipboard.SetDataObject(d, true);
            }
            catch { }
            if (Shot != null) Shot(file);
        }));
    }

    // Home Screen icons (180 px, iOS rounds the corners itself): red with a camera for the
    // camera page, blue with an arrow up out of a tray for Send to PC.
    static readonly byte[][] icons = new byte[2][];
    static byte[] HomeIcon(bool shot)
    {
        int ix = shot ? 1 : 0;
        if (icons[ix] != null) return icons[ix];
        using (var b = new Bitmap(180, 180, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(b))
            using (var white = new SolidBrush(Color.White))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(shot ? Color.FromArgb(47, 111, 228) : Color.FromArgb(232, 52, 46));
                if (!shot)
                {
                    using (var body = RecFrame.Rounded(new Rectangle(34, 58, 112, 80), 16)) g.FillPath(white, body);
                    using (var hump = RecFrame.Rounded(new Rectangle(66, 44, 48, 26), 8)) g.FillPath(white, hump);
                    using (var red = new SolidBrush(Color.FromArgb(232, 52, 46))) g.FillEllipse(red, 63, 71, 54, 54);
                    g.FillEllipse(white, 74, 82, 32, 32);
                }
                else
                {
                    using (var pen = new Pen(Color.White, 13) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    {
                        g.DrawLines(pen, new[] { new Point(42, 104), new Point(42, 138), new Point(138, 138), new Point(138, 104) });   // tray
                        g.DrawLine(pen, 90, 112, 90, 42);                                                                        // shaft
                        g.DrawLines(pen, new[] { new Point(64, 68), new Point(90, 42), new Point(116, 68) });                    // head
                    }
                }
            }
            using (var m = new MemoryStream()) { b.Save(m, ImageFormat.Png); return icons[ix] = m.ToArray(); }
        }
    }

    const string ShotPage = @"<!doctype html><html lang=en><head><meta charset=utf-8>
<meta name=viewport content='width=device-width,initial-scale=1'>
<meta name=apple-mobile-web-app-capable content=yes><meta name=apple-mobile-web-app-title content='Send to PC'>
<link rel=apple-touch-icon href='/icon-shot.png'><title>Send to PC</title><style>
body{margin:0;min-height:100vh;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:18px;background:#111;color:#eee;font:17px/1.4 -apple-system,system-ui,sans-serif;text-align:center;padding:24px;box-sizing:border-box}
label{background:#2f6fe4;color:#fff;border-radius:28px;padding:18px 34px;font-size:19px}
input{display:none}#st{color:#c9c9c4;max-width:320px}img,video{max-width:80vw;max-height:40vh;border-radius:10px}
progress{width:70vw;height:10px}
</style></head><body>
<label>Send to the PC<input id=f type=file accept='image/*,video/*'></label>
<div id=st>Pick a screenshot, photo or video, or take a new one. It goes to the PC clipboard: paste it with Ctrl+V.</div>
<progress id=pr max=1 value=0 hidden></progress>
<img id=pv hidden><video id=vv hidden muted playsinline></video>
<script>
const f=document.getElementById('f'),st=document.getElementById('st'),pv=document.getElementById('pv'),vv=document.getElementById('vv'),pr=document.getElementById('pr');
const mb=n=>(n/1048576).toFixed(n<10485760?1:0)+' MB';
f.onchange=()=>{const x=f.files[0];if(!x)return;const isV=(x.type||'').startsWith('video/');
const u=URL.createObjectURL(x);pv.hidden=isV;vv.hidden=!isV;if(isV)vv.src=u;else pv.src=u;
const q=new XMLHttpRequest();q.open('POST','/shot');q.setRequestHeader('Content-Type',x.type||'image/jpeg');
pr.hidden=false;pr.value=0;st.textContent='Sending '+mb(x.size)+'...';
q.upload.onprogress=e=>{if(e.lengthComputable){pr.value=e.loaded/e.total;st.textContent='Sending... '+mb(e.loaded)+' of '+mb(e.total);}};
q.onload=()=>{pr.hidden=true;st.textContent=q.status==200?'Sent. On the PC press Ctrl+V to paste it.':'Did not arrive ('+q.status+').';};
q.onerror=()=>{pr.hidden=true;st.textContent='Could not reach the PC. Same Wi-Fi, and Snip running?';};
q.send(x);f.value='';};
</script></body></html>";

    static void Reply(Stream s, string status, string type, byte[] body, string extra)
    {
        byte[] h = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
            "\r\nCache-Control: no-store\r\nConnection: close\r\n" + (extra ?? "") + "\r\n");
        s.Write(h, 0, h.Length); s.Write(body, 0, body.Length); s.Flush();
    }

    static byte[] Resource(string name)
    {
        using (var r = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        { var m = new MemoryStream(); r.CopyTo(m); return m.ToArray(); }
    }

    // WebSocket frames from the phone: binary = one JPEG; ping answered; close ends it.
    static void Frames(Stream s)
    {
        var hdr = new byte[8];
        while (true)
        {
            ReadFull(s, hdr, 2);
            int op = hdr[0] & 0x0F; bool masked = (hdr[1] & 0x80) != 0;
            long len = hdr[1] & 0x7F;
            if (len == 126) { ReadFull(s, hdr, 2); len = (hdr[0] << 8) | hdr[1]; }
            else if (len == 127) { ReadFull(s, hdr, 8); len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | hdr[i]; }
            if (len > 8000000) return;
            var mask = new byte[4];
            if (masked) ReadFull(s, mask, 4);
            var data = new byte[len];
            ReadFull(s, data, (int)len);
            if (masked) for (int i = 0; i < data.Length; i++) data[i] ^= mask[i & 3];
            if (op == 8) return;
            if (op == 9) { Send(s, 10, data); continue; }
            if (op != 2 || decoding) continue;   // text frames are not used; drop a frame while the last decodes
            decoding = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Bitmap bmp = null;
                try
                {
                    using (var m = new MemoryStream(data)) using (var raw = new Bitmap(m)) bmp = new Bitmap(raw);   // detach from the stream
                    if (Frame != null && Bg.Phone.Mode != "none")   // blur / picture behind the person, off the UI thread
                    {
                        int w = bmp.Width, h = bmp.Height;
                        var px = Img.Bytes(bmp);
                        try { Bg.Phone.Apply(px, w, h); } catch { }
                        bmp.Dispose(); bmp = null;
                        bmp = Img.FromBytes(px, w, h);
                    }
                    var b = bmp;
                    ui.BeginInvoke(new Action(() => { try { if (Frame != null) Frame(b); else b.Dispose(); } finally { decoding = false; } }));
                }
                catch { if (bmp != null) bmp.Dispose(); decoding = false; }
            });
        }
    }

    static void Send(Stream s, int op, byte[] data)
    {
        var m = new MemoryStream();
        m.WriteByte((byte)(0x80 | op));
        if (data.Length < 126) m.WriteByte((byte)data.Length);
        else { m.WriteByte(126); m.WriteByte((byte)(data.Length >> 8)); m.WriteByte((byte)data.Length); }
        m.Write(data, 0, data.Length);
        lock (s) { m.WriteTo(s); s.Flush(); }
    }

    static void ReadFull(Stream s, byte[] buf, int n)
    {
        int got = 0;
        while (got < n) { int k = s.Read(buf, got, n - got); if (k <= 0) throw new IOException("closed"); got += k; }
    }

    static void Notify()
    {
        try { ui.BeginInvoke(new Action(() => { if (Changed != null) Changed(); })); } catch { }
    }

    static string SetupPage()
    {
        return "<!doctype html><html><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>Snip on this iPhone</title>" +
            "<style>body{font:17px/1.5 -apple-system,system-ui,sans-serif;margin:0;padding:28px 22px;background:#f4f3ef;color:#1d1d1b}h1{font-size:24px;margin:0 0 18px}" +
            "ol{padding-left:22px}li{margin:0 0 14px}a.b{display:inline-block;background:#e8342e;color:#fff;text-decoration:none;padding:12px 22px;border-radius:24px;font-weight:600}" +
            "code{background:#e6e4dd;padding:2px 6px;border-radius:4px}</style></head><body>" +
            "<h1>Use this iPhone as Snip's second camera</h1><p>One-time setup, so Safari lets the page use the camera.</p><ol>" +
            "<li><a class=b href='/snip.cer'>Download Snip's certificate</a><br>Safari asks to download a profile: allow it.</li>" +
            "<li>Open <b>Settings</b>. Tap <b>Profile Downloaded</b> at the top and install it.</li>" +
            "<li>In Settings go to <b>General &rsaquo; About &rsaquo; Certificate Trust Settings</b> and switch on <b>Snip on " + Environment.MachineName + "</b>.</li>" +
            "<li>Open <a href='" + Address + "'><code>" + Address + "</code></a> in Safari and tap <b>Use this camera</b>. Add it to the Home Screen to keep it handy.</li>" +
            "</ol><p>On the PC, turn on <b>iPhone camera</b> in Snip's tray panel before you record.</p></body></html>";
    }
}

// Small bubble beside the webcam showing the phone's picture. Same window technique as
// CamBubble (layered, so the shape has soft edges), the saved camera shape, draggable.
class PhoneBubble : Form
{
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    readonly int fw, fh;
    readonly string shape;
    Point grab; bool dragging;

    public PhoneBubble()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        shape = CamBubble.SavedShape();
        int small = (int)(150 * Dpi.Scale);   // the webcam's "small" size: the phone is the lesser angle
        if (shape == "wide") { fh = (small * 3 / 4) & ~1; fw = (fh * 16 / 9) & ~1; }
        else fw = fh = small & ~1;
        Size = new Size(fw, fh);
        PhoneCam.Frame += OnFrame;
        PhoneCam.Changed += OnChanged;
        var menu = new ContextMenuStrip();
        menu.Items.Add("-");
        menu.Opening += (s, e) => { menu.Items.Clear(); Bg.Phone.AddMenuItems(menu.Items); e.Cancel = false; };
        ContextMenuStrip = menu;
        Bg.Phone.Warm();
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80 | 0x08000000 | 0x8; return cp; }   // LAYERED, TOOLWINDOW, NOACTIVATE, TOPMOST
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override void OnShown(EventArgs e) { base.OnShown(e); Present(null); }

    void OnFrame(Bitmap b)
    {
        try { if (!IsDisposed) Present(b); }
        finally { b.Dispose(); }
    }
    void OnChanged() { if (!PhoneCam.Connected) Present(null); }

    GraphicsPath Shape(Rectangle r)
    {
        var p = new GraphicsPath();
        if (shape == "circle") { p.AddEllipse(r); return p; }
        return RecFrame.Rounded(r, Math.Min(fw, fh) / 8);
    }

    // Centre-crop the phone's picture (portrait or landscape) into the bubble's shape.
    void Present(Bitmap pic)
    {
        if (IsDisposed || !IsHandleCreated) return;
        using (var canvas = new Bitmap(fw, fh, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.Clear(Color.Transparent);
                var r = new Rectangle(0, 0, fw - 1, fh - 1);
                using (var path = Shape(r))
                {
                    if (pic != null)
                    {
                        float k = Math.Max((float)fw / pic.Width, (float)fh / pic.Height);
                        int w = (int)(pic.Width * k + 0.5f), h = (int)(pic.Height * k + 0.5f);
                        g.SetClip(path);
                        g.DrawImage(pic, (fw - w) / 2, (fh - h) / 2, w, h);
                        g.ResetClip();
                    }
                    else using (var b = new SolidBrush(Color.FromArgb(48, 48, 45))) g.FillPath(b, path);
                }
                if (pic == null)
                    TextRenderer.DrawText(g, PhoneCam.Connected ? "iPhone..." : "Open " + PhoneCam.Address + "\non the iPhone", new Font("Segoe UI", 8.5f),
                        new Rectangle(8, 0, fw - 16, fh), Color.FromArgb(190, 190, 184), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                if (shape == "circle")
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
        if (e.Button == MouseButtons.Left) { dragging = true; grab = e.Location; Cursor = Cursors.SizeAll; }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        Cursor = Cursors.SizeAll;
        if (dragging) { var c = Control.MousePosition; Location = new Point(c.X - grab.X, c.Y - grab.Y); }
    }
    protected override void OnMouseUp(MouseEventArgs e) { dragging = false; }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { PhoneCam.Frame -= OnFrame; PhoneCam.Changed -= OnChanged; }
        base.Dispose(disposing);
    }
}
