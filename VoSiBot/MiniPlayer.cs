using System.Net.Http;

namespace VoSiBot;

/// <summary>Mini player nổi — thumbnail + tên bài + ⏮⏯⏭</summary>
class MiniPlayer : Form
{
    // ── Controls ──────────────────────────────────────────
    readonly PictureBox thumb  = new() { Size = new Size(64, 64), Left = 6, Top = 6, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(30, 30, 30) };
    readonly Label      lblTitle = new() { Left = 76, Top = 6, Width = 210, Height = 36, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = Color.White, BackColor = Color.Transparent, AutoEllipsis = true };
    readonly Label      lblSub   = new() { Left = 76, Top = 40, Width = 210, Height = 18, Font = new Font("Segoe UI", 8f), ForeColor = Color.Silver, BackColor = Color.Transparent };
    readonly Button     btnPrev  = new() { Text = "⏮", Left = 76,  Top = 42, Width = 40, Height = 24, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.Transparent, Font = new Font("Segoe UI", 11f) };
    readonly Button     btnPlay  = new() { Text = "⏸", Left = 120, Top = 42, Width = 40, Height = 24, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.Transparent, Font = new Font("Segoe UI", 11f) };
    readonly Button     btnNext  = new() { Text = "⏭", Left = 164, Top = 42, Width = 40, Height = 24, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.Transparent, Font = new Font("Segoe UI", 11f) };
    readonly Button     btnClose2 = new() { Text = "✕", Left = 282, Top = 4, Width = 22, Height = 22, FlatStyle = FlatStyle.Flat, ForeColor = Color.Silver, BackColor = Color.Transparent, Font = new Font("Segoe UI", 9f) };

    static readonly HttpClient http = new();
    bool dragging; Point dragStart;

    // Callbacks từ Form1
    public Action? OnPrev, OnPlayPause, OnNext;

    public MiniPlayer()
    {
        FormBorderStyle = FormBorderStyle.None;
        BackColor       = Color.FromArgb(25, 25, 35);
        Size            = new Size(310, 76);
        TopMost         = true;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.Manual;

        // Đặt góc dưới phải màn hình
        var screen = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(screen.Right - Width - 12, screen.Bottom - Height - 12);

        // Bo góc bằng Region
        ApplyRoundRect();

        foreach (var btn in new[] { btnPrev, btnPlay, btnNext, btnClose2 })
            btn.FlatAppearance.BorderSize = 0;

        lblSub.Visible  = false; // ẩn sub, chỉ dùng nếu cần
        btnPrev.Visible = true;

        Controls.AddRange(new Control[] { thumb, lblTitle, btnPrev, btnPlay, btnNext, btnClose2 });

        // Drag cả form khi kéo phần nền
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { dragging = true; dragStart = e.Location; } };
        MouseMove += (_, e) => { if (dragging) Location = new Point(Location.X + e.X - dragStart.X, Location.Y + e.Y - dragStart.Y); };
        MouseUp   += (_, _) => dragging = false;

        // Drag trên thumb và label cũng được
        foreach (Control c in new Control[] { thumb, lblTitle })
        {
            c.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { dragging = true; dragStart = PointToClient(MousePosition); } };
            c.MouseMove += (_, e) => { if (dragging) Location = new Point(Location.X + e.X - dragStart.X, Location.Y + e.Y - dragStart.Y); };
            c.MouseUp   += (_, _) => dragging = false;
        }

        btnPrev.Click   += (_, _) => OnPrev?.Invoke();
        btnPlay.Click   += (_, _) => OnPlayPause?.Invoke();
        btnNext.Click   += (_, _) => OnNext?.Invoke();
        btnClose2.Click += (_, _) => Hide();

        // Paint border
        Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(60, 80, 120), 1.5f);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawRoundedRectangle(pen, 1, 1, Width - 3, Height - 3, 10);
        };
    }

    void ApplyRoundRect()
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int r = 12;
        path.AddArc(0, 0, r, r, 180, 90);
        path.AddArc(Width - r, 0, r, r, 270, 90);
        path.AddArc(Width - r, Height - r, r, r, 0, 90);
        path.AddArc(0, Height - r, r, r, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); ApplyRoundRect(); }

    // ── Public API ────────────────────────────────────────
    public void UpdateTrack(TrackInfo track, bool isPlaying)
    {
        if (InvokeRequired) { BeginInvoke(() => UpdateTrack(track, isPlaying)); return; }
        lblTitle.Text   = track.Title;
        btnPlay.Text    = isPlaying ? "⏸" : "▶";
        thumb.Image     = null;
        thumb.BackColor = Color.FromArgb(30, 30, 40);

        // Load thumbnail async
        string thumbUrl = GetThumbnailUrl(track);
        if (!string.IsNullOrEmpty(thumbUrl))
            _ = LoadThumbAsync(thumbUrl);
    }

    public void UpdatePlayState(bool isPlaying)
    {
        if (InvokeRequired) { BeginInvoke(() => UpdatePlayState(isPlaying)); return; }
        btnPlay.Text = isPlaying ? "⏸" : "▶";
    }

    static string GetThumbnailUrl(TrackInfo track)
    {
        if (!string.IsNullOrEmpty(track.ThumbnailUrl)) return track.ThumbnailUrl;

        // Construct YouTube thumbnail từ URL
        string src = track.SourceUrl;
        string? id = null;
        if (src.Contains("youtu.be/"))
            id = src.Split("youtu.be/")[1].Split('?')[0];
        else if (src.Contains("youtube.com/watch") && src.Contains("v="))
        {
            int vi = src.IndexOf("v=") + 2;
            int end = src.IndexOf('&', vi);
            id = end < 0 ? src[vi..] : src[vi..end];
        }
        if (id != null) return $"https://img.youtube.com/vi/{id}/mqdefault.jpg";
        return "";
    }

    async Task LoadThumbAsync(string url)
    {
        try
        {
            var bytes = await http.GetByteArrayAsync(url);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            if (IsDisposed) { bmp.Dispose(); return; }
            BeginInvoke(() =>
            {
                thumb.Image?.Dispose();
                thumb.Image      = bmp;
                thumb.BackColor  = Color.Transparent;
            });
        }
        catch { }
    }
}

// Extension helper để vẽ rounded rectangle
static class GraphicsEx
{
    public static void DrawRoundedRectangle(this Graphics g, Pen pen, float x, float y, float w, float h, float r)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(x, y, r, r, 180, 90);
        path.AddArc(x + w - r, y, r, r, 270, 90);
        path.AddArc(x + w - r, y + h - r, r, r, 0, 90);
        path.AddArc(x, y + h - r, r, r, 90, 90);
        path.CloseFigure();
        g.DrawPath(pen, path);
    }
}
