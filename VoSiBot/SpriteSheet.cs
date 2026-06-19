using System.Text.Json.Nodes;

namespace VoSiBot;

/// <summary>
/// Đọc sprite sheet từ:
///   1. Aseprite JSON + PNG
///   2. Thư mục PNG sequence (subfolder hoặc flat prefix)
/// </summary>
class SpriteSheet : IDisposable
{
    public record FrameTag(string Name, int From, int To, string Direction);
    public record Frame(Rectangle Rect, int Duration);

    // Sprite sheet mode
    Bitmap? sheet;
    // PNG sequence mode — mỗi frame là 1 Bitmap riêng
    Bitmap[]? seqFrames;

    readonly Frame[] frames;
    readonly Dictionary<string, FrameTag> tags = new(StringComparer.OrdinalIgnoreCase);

    // Map state bot → tên folder/prefix thường gặp
    static readonly Dictionary<string, string[]> StateAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["idle"]   = new[] { "idle", "stand", "normal", "default", "rest" },
        ["dance"]  = new[] { "dance", "dancing", "music", "groove", "happy", "joy" },
        ["attack"] = new[] { "attack", "hit", "punch", "fight", "slash", "hurt" },
        ["sing"]   = new[] { "sing", "singing", "song", "talk", "cheer" },
    };

    // ── Constructor (Aseprite) ─────────────────────────────
    SpriteSheet(Bitmap sheet, Frame[] frames, IEnumerable<FrameTag> tags)
    {
        this.sheet  = sheet;
        this.frames = frames;
        foreach (var t in tags) this.tags[t.Name] = t;
    }

    // ── Constructor (PNG sequence) ─────────────────────────
    SpriteSheet(Bitmap[] seqFrames, Frame[] frames, IEnumerable<FrameTag> tags)
    {
        this.seqFrames = seqFrames;
        this.frames    = frames;
        foreach (var t in tags) this.tags[t.Name] = t;
    }

    // ══════════════════════════════════════════════════════
    // Load từ Aseprite JSON
    // ══════════════════════════════════════════════════════
    public static SpriteSheet? Load(string jsonPath)
    {
        try
        {
            string pngPath = Path.ChangeExtension(jsonPath, ".png");
            if (!File.Exists(pngPath))
            {
                string dir  = Path.GetDirectoryName(jsonPath)!;
                string stem = Path.GetFileNameWithoutExtension(jsonPath);
                pngPath = Directory.GetFiles(dir, "*.png")
                              .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                                                       .Equals(stem, StringComparison.OrdinalIgnoreCase))
                          ?? Directory.GetFiles(dir, "*.png").FirstOrDefault() ?? "";
                if (!File.Exists(pngPath)) return null;
            }

            var bmp  = new Bitmap(pngPath);
            var json = JsonNode.Parse(File.ReadAllText(jsonPath))!;
            var framesNode = json["frames"]!;
            var metaNode   = json["meta"];

            var frameList = new List<Frame>();
            if (framesNode is JsonArray arr)
                foreach (var f in arr) frameList.Add(ParseAsepriteFrame(f!));
            else if (framesNode is JsonObject obj)
                foreach (var kv in obj) frameList.Add(ParseAsepriteFrame(kv.Value!));

            var tagList = new List<FrameTag>();
            var tagsNode = metaNode?["frameTags"] ?? metaNode?["tags"];
            if (tagsNode is JsonArray tagsArr)
                foreach (var t in tagsArr)
                    tagList.Add(new FrameTag(
                        t!["name"]!.GetValue<string>(),
                        t["from"]!.GetValue<int>(),
                        t["to"]!.GetValue<int>(),
                        t["direction"]?.GetValue<string>() ?? "forward"));

            if (tagList.Count == 0)
                tagList.Add(new FrameTag("idle", 0, frameList.Count - 1, "forward"));

            return new SpriteSheet(bmp, frameList.ToArray(), tagList);
        }
        catch { return null; }
    }

    static Frame ParseAsepriteFrame(JsonNode f)
    {
        var fr = f["frame"]!;
        return new Frame(
            new Rectangle(fr["x"]!.GetValue<int>(), fr["y"]!.GetValue<int>(),
                          fr["w"]!.GetValue<int>(), fr["h"]!.GetValue<int>()),
            f["duration"]?.GetValue<int>() ?? 100);
    }

    // ══════════════════════════════════════════════════════
    // Load từ thư mục PNG sequence
    // ══════════════════════════════════════════════════════
    /// <summary>
    /// Hỗ trợ 2 cấu trúc:
    ///   A) rootFolder/idle/0.png, rootFolder/dance/0.png ...
    ///   B) rootFolder/idle_0.png, rootFolder/dance_0.png ...
    /// </summary>
    public static SpriteSheet? LoadFromFolder(string rootFolder)
    {
        if (!Directory.Exists(rootFolder)) return null;
        try
        {
            var allFrames  = new List<Bitmap>();
            var allMeta    = new List<Frame>();
            var tagList    = new List<FrameTag>();

            // ── Kiểu A: subfolders ─────────────────────────
            var subDirs = Directory.GetDirectories(rootFolder)
                                   .OrderBy(d => d).ToArray();

            if (subDirs.Length > 0)
            {
                foreach (var dir in subDirs)
                {
                    string tagName = Path.GetFileName(dir);
                    var pngs = GetSortedPngs(dir);
                    if (pngs.Length == 0) continue;

                    int from = allFrames.Count;
                    foreach (var p in pngs)
                    {
                        var (bitmaps, durations) = LoadImageFrames(p);
                        for (int fi = 0; fi < bitmaps.Length; fi++)
                        {
                            // Duration: tên file ưu tiên hơn GIF metadata
                            int ms = ParseDurationFromName(Path.GetFileNameWithoutExtension(p));
                            if (ms == 100) ms = durations[fi]; // dùng GIF delay nếu không có trong tên
                            allFrames.Add(bitmaps[fi]);
                            allMeta.Add(new Frame(Rectangle.Empty, ms));
                        }
                    }
                    int to = allFrames.Count - 1;
                    tagList.Add(new FrameTag(tagName, from, to, "forward"));
                }
            }
            else
            {
                // ── Kiểu B: flat folder với prefix ────────────
                var pngs = GetSortedPngs(rootFolder);
                // Group by prefix (phần trước dấu _ hoặc số đầu tiên)
                var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in pngs)
                {
                    string stem   = Path.GetFileNameWithoutExtension(p);
                    string prefix = ExtractPrefix(stem);
                    if (!groups.ContainsKey(prefix)) groups[prefix] = new();
                    groups[prefix].Add(p);
                }

                if (groups.Count == 0) return null;

                foreach (var (prefix, files) in groups.OrderBy(g => g.Key))
                {
                    int from = allFrames.Count;
                    foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        var (bitmaps, durations) = LoadImageFrames(f);
                        for (int fi = 0; fi < bitmaps.Length; fi++)
                        {
                            int ms = ParseDurationFromName(Path.GetFileNameWithoutExtension(f));
                            if (ms == 100) ms = durations[fi];
                            allFrames.Add(bitmaps[fi]);
                            allMeta.Add(new Frame(Rectangle.Empty, ms));
                        }
                    }
                    tagList.Add(new FrameTag(prefix, from, allFrames.Count - 1, "forward"));
                }
            }

            if (allFrames.Count == 0) return null;

            // Nếu không có tag nào khớp state → dùng tất cả là "idle"
            if (tagList.Count == 0)
                tagList.Add(new FrameTag("idle", 0, allFrames.Count - 1, "forward"));

            return new SpriteSheet(allFrames.ToArray(), allMeta.ToArray(), tagList);
        }
        catch { return null; }
    }

    // ── Helpers ───────────────────────────────────────────
    static string[] GetSortedImages(string dir) =>
        Directory.GetFiles(dir)
                 .Where(f => { var e = Path.GetExtension(f).ToLower(); return e is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif"; })
                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();

    // Giữ tên cũ để không break code cũ
    static string[] GetSortedPngs(string dir) => GetSortedImages(dir);

    /// <summary>Load 1 file → trả về danh sách frames (GIF multi-frame được extract ra).</summary>
    static (Bitmap[] bitmaps, int[] durations) LoadImageFrames(string path)
    {
        using var img = Image.FromFile(path);
        var dim = new System.Drawing.Imaging.FrameDimension(
            img.FrameDimensionsList[0]);
        int count = img.GetFrameCount(dim);

        if (count <= 1)
            return (new[] { new Bitmap(path) }, new[] { 100 });

        var bitmaps   = new Bitmap[count];
        var durations = new int[count];

        // GIF delay lưu trong PropertyItem 0x5100 (đơn vị: 1/100 giây)
        int[]? delays = null;
        try
        {
            var prop = img.GetPropertyItem(0x5100);
            if (prop?.Value != null)
            {
                delays = new int[count];
                for (int i = 0; i < count; i++)
                    delays[i] = BitConverter.ToInt32(prop.Value, i * 4) * 10; // → ms
            }
        }
        catch { }

        for (int i = 0; i < count; i++)
        {
            img.SelectActiveFrame(dim, i);
            bitmaps[i]   = new Bitmap(img);
            durations[i] = delays != null ? Math.Max(delays[i], 20) : 100;
        }
        return (bitmaps, durations);
    }

    // "idle_003" → "idle",  "003" → "",  "run0001" → "run"
    static string ExtractPrefix(string stem)
    {
        int i = stem.Length - 1;
        while (i >= 0 && (char.IsDigit(stem[i]) || stem[i] == '_')) i--;
        string prefix = stem[..(i + 1)].TrimEnd('_');
        return prefix.Length > 0 ? prefix : stem;
    }

    // "frame_100ms" → 100,  mặc định 100ms
    static int ParseDurationFromName(string stem)
    {
        int ms = stem.IndexOf("ms", StringComparison.OrdinalIgnoreCase);
        if (ms < 1) return 100;
        int start = ms - 1;
        while (start > 0 && char.IsDigit(stem[start - 1])) start--;
        if (int.TryParse(stem[start..ms], out int v) && v > 0) return v;
        return 100;
    }

    // ══════════════════════════════════════════════════════
    // Public API
    // ══════════════════════════════════════════════════════
    public FrameTag? GetTag(string state)
    {
        if (tags.TryGetValue(state, out var t)) return t;
        if (StateAliases.TryGetValue(state, out var aliases))
            foreach (var a in aliases)
                if (tags.TryGetValue(a, out t)) return t;
        return tags.Count > 0 ? tags.Values.First() : null;
    }

    public int FrameCount(FrameTag tag) => tag.To - tag.From + 1;

    public int GetFrameIndex(FrameTag tag, long elapsedMs)
    {
        int count = FrameCount(tag);
        if (count <= 0) return tag.From;
        int totalMs = 0;
        for (int i = tag.From; i <= tag.To; i++)
            totalMs += frames[i].Duration > 0 ? frames[i].Duration : 100;
        if (totalMs <= 0) totalMs = count * 100;
        long t = elapsedMs % totalMs;
        int acc = 0;
        for (int i = tag.From; i <= tag.To; i++)
        {
            acc += frames[i].Duration > 0 ? frames[i].Duration : 100;
            if (t < acc) return i;
        }
        return tag.To;
    }

    public void DrawFrame(Graphics g, int frameIdx, float cx, float cy, float yo,
                          int maxW, int maxH, float rotation = 0f, float scale = 1f)
    {
        Bitmap? bmp = null;
        RectangleF src = RectangleF.Empty;

        if (seqFrames != null)
        {
            // PNG sequence mode
            if (frameIdx < 0 || frameIdx >= seqFrames.Length) return;
            bmp = seqFrames[frameIdx];
            src = new RectangleF(0, 0, bmp.Width, bmp.Height);
        }
        else if (sheet != null)
        {
            // Sprite sheet mode
            if (frameIdx < 0 || frameIdx >= frames.Length) return;
            var r = frames[frameIdx].Rect;
            if (r.Width == 0 || r.Height == 0) return;
            bmp = sheet;
            src = r;
        }
        if (bmp == null) return;

        float aspect = src.Width / src.Height;
        float dw = maxW, dh = maxH;
        if (aspect > (float)maxW / maxH) dh = dw / aspect;
        else                             dw = dh * aspect;
        dw *= scale; dh *= scale;

        var gs = g.Save();
        g.TranslateTransform(cx, cy + yo);
        g.RotateTransform(rotation);
        g.DrawImage(bmp, new RectangleF(-dw / 2, -dh / 2, dw, dh), src, GraphicsUnit.Pixel);
        g.Restore(gs);
    }

    /// <summary>Tên tất cả animation tags đã load — hiển thị để debug.</summary>
    public string[] TagNames => tags.Keys.ToArray();

    /// <summary>Áp dụng hàm xử lý lên tất cả frame (chỉ dùng với PNG sequence mode).</summary>
    public void ApplyToAllFrames(Func<Bitmap, Bitmap> fn)
    {
        if (seqFrames == null) return;
        for (int i = 0; i < seqFrames.Length; i++)
        {
            var result = fn(seqFrames[i]);
            seqFrames[i].Dispose();
            seqFrames[i] = result;
        }
    }

    // ── Static image processing helpers ───────────────────

    /// <summary>Xóa nền (flood-fill từ 4 góc) + bo tròn.</summary>
    public static Bitmap ProcessFrame(Bitmap src, bool removeBg, bool circularCrop)
    {
        // Đảm bảo format 32bpp ARGB
        var bmp = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.DrawImage(src, 0, 0, src.Width, src.Height);

        if (removeBg) FloodFillBackground(bmp);
        if (circularCrop) ApplyCircularMask(bmp);
        return bmp;
    }

    static void FloodFillBackground(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        byte[] buf = new byte[stride * h];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);

        // Lấy màu nền từ 4 góc (trung bình)
        Color SampleCorner(int x, int y)
        {
            int i = y * stride + x * 4;
            return Color.FromArgb(buf[i+3], buf[i+2], buf[i+1], buf[i]);
        }
        var corners = new[] {
            SampleCorner(0,0), SampleCorner(w-1,0),
            SampleCorner(0,h-1), SampleCorner(w-1,h-1)
        };
        int bgR = (int)corners.Average(c => c.R);
        int bgG = (int)corners.Average(c => c.G);
        int bgB = (int)corners.Average(c => c.B);

        const int tolerance = 38;
        bool IsBackground(int i) {
            int r = buf[i+2], g = buf[i+1], b = buf[i];
            return Math.Abs(r-bgR)+Math.Abs(g-bgG)+Math.Abs(b-bgB) < tolerance*3;
        }

        // BFS flood-fill từ 4 góc
        bool[] visited = new bool[w * h];
        var queue = new Queue<int>();
        void Enqueue(int x, int y) {
            if (x<0||x>=w||y<0||y>=h) return;
            int idx = y*w+x;
            if (visited[idx]) return;
            int bi = y*stride+x*4;
            if (buf[bi+3] < 10 || IsBackground(bi)) {
                visited[idx] = true;
                buf[bi+3] = 0;  // transparent
                queue.Enqueue(idx);
            }
        }
        foreach (var (ex,ey) in new[]{(0,0),(w-1,0),(0,h-1),(w-1,h-1)})
            Enqueue(ex,ey);

        while (queue.Count > 0) {
            int idx = queue.Dequeue();
            int px = idx % w, py = idx / w;
            Enqueue(px-1,py); Enqueue(px+1,py);
            Enqueue(px,py-1); Enqueue(px,py+1);
        }

        System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        bmp.UnlockBits(data);
    }

    static void ApplyCircularMask(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        float cx = w / 2f, cy = h / 2f;
        float r  = Math.Min(cx, cy) * 0.96f;

        var data = bmp.LockBits(new Rectangle(0,0,w,h),
            System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        byte[] buf = new byte[stride * h];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float dx = x - cx, dy = y - cy;
            float dist = MathF.Sqrt(dx*dx + dy*dy);
            if (dist > r)
            {
                int i = y * stride + x * 4;
                // Feather edge 2px
                float alpha = dist > r+2 ? 0f : 1f - (dist-r)/2f;
                buf[i+3] = (byte)(buf[i+3] * alpha);
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        bmp.UnlockBits(data);
    }

    public void Dispose()
    {
        sheet?.Dispose();
        if (seqFrames != null)
            foreach (var b in seqFrames) b.Dispose();
        GC.SuppressFinalize(this);
    }
}
