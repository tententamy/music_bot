// Pucca Bot – Desktop Pet  (C# WinForms .NET 8)
// Per-pixel alpha via UpdateLayeredWindow (no green fringe)

using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace VoSiBot;

public partial class Form1 : Form
{
    // ── Win32 per-pixel alpha ─────────────────────────────
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int    ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")]  static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")]  static extern IntPtr SelectObject(IntPtr hdc, IntPtr hobj);
    [DllImport("gdi32.dll")]  static extern bool   DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")]  static extern bool   DeleteObject(IntPtr hobj);
    [DllImport("user32.dll")] static extern bool   UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref WPOINT pptDst, ref WSIZE psize,
        IntPtr hdcSrc, ref WPOINT pptSrc, uint crKey,
        ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("kernel32.dll")] static extern bool Beep(int freq, int dur);

    [StructLayout(LayoutKind.Sequential)] struct WPOINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct WSIZE  { public int CX, CY; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION
    { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    // WS_EX_LAYERED — cho phép per-pixel alpha
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x00080000; return cp; }
    }

    // ── Kích thước cửa sổ ────────────────────────────────
    const int W = 170, H = 260;  // cao hơn để bubble không bị cắt

    // ── Vị trí (trung tâm bot) ───────────────────────────
    float bx, by;

    // ── Trạng thái ───────────────────────────────────────
    string state   = "idle";
    int    frameT  = 0;
    int    attackT = 0;

    // ── Drag ─────────────────────────────────────────────
    bool  dragging;
    float dox, doy;
    readonly List<PointF> dragHist = new();

    // ── Bubble ───────────────────────────────────────────
    string? bubble;
    int     bubbleT;

    // ── Floaties ─────────────────────────────────────────
    readonly List<Note> floaties = new();
    readonly Random     rng      = new();

    // ── Timers ────────────────────────────────────────────
    readonly System.Windows.Forms.Timer loopTimer  = new() { Interval = 16  };
    readonly System.Windows.Forms.Timer clickTimer = new() { Interval = 260 };
    bool clickPending;

    // ── Tray ─────────────────────────────────────────────
    NotifyIcon tray = new();

    // ── Nhạc ─────────────────────────────────────────────
    static readonly int[] SongFreqs = { 523, 659, 784, 880, 784, 659, 523, 440 };
    int songIdx;

    // ── Audio (NAudio + yt-dlp) ──────────────────────────
    WaveOutEvent?          waveOut;
    WaveStream? audioReader;
    bool   audioPaused  = false;
    int    audioSession = 0;
    readonly List<TrackInfo> playlist = new();
    int playlistIdx = 0;

    // ── Sprite tùy chỉnh (null = vẽ GDI+) ───────────────
    Bitmap?      sprite;
    SpriteSheet? spriteSheet;
    long         spriteMs;   // thời gian animation (ms)

    // ── Mini player ───────────────────────────────────────
    MiniPlayer? miniPlayer;

    // ─────────────────────────────────────────────────────
    public Form1()
    {
        InitializeComponent();

        var scr = Screen.PrimaryScreen!.Bounds;
        bx = scr.Width / 2f;
        by = scr.Height - 180f;

        // Không dùng TransparencyKey nữa — UpdateLayeredWindow lo hết
        FormBorderStyle = FormBorderStyle.None;
        TopMost         = true;
        ShowInTaskbar   = false;
        DoubleBuffered  = true;
        Size            = new Size(W, H);
        StartPosition   = FormStartPosition.Manual;
        MoveWin();

        MouseDown        += OnDown;
        MouseMove        += OnMove;
        MouseUp          += OnUp;
        MouseClick       += OnClick;
        MouseDoubleClick += OnDbl;

        var ctx = new ContextMenuStrip();
        ctx.Items.Add("🖼 Load ảnh / sprite",       null, OnLoadSprite);
        ctx.Items.Add("📁 Load thư mục PNG",       null, OnLoadFolder);
        ctx.Items.Add("🎵 YouTube",              null, OnYouTube);
        ctx.Items.Add("➕ Thêm bài vào playlist", null, OnAddToPlaylist);
        ctx.Items.Add("🎛 Mini Player",          null, (_, _) => ToggleMiniPlayer());
        ctx.Items.Add("⏭ Bài tiếp",              null, (_, _) => NextTrack());
        ctx.Items.Add("🗑 Xóa playlist",        null, (_, _) => { StopAudio(); playlist.Clear(); playlistIdx = 0; Say("Đã xóa playlist"); });
        ctx.Items.Add(new ToolStripSeparator());
        ctx.Items.Add("Ẩn",               null, (_, _) => Hide());
        ctx.Items.Add("Hiện",             null, (_, _) => { Show(); Activate(); });
        ctx.Items.Add(new ToolStripSeparator());
        ctx.Items.Add("Thoát",            null, (_, _) => { StopAudio(); tray.Dispose(); Application.Exit(); });
        ContextMenuStrip = ctx;
        SetupTray(ctx);

        loopTimer.Tick  += (_, _) => { GameTick(); PushFrame(); };
        loopTimer.Start();
        clickTimer.Tick += (_, _) => { clickTimer.Stop(); DoAttack(); };

        var gt = new System.Windows.Forms.Timer { Interval = 700 };
        gt.Tick += (_, _) => { Say("Chào bạn! ◕‿◕"); gt.Stop(); gt.Dispose(); };
        gt.Start();

        // Vẽ frame đầu tiên ngay
        PushFrame();
    }

    // ── Load sprite / sprite sheet ────────────────────────
    void OnLoadSprite(object? s, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title  = "Chọn ảnh PNG hoặc Aseprite JSON",
            Filter = "Aseprite JSON|*.json|PNG/Image|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Tất cả|*.*"
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        string path = dlg.FileName;
        try
        {
            if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                // Sprite sheet từ Aseprite
                var ss = SpriteSheet.Load(path);
                if (ss == null) { Say("❌ JSON không hợp lệ"); return; }
                spriteSheet?.Dispose();
                spriteSheet = ss;
                sprite?.Dispose(); sprite = null;
                spriteMs = 0;
                Say("✨ Sprite sheet loaded!");
            }
            else
            {
                // Ảnh PNG đơn
                spriteSheet?.Dispose(); spriteSheet = null;
                sprite?.Dispose();
                var raw = new Bitmap(path);
                sprite  = RemoveBackground(raw);
                raw.Dispose();
                Say("Hình đẹp quá! ✨");
            }
        }
        catch { Say("Lỗi load ảnh 😅"); }
    }

    // ── Load PNG sequence từ thư mục ─────────────────────
    void OnLoadFolder(object? s, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description         = "Chọn thư mục chứa PNG sequence\n(subfolder idle/, dance/... hoặc file idle_0.png...)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        Say("⏳ Đang load...");
        var ss = SpriteSheet.LoadFromFolder(dlg.SelectedPath);
        if (ss == null) { Say("❌ Không tìm được PNG trong thư mục"); return; }

        // Hỏi người dùng có muốn xóa nền + bo tròn không
        var answer = MessageBox.Show(
            "✂️ Cắt nền và bo tròn hình?",
            "VoSiBot",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
        {
            Say("✂️ Đang xử lý hình...");
            ss.ApplyToAllFrames(bmp => SpriteSheet.ProcessFrame(bmp, removeBg: true, circularCrop: true));
        }

        spriteSheet?.Dispose();
        spriteSheet = ss;
        sprite?.Dispose(); sprite = null;
        spriteMs = 0;
        string tags = string.Join(", ", ss.TagNames);
        Say($"✨ Loaded! Tags: {tags}");
    }

    // Xóa nền trắng + pixel ngoài vòng tròn
    static Bitmap RemoveBackground(Bitmap src)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);

        // Tâm và bán kính vòng nội tiếp (dùng 47% cạnh nhỏ hơn)
        float cx = src.Width  / 2f;
        float cy = src.Height / 2f;
        float r  = Math.Min(cx, cy) * 0.97f; // để hơi rộng hơn chút

        var srcData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height),
                                   ImageLockMode.ReadOnly,
                                   PixelFormat.Format32bppArgb);
        var dstData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height),
                                   ImageLockMode.WriteOnly,
                                   PixelFormat.Format32bppArgb);

        int bytes   = srcData.Stride * src.Height;
        byte[] buf  = new byte[bytes];
        byte[] dBuf = new byte[bytes];
        System.Runtime.InteropServices.Marshal.Copy(srcData.Scan0, buf,  0, bytes);
        System.Runtime.InteropServices.Marshal.Copy(dstData.Scan0, dBuf, 0, bytes);

        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                int i = y * srcData.Stride + x * 4;
                byte b = buf[i], g2 = buf[i+1], r2 = buf[i+2], a = buf[i+3];

                // Pixel ngoài vòng tròn → transparent
                float dx = x - cx, dy = y - cy;
                if (dx*dx + dy*dy > r*r) { dBuf[i+3] = 0; continue; }

                // Pixel trắng/gần trắng → transparent
                // (nền trắng của ảnh chibi kiểu này thường >= 240 cả 3 kênh)
                int brightness = (r2 + g2 + b) / 3;
                if (a > 0 && brightness > 235 && Math.Max(r2, Math.Max(g2, b)) - Math.Min(r2, Math.Min(g2, b)) < 30)
                { dBuf[i+3] = 0; continue; }

                // Vàng/nâu của vòng rune (khoảng R>180, G>140, B<80 với saturation cao)
                // → cũng xóa luôn nếu muốn bỏ cả vòng vàng
                if (r2 > 160 && g2 > 120 && b < 80 && a > 0)
                { dBuf[i+3] = 0; continue; }

                dBuf[i]   = b;
                dBuf[i+1] = g2;
                dBuf[i+2] = r2;
                dBuf[i+3] = a > 0 ? a : (byte)255;
            }
        }

        System.Runtime.InteropServices.Marshal.Copy(dBuf, 0, dstData.Scan0, bytes);
        src.UnlockBits(srcData);
        dst.UnlockBits(dstData);
        return dst;
    }

    // ── Tray ─────────────────────────────────────────────
    void SetupTray(ContextMenuStrip menu)
    {
        var bmp = new Bitmap(32, 32);
        using (var gg = Graphics.FromImage(bmp))
        {
            gg.Clear(Color.Transparent);
            gg.SmoothingMode = SmoothingMode.AntiAlias;
            gg.FillEllipse(Brushes.Black, 4, 8, 24, 21);
            gg.FillEllipse(Brushes.Black, 2, 2, 12, 12);
            gg.FillEllipse(Brushes.Black, 18, 2, 12, 12);
            gg.FillEllipse(Brushes.White, 9, 13, 6, 7);
            gg.FillEllipse(Brushes.White, 17, 13, 6, 7);
            gg.FillEllipse(Brushes.Black, 10, 15, 4, 5);
            gg.FillEllipse(Brushes.Black, 18, 15, 4, 5);
        }
        tray = new NotifyIcon
        {
            Text             = "Pucca Bot",
            Icon             = Icon.FromHandle(bmp.GetHicon()),
            Visible          = true,
            ContextMenuStrip = menu
        };
        tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (Visible) Hide(); else { Show(); Activate(); }
        };
    }

    void MoveWin() => Location = new Point((int)(bx - W / 2f), (int)(by - H / 2f));
    void Say(string txt, int dur = 95) { bubble = txt; bubbleT = dur; }

    // ── Mouse ─────────────────────────────────────────────
    void OnDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var sp = PointToScreen(new Point(e.X, e.Y));
        dox = sp.X - bx; doy = sp.Y - by;
        dragging = true; dragHist.Clear();
        state = "drag"; Cursor = Cursors.SizeAll;
    }

    void OnMove(object? s, MouseEventArgs e)
    {
        if (!dragging) return;
        var sp = PointToScreen(new Point(e.X, e.Y));
        dragHist.Add(new PointF(sp.X, sp.Y));
        if (dragHist.Count > 5) dragHist.RemoveAt(0);
        bx = sp.X - dox;
        by = sp.Y - doy;
        var vb = SystemInformation.VirtualScreen;
        bx = Math.Clamp(bx, vb.Left + 50, vb.Right  - 50);
        by = Math.Clamp(by, vb.Top  + 70, vb.Bottom - 70);
        MoveWin();
    }

    void OnUp(object? s, MouseEventArgs e)
    {
        if (!dragging || e.Button != MouseButtons.Left) return;
        dragging = false; Cursor = Cursors.Default;
        state = "idle";
    }

    void OnClick(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (!clickPending) { clickPending = true; clickTimer.Start(); }
    }

    void OnDbl(object? s, MouseEventArgs e)
    {
        clickTimer.Stop(); clickPending = false;
        // Double-click: play/pause nhạc (nếu đang có nhạc), còn lại thì hát/nghỉ
        if (waveOut != null || audioPaused)
        {
            TogglePlayPause();
            return;
        }
        if (state is "sing" or "dance") { state = "idle"; Say("Nghỉ rồi ~_~"); }
        else                            { state = "sing";  Say("🎵 La la laaa~ 🎵"); }
    }

    void DoAttack()
    {
        clickPending = false;
        // Single click: luôn đánh (không dính nhạc)
        attackT = 32;
        if (state == "idle" || state == "dance") state = "attack";
        Say(rng.Next(4) switch {
            0 => "Hiyaaa! ✊",
            1 => "Pucca punch! 👊",
            2 => "HUP! 🦵",
            _ => "Gotcha! (ﾒ▼▼ﾒ)"
        });
        Task.Run(() => { Beep(440, 55); Beep(220, 90); });
    }

    // ── YouTube ───────────────────────────────────────────
    void OnYouTube(object? s, EventArgs e)
    {
        string clip = "";
        try { clip = Clipboard.GetText().Trim(); } catch { }
        if (IsYT(clip)) OpenYT(clip); else ShowYTDialog();
    }

    static bool IsSupported(string u) =>
        u.StartsWith("http") && (
            u.Contains("youtube.com") || u.Contains("youtu.be") ||
            u.Contains("soundcloud.com"));

    // Giữ lại alias cho các chỗ gọi cũ
    static bool IsYT(string u) => IsSupported(u);

    void OpenYT(string url) => _ = LoadAndPlayUrl(url, clearPlaylist: true);

    // ── Load URL → thêm vào playlist rồi phát ───────────
    async Task LoadAndPlayUrl(string sourceUrl, bool clearPlaylist)
    {
        Say("⏳ Đang tải danh sách...");
        int mySession = ++audioSession;

        string? ytdlp = await FindYtDlpAsync();
        if (ytdlp == null)
        {
            if (mySession != audioSession) return;
            var r = MessageBox.Show(
                "Chưa có yt-dlp để phát nhạc trực tiếp.\n\n" +
                "Cài nhanh:\n  winget install yt-dlp.yt-dlp\n\n" +
                "Bấm OK để mở link trên browser.",
                "Cần yt-dlp", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (r == DialogResult.OK)
                Process.Start(new ProcessStartInfo(sourceUrl) { UseShellExecute = true });
            state = "idle"; Say("Mở browser rồi!"); return;
        }

        // Lấy title + audio URL cho tất cả track (playlist hoặc đơn bài)
        var tracks = await FetchTracksAsync(ytdlp, sourceUrl);
        if (mySession != audioSession) return;

        if (tracks.Count == 0) { Say("❌ Không tìm được bài nào"); return; }

        if (clearPlaylist) { playlist.Clear(); playlistIdx = 0; }
        int startIdx = playlist.Count;
        playlist.AddRange(tracks);
        playlistIdx = startIdx;

        Say($"✓ Thêm {tracks.Count} bài vào playlist");
        await StartTrackAsync(playlistIdx);
    }

    // ── Phát track theo index ─────────────────────────────
    async Task StartTrackAsync(int idx)
    {
        if (idx < 0 || idx >= playlist.Count) return;
        int mySession = ++audioSession;
        playlistIdx   = idx;

        // Dừng bài cũ
        var oldWo = waveOut;  var oldAr = audioReader;
        waveOut = null;       audioReader = null;
        audioPaused = false;
        oldWo?.Stop(); oldWo?.Dispose(); oldAr?.Dispose();

        var track = playlist[idx];
        // Hiện tên bài ~10 giây (625 frame × 16ms)
        Say($"🎵 {track.Title}", 625);

        string? localFile = null;
        try
        {
            WaveStream? reader = null;

            // Local file → phát thẳng, không cần yt-dlp
            if (File.Exists(track.SourceUrl))
            {
                reader = new AudioFileReader(track.SourceUrl);
            }
            else
            {
            // Tìm yt-dlp
            string? ytdlp = await FindYtDlpAsync();
            if (ytdlp == null) { Say("❌ Cần cài yt-dlp"); return; }
            if (mySession != audioSession) return;

            // Bước 1: extract audio URL từ webpage URL
            Say("⏳ Đang lấy link nhạc...");
            string? audioUrl = await ExtractAudioUrlAsync(ytdlp, track.SourceUrl);
            if (mySession != audioSession) return;
            if (audioUrl != null && !audioUrl.Contains(".m3u8"))
            {
                try
                {
                    // Bước 2a: thử stream trực tiếp (YouTube mp4/webm, SoundCloud mp3)
                    reader = new MediaFoundationReader(audioUrl);
                }
                catch
                {
                    // Bước 2b: stream thất bại → download temp
                    if (mySession != audioSession) return;
                    Say("⏳ Đang tải bài...");
                    localFile = await DownloadTempAsync(ytdlp, track);
                    if (localFile == null || mySession != audioSession)
                    {
                        if (mySession == audioSession) Say("❌ Tải thất bại");
                        return;
                    }
                    reader = new AudioFileReader(localFile);
                }
            }
            else
            {
                // Bước 2c: HLS hoặc không lấy được URL → download temp
                if (mySession != audioSession) return;
                Say("⏳ Đang tải bài (HLS)...");
                localFile = await DownloadTempAsync(ytdlp, track);
                if (localFile == null || mySession != audioSession)
                {
                    if (mySession == audioSession) Say("❌ Tải thất bại");
                    return;
                }
                reader = new AudioFileReader(localFile);
            }
            } // end else (online track)

            if (reader == null) { if (mySession == audioSession) Say("❌ Không tải được"); return; }
            var wo = new WaveOutEvent { DesiredLatency = 200 };
            wo.Init(reader);
            if (mySession != audioSession) { wo.Dispose(); reader.Dispose(); return; }

            audioReader = reader;
            waveOut     = wo;
            wo.Play();
            Say($"🎵 {track.Title}", 625);
            miniPlayer?.UpdateTrack(track, true);

            wo.PlaybackStopped += (_, _) =>
            {
                BeginInvoke(() =>
                {
                    // Xóa temp file sau khi phát xong
                    if (localFile != null) try { File.Delete(localFile); } catch { }
                    if (mySession != audioSession || audioPaused || state != "dance") return;
                    int next = (playlistIdx + 1) % playlist.Count;
                    _ = StartTrackAsync(next);
                });
            };

            state = "dance"; frameT = 0;
        }
        catch (Exception ex)
        {
            if (mySession == audioSession)
            {
                string msg = ex.Message.Length > 40 ? ex.Message[..40] : ex.Message;
                Say($"❌ {msg}");
            }
        }
    }

    // ── Lấy danh sách track từ URL (YouTube/SoundCloud/playlist) ──
    /// Tạo ProcessStartInfo cho yt-dlp với UTF-8 encoding đúng trên Windows
    static ProcessStartInfo MakeYtPsi(string exe, string args)
    {
        var p = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        p.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        p.EnvironmentVariables["PYTHONUTF8"]       = "1";
        return p;
    }

    // Dùng --flat-playlist để liệt kê nhanh từng track (chỉ lấy title + webpage_url)
    // AudioUrl để trống — sẽ extract lúc play
    static async Task<List<TrackInfo>> FetchTracksAsync(string ytdlp, string url)
    {
        // Không dùng --flat-playlist vì nó không fetch đủ metadata (title = NA)
        // --print %(webpage_url)s không cần --format nên không download audio, vẫn nhanh
        var psi = MakeYtPsi(ytdlp, $"--ignore-errors --print \"%(title)s|||%(webpage_url)s\" \"{url}\"");
        using var proc = Process.Start(psi)!;
        string raw = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();

        var tracks = new List<TrackInfo>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int sep = line.IndexOf("|||");
            if (sep < 1) continue;
            string title      = line[..sep].Trim();
            string webpageUrl = line[(sep + 3)..].Trim();
            if (webpageUrl.StartsWith("http"))
                // AudioUrl = "" → StartTrackAsync sẽ extract lúc cần
                tracks.Add(new TrackInfo(title, "", webpageUrl));
        }
        return tracks;
    }

    // ── Extract audio URL trực tiếp từ webpage URL ──
    static async Task<string?> ExtractAudioUrlAsync(string ytdlp, string webpageUrl)
    {
        const string fmt = "bestaudio[protocol=https]/bestaudio[protocol=http]"
                         + "/bestaudio[ext=mp3]/bestaudio[ext=m4a]/bestaudio";
        var psi = MakeYtPsi(ytdlp, $"--print \"%(url)s\" --format \"{fmt}\" \"{webpageUrl}\"");
        using var proc = Process.Start(psi)!;
        string result = (await proc.StandardOutput.ReadToEndAsync()).Trim();
        await proc.WaitForExitAsync();
        // Lấy dòng đầu tiên là http
        foreach (var line in result.Split('\n'))
        {
            string u = line.Trim();
            if (u.StartsWith("http")) return u;
        }
        return null;
    }

    // ── Tìm yt-dlp (tự download nếu chưa có) ─────────────
    static async Task<string?> FindYtDlpAsync()
    {
        // Thư mục cạnh exe
        string exeDir   = AppContext.BaseDirectory;
        string localExe = Path.Combine(exeDir, "yt-dlp.exe");

        string[] candidates = {
            localExe, "yt-dlp", "yt-dlp.exe",
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                "Programs", "yt-dlp", "yt-dlp.exe")
        };
        foreach (var c in candidates)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(c, "--version")
                    { UseShellExecute = false, CreateNoWindow = true,
                      RedirectStandardOutput = true });
                if (p == null) continue;
                await p.WaitForExitAsync();
                return c;
            }
            catch { }
        }

        // Chưa có → tự download từ GitHub releases
        try
        {
            const string url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            var bytes = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(localExe, bytes);
            return localExe;
        }
        catch { return null; }
    }

    // ── Download về temp file khi HLS stream không stream được ──
    static async Task<string?> DownloadTempAsync(string ytdlp, TrackInfo track)
    {
        string tmpFile = Path.Combine(Path.GetTempPath(), $"vosi_{Guid.NewGuid():N}.mp3");
        try
        {
            // yt-dlp -x --audio-format mp3 -o <tmpFile> <sourceUrl>
            var psi = MakeYtPsi(ytdlp, $"-x --audio-format mp3 --no-playlist -o \"{tmpFile}\" \"{track.SourceUrl}\"");
            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync();
            return File.Exists(tmpFile) ? tmpFile : null;
        }
        catch
        {
            try { File.Delete(tmpFile); } catch { }
            return null;
        }
    }

    void TogglePlayPause()
    {
        if (waveOut == null) return;
        if (audioPaused)
        {
            waveOut.Play();
            audioPaused = false;
            state = "dance";
            Say("▶ Phát tiếp! 🕺");
            miniPlayer?.UpdatePlayState(true);
        }
        else
        {
            // Set audioPaused TRƯỚC khi Pause() để PlaybackStopped handler bỏ qua
            audioPaused = true;
            state = "idle";
            waveOut.Pause();
            Say("⏸ Tạm dừng");
            miniPlayer?.UpdatePlayState(false);
        }
    }

    void StopAudio()
    {
        // Tăng session để vô hiệu tất cả callback cũ
        audioSession++;
        audioPaused = false;
        var wo = waveOut;
        var ar = audioReader;
        waveOut     = null;
        audioReader = null;
        wo?.Stop();
        wo?.Dispose();
        ar?.Dispose();
    }

    void ShowYTDialog() => ShowPlaylistManager();
    void OnAddToPlaylist(object? s, EventArgs e) => ShowPlaylistManager();

    void ShowPlaylistManager()
    {
        var dlg = new Form
        {
            Text            = "🎵 Playlist",
            Size            = new Size(500, 420),
            StartPosition   = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimumSize     = new Size(400, 340),
            TopMost         = true,
            Font            = new Font("Segoe UI", 9f)
        };

        // ── Danh sách bài ──
        var listBox = new ListBox
        {
            Left = 10, Top = 10, Width = 470, Height = 220,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            IntegralHeight = false,
            Font = new Font("Segoe UI", 9f)
        };
        RefreshList(listBox);

        // ── Input ──
        var lblInput = new Label { Text = "Link YouTube / SoundCloud / Playlist:", AutoSize = true, Left = 10, Top = 243, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        var txtInput = new TextBox
        {
            Left = 10, Top = 260, Width = 390, Height = 26,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font("Segoe UI", 10f)
        };
        try { txtInput.Text = Clipboard.GetText().Trim(); } catch { }
        if (!IsSupported(txtInput.Text)) txtInput.Clear();

        var btnBrowse = new Button
        {
            Text = "🔍 Tìm nhạc",
            Left = 408, Top = 259, Width = 74, Height = 28,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Font = new Font("Segoe UI", 8.5f)
        };

        // ── Buttons ──
        var btnAdd = new Button
        {
            Text = "➕ Thêm link",
            Left = 10, Top = 296, Width = 100, Height = 32,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        var btnPlay = new Button
        {
            Text = "▶ Phát ngay",
            Left = 118, Top = 296, Width = 100, Height = 32,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        var btnDel = new Button
        {
            Text = "🗑 Xóa bài",
            Left = 226, Top = 296, Width = 90, Height = 32,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        var btnUp = new Button
        {
            Text = "▲",
            Left = 324, Top = 296, Width = 38, Height = 32,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        var btnDown = new Button
        {
            Text = "▼",
            Left = 366, Top = 296, Width = 38, Height = 32,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        var btnClose = new Button
        {
            Text = "Đóng",
            Left = 414, Top = 296, Width = 68, Height = 32,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };

        // Tìm nhạc YouTube
        btnBrowse.Click += (_, _) => OpenYouTubeSearch(dlg, listBox);

        void OpenYouTubeSearch(Form parent, ListBox lb)
        {
            var searchDlg = new Form
            {
                Text = "🔍 Tìm nhạc",
                Width = 660, Height = 510,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false
            };
            var cmbSource = new ComboBox
            {
                Left = 10, Top = 10, Width = 120, Height = 28,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 10f)
            };
            cmbSource.Items.AddRange(new object[] { "▶ YouTube", "☁ SoundCloud" });
            cmbSource.SelectedIndex = 0;
            var txtSearch = new TextBox
            {
                Left = 138, Top = 10, Width = 376, Height = 28,
                Font = new Font("Segoe UI", 11f),
                PlaceholderText = "Tên bài hát, nghệ sĩ..."
            };
            var btnSearch = new Button { Text = "🔍 Tìm", Left = 522, Top = 9, Width = 110, Height = 30 };

            // ── Thumbnail panel bên phải ──────────────────────────
            var thumbBox = new PictureBox
            {
                Left = 358, Top = 48, Width = 274, Height = 154,
                SizeMode   = PictureBoxSizeMode.Zoom,
                BackColor  = Color.FromArgb(30, 30, 30),
                BorderStyle= BorderStyle.FixedSingle
            };
            var lblThumbTitle = new Label
            {
                Left = 358, Top = 206, Width = 274, Height = 56,
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.DimGray,
                AutoEllipsis = true,
                Text = ""
            };

            var resultBox = new ListBox
            {
                Left = 10, Top = 48, Width = 340, Height = 310,
                Font = new Font("Segoe UI", 10f)
            };
            var lblStatus = new Label { Left = 10, Top = 365, Width = 620, Height = 22, Text = "", ForeColor = Color.Gray };
            var btnAdd2  = new Button { Text = "➕ Thêm vào playlist", Left = 10,  Top = 390, Width = 160, Height = 32 };
            var btnPlay2 = new Button { Text = "▶ Phát ngay",         Left = 178, Top = 390, Width = 120, Height = 32 };
            var btnClose2= new Button { Text = "Đóng",                Left = 556, Top = 390, Width = 80,  Height = 32 };

            var searchResults = new List<TrackInfo>();
            var thumbHttp = new System.Net.Http.HttpClient();
            int thumbSession = 0;
            string? cachedYtdlp = null;

            // ── Load thumbnail khi chọn bài ──────────────────────
            resultBox.SelectedIndexChanged += (_, _) =>
            {
                int idx = resultBox.SelectedIndex;
                if (idx < 0 || idx >= searchResults.Count) return;
                var track = searchResults[idx];
                lblThumbTitle.Text = track.Title;
                thumbBox.Image = null;
                thumbBox.BackColor = Color.FromArgb(30, 30, 30);

                int session = ++thumbSession;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        string thumbUrl = track.ThumbnailUrl;

                        // SoundCloud: thumbnail chưa có → lazy-fetch bằng yt-dlp
                        if (!thumbUrl.StartsWith("http") && track.SourceUrl.Contains("soundcloud"))
                        {
                            cachedYtdlp ??= await FindYtDlpAsync();
                            if (cachedYtdlp != null)
                            {
                                var psi2 = MakeYtPsi(cachedYtdlp,
                                    $"--no-playlist --print \"%(thumbnail)s\" \"{track.SourceUrl}\"");
                                using var p2 = Process.Start(psi2)!;
                                string t = (await p2.StandardOutput.ReadToEndAsync()).Trim();
                                await p2.WaitForExitAsync();
                                if (t.StartsWith("http"))
                                {
                                    thumbUrl = t;
                                    // Cache lại cho lần sau
                                    if (session == thumbSession && idx < searchResults.Count)
                                        searchResults[idx] = searchResults[idx] with { ThumbnailUrl = thumbUrl };
                                }
                            }
                        }

                        if (!thumbUrl.StartsWith("http")) return;
                        if (session != thumbSession || searchDlg.IsDisposed) return;

                        var bytes = await thumbHttp.GetByteArrayAsync(thumbUrl);
                        if (session != thumbSession || searchDlg.IsDisposed) return;
                        using var ms = new MemoryStream(bytes);
                        var bmp = new Bitmap(ms);
                        searchDlg.BeginInvoke(() =>
                        {
                            if (session != thumbSession) { bmp.Dispose(); return; }
                            thumbBox.Image?.Dispose();
                            thumbBox.Image     = bmp;
                            thumbBox.BackColor = Color.Black;
                        });
                    }
                    catch { }
                });
            };

            async Task DoSearch()
            {
                string q = txtSearch.Text.Trim();
                if (string.IsNullOrEmpty(q)) return;
                lblStatus.Text = "⏳ Đang tìm...";
                resultBox.Items.Clear();
                searchResults.Clear();
                thumbBox.Image = null;
                lblThumbTitle.Text = "";
                btnSearch.Enabled = false;

                string? ytdlp = await FindYtDlpAsync();
                if (ytdlp == null) { lblStatus.Text = "❌ Cần yt-dlp"; btnSearch.Enabled = true; return; }

                string prefix = cmbSource.SelectedIndex == 1 ? "scsearch15" : "ytsearch15";
                var psi = MakeYtPsi(ytdlp,
                    $"--flat-playlist --ignore-errors --print \"%(title)s|||%(webpage_url)s|||%(thumbnail)s\" \"{prefix}:{q}\"");
                using var proc = Process.Start(psi)!;
                string raw = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split("|||");
                    if (parts.Length < 2) continue;
                    string title = parts[0].Trim();
                    string url   = parts[1].Trim();
                    string thumb = parts.Length >= 3 ? parts[2].Trim() : "";
                    if (!url.StartsWith("http")) continue;
                    // Thumbnail chỉ lưu nếu là URL hợp lệ
                    if (!thumb.StartsWith("http")) thumb = "";
                    // YouTube: tự dựng thumbnail từ video ID nếu chưa có
                    if (thumb == "" && url.Contains("youtube"))
                    {
                        string? id = null;
                        if (url.Contains("youtu.be/")) id = url.Split("youtu.be/")[1].Split('?')[0];
                        else if (url.Contains("v=")) { int vi = url.IndexOf("v=")+2; int en = url.IndexOf('&',vi); id = en<0?url[vi..]:url[vi..en]; }
                        if (id != null) thumb = $"https://img.youtube.com/vi/{id}/mqdefault.jpg";
                    }
                    // SoundCloud: thumbnail để trống → lazy-load khi user chọn bài
                    searchResults.Add(new TrackInfo(title, "", url, thumb));
                    resultBox.Items.Add(title);
                }
                lblStatus.Text = searchResults.Count > 0 ? $"Tìm thấy {searchResults.Count} bài" : "Không tìm thấy bài nào";
                btnSearch.Enabled = true;
            }

            btnSearch.Click   += (_, _) => _ = DoSearch();
            txtSearch.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = DoSearch(); } };

            void AddSelected(bool playNow)
            {
                int i = resultBox.SelectedIndex;
                if (i < 0 || i >= searchResults.Count) return;
                var track = searchResults[i];
                playlist.Add(track);
                RefreshList(lb);
                Say($"✓ Thêm: {track.Title}");
                if (playNow) { searchDlg.Close(); parent.Close(); _ = StartTrackAsync(playlist.Count - 1); }
            }

            btnAdd2.Click  += (_, _) => AddSelected(false);
            btnPlay2.Click += (_, _) => AddSelected(true);
            resultBox.DoubleClick += (_, _) => AddSelected(true);
            btnClose2.Click += (_, _) => searchDlg.Close();
            searchDlg.FormClosed += (_, _) => thumbHttp.Dispose();

            searchDlg.Controls.AddRange(new Control[] {
                cmbSource, txtSearch, btnSearch,
                resultBox, thumbBox, lblThumbTitle,
                lblStatus, btnAdd2, btnPlay2, btnClose2
            });
            searchDlg.AcceptButton = btnSearch;
            searchDlg.ShowDialog(parent);
        }

        // Chọn file nhạc từ máy
        var btnFileLocal = new Button
        {
            Text = "📂 File",
            Left = 408, Top = 230, Width = 74, Height = 26,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Font = new Font("Segoe UI", 9f)
        };
        btnFileLocal.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog
            {
                Title     = "Chọn file nhạc",
                Filter    = "Audio|*.mp3;*.m4a;*.wav;*.flac;*.ogg;*.aac;*.wma;*.opus|Tất cả|*.*",
                Multiselect = true
            };
            if (ofd.ShowDialog() != DialogResult.OK) return;
            int added = 0;
            foreach (var f in ofd.FileNames)
            {
                string title = Path.GetFileNameWithoutExtension(f);
                playlist.Add(new TrackInfo(title, f, f));
                added++;
            }
            RefreshList(listBox);
            Say($"✓ Thêm {added} file nhạc");
            if (waveOut == null && playlist.Count > 0)
            {
                dlg.Close();
                _ = StartTrackAsync(playlist.Count - added);
            }
        };

        // Thêm link (load tracks từ URL)
        btnAdd.Click += (_, _) =>
        {
            string u = txtInput.Text.Trim();
            if (!IsSupported(u)) { MessageBox.Show("Link không hợp lệ!\nHỗ trợ: YouTube, SoundCloud", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            txtInput.Clear();
            dlg.Close();
            _ = LoadAndPlayUrl(u, clearPlaylist: false);
        };

        // Phát ngay bài đang chọn trong list
        btnPlay.Click += (_, _) =>
        {
            if (listBox.SelectedIndex >= 0 && listBox.SelectedIndex < playlist.Count)
            {
                _ = StartTrackAsync(listBox.SelectedIndex);
                dlg.Close();
            }
            else
            {
                string u = txtInput.Text.Trim();
                if (!IsSupported(u)) { MessageBox.Show("Chọn bài trong danh sách hoặc nhập link!", "", MessageBoxButtons.OK); return; }
                dlg.Close();
                _ = LoadAndPlayUrl(u, clearPlaylist: true);
            }
        };

        // Xóa bài đang chọn
        btnDel.Click += (_, _) =>
        {
            int i = listBox.SelectedIndex;
            if (i < 0 || i >= playlist.Count) return;
            bool playingThis = (i == playlistIdx && waveOut != null);
            playlist.RemoveAt(i);
            if (playlistIdx >= playlist.Count) playlistIdx = Math.Max(0, playlist.Count - 1);
            RefreshList(listBox);
            if (listBox.Items.Count > 0) listBox.SelectedIndex = Math.Min(i, listBox.Items.Count - 1);
            if (playingThis && playlist.Count > 0) _ = StartTrackAsync(playlistIdx);
            else if (playingThis) StopAudio();
            Say($"Còn {playlist.Count} bài");
        };

        // Di chuyển lên
        btnUp.Click += (_, _) =>
        {
            int i = listBox.SelectedIndex;
            if (i <= 0) return;
            (playlist[i], playlist[i-1]) = (playlist[i-1], playlist[i]);
            if (playlistIdx == i) playlistIdx = i - 1;
            else if (playlistIdx == i - 1) playlistIdx = i;
            RefreshList(listBox);
            listBox.SelectedIndex = i - 1;
        };

        // Di chuyển xuống
        btnDown.Click += (_, _) =>
        {
            int i = listBox.SelectedIndex;
            if (i < 0 || i >= playlist.Count - 1) return;
            (playlist[i], playlist[i+1]) = (playlist[i+1], playlist[i]);
            if (playlistIdx == i) playlistIdx = i + 1;
            else if (playlistIdx == i + 1) playlistIdx = i;
            RefreshList(listBox);
            listBox.SelectedIndex = i + 1;
        };

        // Double-click phát luôn
        listBox.DoubleClick += (_, _) => btnPlay.PerformClick();

        btnClose.Click += (_, _) => dlg.Close();

        dlg.Controls.AddRange(new Control[]
            { listBox, lblInput, txtInput, btnBrowse, btnFileLocal, btnAdd, btnPlay, btnDel, btnUp, btnDown, btnClose });

        // Highlight bài đang phát
        if (playlist.Count > 0 && playlistIdx < playlist.Count)
            listBox.SelectedIndex = playlistIdx;

        dlg.ShowDialog();
    }

    void RefreshList(ListBox lb)
    {
        lb.Items.Clear();
        for (int i = 0; i < playlist.Count; i++)
        {
            string icon  = (i == playlistIdx && waveOut != null) ? "▶ " : "   ";
            string title = playlist[i].Title;
            if (title.Length > 55) title = title[..55] + "…";
            lb.Items.Add($"{i + 1}. {icon}{title}");
        }
        if (lb.Items.Count == 0) lb.Items.Add("(Chưa có bài nào — thêm link YouTube/SoundCloud)");
    }

    void NextTrack()
    {
        if (playlist.Count == 0) { Say("Playlist trống!"); return; }
        _ = StartTrackAsync((playlistIdx + 1) % playlist.Count);
    }

    void PrevTrack()
    {
        if (playlist.Count == 0) return;
        int prev = (playlistIdx - 1 + playlist.Count) % playlist.Count;
        _ = StartTrackAsync(prev);
    }

    void ToggleMiniPlayer()
    {
        if (miniPlayer == null || miniPlayer.IsDisposed)
        {
            miniPlayer = new MiniPlayer();
            miniPlayer.OnPrev      = PrevTrack;
            miniPlayer.OnPlayPause = TogglePlayPause;
            miniPlayer.OnNext      = NextTrack;
            // Cập nhật state hiện tại
            if (playlist.Count > 0 && playlistIdx < playlist.Count)
                miniPlayer.UpdateTrack(playlist[playlistIdx], waveOut != null && !audioPaused);
        }
        if (miniPlayer.Visible) miniPlayer.Hide();
        else                    miniPlayer.Show(this);
    }

    // ── Game loop ─────────────────────────────────────────
    void GameTick()
    {
        frameT++;
        spriteMs += 16; // timer interval ~16ms
        if (attackT > 0) { attackT--; if (attackT == 0 && state == "attack") state = (waveOut != null && !audioPaused) ? "dance" : "idle"; }

        if (state == "sing"  && frameT % 34 == 0)
        {
            Task.Run(() => Beep(SongFreqs[songIdx++ % SongFreqs.Length], 105));
            SpawnF("♪♫♬"[rng.Next(3)].ToString());
        }
        if (state == "dance" && frameT % 22 == 0)
            SpawnF(new[] { "♪","♫","🌸","✿","★","❤" }[rng.Next(6)]);

        if (bubbleT > 0) { bubbleT--; if (bubbleT == 0) bubble = null; }

        for (int i = floaties.Count - 1; i >= 0; i--)
        {
            floaties[i].Y    += floaties[i].Vy;
            floaties[i].Life -= 0.011f;
            if (floaties[i].Life <= 0) floaties.RemoveAt(i);
        }
    }

    void SpawnF(string sym) => floaties.Add(new Note {
        X = bx + (float)(rng.NextDouble() - 0.5) * 55,
        Y = by - 88, Vy = -0.85f - (float)rng.NextDouble() * 0.85f,
        Life = 1f, Symbol = sym
    });

    // ── Per-pixel alpha render ────────────────────────────
    void PushFrame()
    {
        if (!IsHandleCreated) return;

        using var bmp = new Bitmap(W, H, PixelFormat.Format32bppPArgb); // premultiplied!
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            DrawBot(g);
        }

        var hdcScr = GetDC(IntPtr.Zero);
        var hdcMem = CreateCompatibleDC(hdcScr);
        var hbmp   = bmp.GetHbitmap(Color.FromArgb(0));
        var hOld   = SelectObject(hdcMem, hbmp);

        var dst   = new WPOINT { X = Left, Y = Top };
        var sz    = new WSIZE  { CX = W,   CY = H  };
        var src   = new WPOINT { X = 0,    Y = 0   };
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };

        UpdateLayeredWindow(Handle, hdcScr, ref dst, ref sz, hdcMem, ref src, 0, ref blend, 2);

        SelectObject(hdcMem, hOld);
        DeleteObject(hbmp);
        DeleteDC(hdcMem);
        ReleaseDC(IntPtr.Zero, hdcScr);
    }

    // OnPaint không làm gì (PushFrame lo hết)
    protected override void OnPaint(PaintEventArgs e) { }

    // ── Draw ──────────────────────────────────────────────
    void DrawBot(Graphics g)
    {
        float cx  = W / 2f;
        float cy  = H / 2f + 8f;
        float bob = MathF.Sin(frameT * 0.055f) * 2.8f;
        float dB  = state == "dance" ? MathF.Sin(frameT * 0.19f) * 9f : 0f;
        float yo  = bob + dB;

        // ── Sprite sheet mode (Aseprite) ─────────────────
        if (spriteSheet != null)
        {
            DrawSpriteSheet(g, cx, cy, yo);
            DrawOverlays(g, cx, cy, yo);
            return;
        }

        // ── Sprite PNG đơn ────────────────────────────────
        if (sprite != null)
        {
            DrawSprite(g, cx, cy, yo);
            DrawOverlays(g, cx, cy, yo);
            return;
        }

        // ── GDI+ Pucca mode ──────────────────────────────
        // Colors
        var cHair = Color.FromArgb(15,  12,  18 );   // near-black hair
        var cRed  = Color.FromArgb(196, 30,  50 );   // Pucca red
        var cRDk  = Color.FromArgb(148, 18,  36 );   // red shadow
        var cRLt  = Color.FromArgb(230, 80,  100);   // red highlight
        var cSkin = Color.FromArgb(255, 212, 176);   // warm skin
        var cSknD = Color.FromArgb(230, 180, 140);   // skin shadow
        var cBlsh = Color.FromArgb(130, 255, 160, 170); // cheek blush
        var cGlss = Color.FromArgb(60,  255, 255, 255); // gloss highlight
        var cShad = Color.FromArgb(50,  0,   0,   0 );  // drop shadow
        var cTeeth= Color.FromArgb(245, 245, 240);   // off-white teeth
        var cWhite= Color.White;

        using var bHair = new SolidBrush(cHair);
        using var bRed  = new SolidBrush(cRed);
        using var bRDk  = new SolidBrush(cRDk);
        using var bRLt  = new SolidBrush(cRLt);
        using var bSkin = new SolidBrush(cSkin);
        using var bSknD = new SolidBrush(cSknD);
        using var bBlsh = new SolidBrush(cBlsh);
        using var bGlss = new SolidBrush(cGlss);
        using var bShad = new SolidBrush(cShad);
        using var bW    = new SolidBrush(cWhite);
        using var bTeeth= new SolidBrush(cTeeth);

        // Helper: fill circle
        void FC(float x, float y, float r, Brush b)
            => g.FillEllipse(b, x - r, y - r, r * 2, r * 2);

        // Helper: fill ellipse with separate x/y radii
        void FE(float x, float y, float rx, float ry, Brush b)
            => g.FillEllipse(b, x - rx, y - ry, rx * 2, ry * 2);

        // Helper: filled rounded rectangle (center x,y)
        void RR(float x, float y, float w, float h, float arc, Brush b)
        {
            arc = Math.Min(arc, Math.Min(w, h));
            using var p = new GraphicsPath();
            p.AddArc(x - w/2,           y - h/2,           arc, arc, 180, 90);
            p.AddArc(x + w/2 - arc,     y - h/2,           arc, arc, 270, 90);
            p.AddArc(x + w/2 - arc,     y + h/2 - arc,     arc, arc,   0, 90);
            p.AddArc(x - w/2,           y + h/2 - arc,     arc, arc,  90, 90);
            p.CloseFigure();
            g.FillPath(b, p);
        }

        // ── Animation values ─────────────────────────────────
        float legSw = state == "dance" ? MathF.Sin(frameT * 0.22f) * 22f : 0f;
        float dArm  = state == "dance" ? MathF.Sin(frameT * 0.20f) * 32f : 0f;
        float lA    = attackT > 0 && state == "attack" ? -72f : (-15f + dArm);
        float rA    = attackT > 0 && state == "attack" ?  28f : ( 15f - dArm);
        float eDB   = state == "dance" ? MathF.Sin(frameT * 0.22f) * 1.5f : 0f;
        float sc    = 1f + (state == "dance" ? MathF.Sin(frameT * 0.19f) * 0.04f : 0f);
        GraphicsState gs;

        // ── 1. Shadow ─────────────────────────────────────────
        FE(cx, cy + 82f + yo, 30f, 4.5f, bShad);

        // ── 2. Legs ───────────────────────────────────────────
        // Left leg — pivot at (cx-15, cy+54+yo), swings backward
        gs = g.Save();
        g.TranslateTransform(cx - 15f, cy + 54f + yo);
        g.RotateTransform(-legSw);
        RR(0f, 14f, 16f, 28f, 8f, bHair);      // leg column
        // Left shoe
        FE(0f, 28f, 11f, 9f, bRed);             // shoe body
        FE(-4f, 25f, 5f, 3f, bRLt);             // shoe highlight
        g.Restore(gs);

        // Right leg — swings forward opposite
        gs = g.Save();
        g.TranslateTransform(cx + 15f, cy + 54f + yo);
        g.RotateTransform(legSw);
        RR(0f, 14f, 16f, 28f, 8f, bHair);
        FE(0f, 28f, 11f, 9f, bRed);
        FE(-4f, 25f, 5f, 3f, bRLt);
        g.Restore(gs);

        // ── 3. Body ───────────────────────────────────────────
        // Slight squash-stretch scale
        gs = g.Save();
        g.TranslateTransform(cx, cy + 32f + yo);
        g.ScaleTransform(sc, 2f - sc);          // squash on Y, stretch on X (chibi bounce)
        RR(0f, 0f, 58f, 52f, 22f, bRed);        // main body
        // body shading — darker bottom half
        using (var bodyShd = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
            g.FillEllipse(bodyShd, -25f, 8f, 50f, 28f);
        // body highlight — lighter arc at top
        using (var bodyHlt = new SolidBrush(Color.FromArgb(40, 255, 200, 200)))
            g.FillEllipse(bodyHlt, -22f, -22f, 44f, 28f);
        g.Restore(gs);

        // ── 4. Collar ─────────────────────────────────────────
        // White rounded collar visible above body neckline
        RR(cx, cy + 8f + yo, 24f, 14f, 7f, bW);
        // collar inner shadow
        using (var cInner = new SolidBrush(Color.FromArgb(30, 0, 0, 0)))
            RR(cx, cy + 9f + yo, 20f, 10f, 5f, cInner);

        // ── 5. Arms ───────────────────────────────────────────
        // Left arm — pivot at left shoulder
        gs = g.Save();
        g.TranslateTransform(cx - 33f, cy + 12f + yo);
        g.RotateTransform(lA);
        RR(0f, 13f, 14f, 26f, 7f, bSknD);      // arm shadow side
        RR(-1f, 12f, 13f, 24f, 7f, bSkin);     // arm main
        FC(0f, 27f, 8f, bSkin);                 // hand ball
        FC(-3f, 24f, 3f, bGlss);               // hand gloss
        g.Restore(gs);

        // Right arm
        gs = g.Save();
        g.TranslateTransform(cx + 33f, cy + 12f + yo);
        g.RotateTransform(rA);
        RR(0f, 13f, 14f, 26f, 7f, bSknD);
        RR(1f, 12f, 13f, 24f, 7f, bSkin);
        FC(0f, 27f, 8f, bSkin);
        FC(3f, 24f, 3f, bGlss);
        g.Restore(gs);

        // ── 6. Hair base circle ───────────────────────────────
        // Large near-black circle — top & back of head
        FC(cx, cy - 30f + yo, 44f, bHair);

        // ── 7. Face circle ────────────────────────────────────
        // Slightly smaller, offset downward — hair frames the top/sides
        FC(cx, cy - 24f + yo, 38f, bSkin);
        // Subtle face shading — darker at chin and temples
        using (var faceShd = new SolidBrush(Color.FromArgb(18, 120, 60, 0)))
        {
            FE(cx, cy - 14f + yo, 30f, 20f, faceShd);  // chin area
        }

        // ── 8. Eyes ───────────────────────────────────────────
        bool blink = frameT % 130 < 7;
        float eY   = cy - 32f + yo + eDB;

        if (blink)
        {
            // Closed eyes — thin horizontal curved lines
            using var blinkPen = new Pen(cHair, 2.8f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(blinkPen, cx - 22f, eY, cx - 10f, eY + 1f);
            g.DrawLine(blinkPen, cx + 10f, eY, cx + 22f, eY + 1f);
        }
        else
        {
            // Open eyes — almond/oval white shapes
            // Left eye
            using (var eyePath = new GraphicsPath())
            {
                eyePath.AddEllipse(cx - 23f, eY - 6f, 15f, 12f);
                g.FillPath(bW, eyePath);
            }
            // Right eye
            using (var eyePath = new GraphicsPath())
            {
                eyePath.AddEllipse(cx + 8f, eY - 6f, 15f, 12f);
                g.FillPath(bW, eyePath);
            }
            // Left pupil — small dark circle, slightly lower-center
            FC(cx - 15.5f, eY + 1.5f, 5f, bHair);
            // Right pupil
            FC(cx + 15.5f, eY + 1.5f, 5f, bHair);
            // Eye gloss (tiny white dot upper-left of each pupil)
            FC(cx - 17.5f, eY - 0.5f, 1.8f, bW);
            FC(cx + 13.5f, eY - 0.5f, 1.8f, bW);
        }

        // ── 9. Cheek blush ────────────────────────────────────
        FE(cx - 29f, cy - 20f + yo, 10f, 6f, bBlsh);
        FE(cx + 29f, cy - 20f + yo, 10f, 6f, bBlsh);

        // ── 10. Mouth ─────────────────────────────────────────
        float mY = cy - 11f + yo;
        if (state is "sing" or "dance")
        {
            // Open mouth — filled arc with teeth showing
            using var mp = new GraphicsPath();
            mp.AddArc(cx - 11f, mY - 3f, 22f, 16f, 0f, 180f);
            g.FillPath(bHair, mp);   // dark mouth interior
            // Teeth row
            g.FillEllipse(bTeeth, cx - 8f, mY + 1f, 16f, 7f);
        }
        else if (attackT > 0 && state == "attack")
        {
            // Flat determined line
            using var atkPen = new Pen(cHair, 2.5f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(atkPen, cx - 9f, mY + 4f, cx + 9f, mY + 4f);
        }
        else
        {
            // Idle — tiny upward-curving smile arc
            using var smilePen = new Pen(Color.FromArgb(180, 100, 60, 60), 2.2f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(smilePen, cx - 8f, mY - 1f, 16f, 10f, 10f, 160f);
        }

        // ── 11. Hair buns ─────────────────────────────────────
        // Left bun
        FC(cx - 28f, cy - 68f + yo, 20f, bHair);
        // Right bun
        FC(cx + 28f, cy - 68f + yo, 20f, bHair);

        // ── 12. Bun highlights ────────────────────────────────
        // Small semi-transparent white ellipse on upper-left of each bun
        FE(cx - 34f, cy - 76f + yo, 6f, 4.5f, bGlss);
        FE(cx + 22f, cy - 76f + yo, 6f, 4.5f, bGlss);

        DrawOverlays(g, cx, cy, yo);
    }

    // ── Sprite sheet render (Aseprite) ────────────────────
    void DrawSpriteSheet(Graphics g, float cx, float cy, float yo)
    {
        if (spriteSheet == null) return;
        var tag = spriteSheet.GetTag(state) ?? spriteSheet.GetTag("idle");
        if (tag == null) return;

        int frameIdx = spriteSheet.GetFrameIndex(tag, spriteMs);

        float dArm = state == "dance" ? MathF.Sin(frameT * 0.20f) * 0.15f : 0f;
        float rot  = (state == "attack" && attackT > 0) ? -8f : dArm * 12f;
        float sc   = 1f + (state == "dance" ? MathF.Sin(frameT * 0.19f) * 0.04f : 0f);

        spriteSheet.DrawFrame(g, frameIdx, cx, cy + 10, yo, W - 20, H - 55, rot, sc);
    }

    // ── Sprite render ─────────────────────────────────────
    void DrawSprite(Graphics g, float cx, float cy, float yo)
    {
        if (sprite == null) return;

        float dArm = state == "dance" ? MathF.Sin(frameT * 0.20f) * 0.15f : 0f;
        float rot  = (state == "attack" && attackT > 0) ? -12f : dArm * 15f;
        float sc   = 1f + (state == "dance" ? MathF.Sin(frameT * 0.19f) * 0.06f : 0f);

        // Fit sprite to W-20 × H-60 area (để chỗ cho bubble)
        int sw = W - 20, sh = H - 55;
        float aspect = (float)sprite.Width / sprite.Height;
        if (aspect > (float)sw / sh) sh = (int)(sw / aspect);
        else                         sw = (int)(sh * aspect);

        var gs = g.Save();
        g.TranslateTransform(cx, cy + 10 + yo);
        g.RotateTransform(rot);
        g.ScaleTransform(sc, sc);
        g.DrawImage(sprite, -sw / 2, -sh / 2, sw, sh);
        g.Restore(gs);
    }

    // ── Bubble + floaties (dùng chung) ───────────────────
    void DrawOverlays(Graphics g, float cx, float cy, float yo)
    {
        using var fB  = new Font("Segoe UI Emoji", 9f, FontStyle.Bold);
        using var fN  = new Font("Segoe UI Emoji", 13f, FontStyle.Bold);
        using var sf  = new StringFormat
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        // Bubble
        if (bubble != null && bubbleT > 0)
        {
            using var fBub = new Font("Segoe UI", 9f, FontStyle.Bold);
            string bt  = bubble.Length > 28 ? bubble[..28] : bubble;
            var ts     = g.MeasureString(bt, fBub);
            float tw   = Math.Max(72f, Math.Min(160f, ts.Width + 24));
            float th   = 32f;
            // Đảm bảo bubble luôn nằm trong form (min y = th/2 + 6)
            float bpx  = cx;
            float bpy  = Math.Max(th / 2 + 6, cy - 120 + yo);
            var br     = new RectangleF(bpx - tw/2, bpy - th/2, tw, th);
            // Vẽ shadow nhẹ
            g.FillRectangle(new SolidBrush(Color.FromArgb(40, 0, 0, 0)),
                new RectangleF(br.X + 2, br.Y + 2, br.Width, br.Height));
            // Vẽ nền trắng đục hoàn toàn
            g.FillRectangle(Brushes.White, br);
            using var bpen = new Pen(Color.FromArgb(255, 220, 140, 160), 1.8f);
            g.DrawRectangle(bpen, br.X, br.Y, br.Width, br.Height);
            // Đuôi bong bóng (chỉ vẽ nếu còn chỗ)
            if (bpy + th / 2 + 10 < H)
                g.FillPolygon(Brushes.White, new PointF[]
                    { new(bpx-5, bpy+th/2), new(bpx+5, bpy+th/2), new(bpx, bpy+th/2+10) });
            // Text với màu đậm, rõ ràng
            TextRenderer.DrawText(g, bt, fBub,
                new Rectangle((int)br.X, (int)br.Y, (int)br.Width, (int)br.Height),
                Color.FromArgb(160, 0, 30),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        // Floaties
        foreach (var n in floaties)
        {
            if (n.Life <= 0.18f) continue;
            int alpha = (int)(Math.Min(1f, n.Life * 1.5f) * 255);
            using var nb = new SolidBrush(Color.FromArgb(alpha, 255, 160, 190));
            g.DrawString(n.Symbol, fN, nb,
                new PointF(n.X - Location.X, n.Y - Location.Y), sf);
        }
    }
}
