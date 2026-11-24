// Build-time tool: turns the artwork in src\App\icon-source.png into a multi-size .ico (+ a PNG for the menu header).
// Without the artwork it falls back to a drawn gold ring, so the project still builds.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static Bitmap Draw(int n)
    {
        var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.Clear(Color.Transparent);
            float s = n / 256f;
            // rounded dark tile
            using (var path = new GraphicsPath())
            {
                float r = 54 * s, x = 8 * s, y = 8 * s, w = 240 * s, h = 240 * s;
                path.AddArc(x, y, r, r, 180, 90); path.AddArc(x + w - r, y, r, r, 270, 90); path.AddArc(x + w - r, y + h - r, r, r, 0, 90); path.AddArc(x, y + h - r, r, r, 90, 90); path.CloseFigure();
                using (var bg = new LinearGradientBrush(new PointF(0, 0), new PointF(0, n), Color.FromArgb(255, 38, 30, 22), Color.FromArgb(255, 12, 10, 8))) g.FillPath(bg, path);
                using (var pen = new Pen(Color.FromArgb(255, 150, 118, 44), Math.Max(1f, 5 * s))) g.DrawPath(pen, path);
            }
            // the ring: tilted ellipse with a gold gradient and an inner shine
            var rect = new RectangleF(52 * s, 70 * s, 152 * s, 116 * s);
            using (var gold = new LinearGradientBrush(new PointF(0, rect.Top), new PointF(0, rect.Bottom), Color.FromArgb(255, 255, 226, 132), Color.FromArgb(255, 176, 124, 22)))
            using (var pen = new Pen(gold, Math.Max(2f, 30 * s))) g.DrawEllipse(pen, rect);
            using (var pen = new Pen(Color.FromArgb(150, 255, 248, 210), Math.Max(1f, 5 * s))) g.DrawArc(pen, rect.X - 4 * s, rect.Y - 6 * s, rect.Width + 8 * s, rect.Height + 8 * s, 200, 110);
            using (var pen = new Pen(Color.FromArgb(120, 90, 56, 6), Math.Max(1f, 4 * s))) g.DrawArc(pen, rect.X + 12 * s, rect.Y + 12 * s, rect.Width - 24 * s, rect.Height - 22 * s, 20, 140);
        }
        return bmp;
    }

    static Bitmap _art;   // the artwork, cropped to its visible part and padded to a square

    static void LoadArt(string path)
    {
        using (var src = new Bitmap(path))
        using (var b = src.Clone(new Rectangle(0, 0, src.Width, src.Height), PixelFormat.Format32bppArgb))
        {
            int w = b.Width, h = b.Height, l = w, t = h, r = -1, bt = -1;
            BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var px = new byte[d.Stride * h]; System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length); b.UnlockBits(d);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (px[y * d.Stride + x * 4 + 3] > 24) { if (x < l) l = x; if (x > r) r = x; if (y < t) t = y; if (y > bt) bt = y; }
            if (r < 0) { l = 0; t = 0; r = w - 1; bt = h - 1; }
            int cw = r - l + 1, ch = bt - t + 1, side = Math.Max(cw, ch);
            _art = new Bitmap(side, side, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(_art)) { g.Clear(Color.Transparent); g.DrawImage(b, new Rectangle((side - cw) / 2, (side - ch) / 2, cw, ch), new Rectangle(l, t, cw, ch), GraphicsUnit.Pixel); }
        }
    }

    /// <summary>Scales the artwork to n x n. Halving in steps keeps small sizes crisp instead of blurry.</summary>
    static Bitmap Scale(int n)
    {
        Bitmap cur = (Bitmap)_art.Clone();
        while (true)
        {
            int next = cur.Width / 2 >= n ? Math.Max(n, cur.Width / 2) : n;
            var dst = new Bitmap(next, next, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            using (var ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.CompositingQuality = CompositingQuality.HighQuality; g.Clear(Color.Transparent);
                g.DrawImage(cur, new Rectangle(0, 0, next, next), 0, 0, cur.Width, cur.Height, GraphicsUnit.Pixel, ia);
            }
            cur.Dispose(); cur = dst;
            if (next == n) return cur;
        }
    }

    static Bitmap Make(int n) { return _art != null ? Scale(n) : Draw(n); }

    static byte[] Dib(Bitmap b)
    {
        int n = b.Width; var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write(40); w.Write(n); w.Write(n * 2); w.Write((short)1); w.Write((short)32); w.Write(0); w.Write(n * n * 4); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (int y = n - 1; y >= 0; y--) for (int x = 0; x < n; x++) { Color c = b.GetPixel(x, y); w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A); }
        int maskRow = ((n + 31) / 32) * 4; w.Write(new byte[maskRow * n]);
        return ms.ToArray();
    }

    static int Main(string[] a)
    {
        string outPath = a.Length > 0 ? a[0] : "app.ico"; int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 }; var images = new List<byte[]>();
        if (a.Length > 2 && File.Exists(a[2])) { try { LoadArt(a[2]); } catch (Exception ex) { Console.WriteLine("artwork not usable (" + ex.Message + ") - using the drawn icon"); _art = null; } }
        foreach (int n in sizes)
            using (Bitmap b = Make(n))
            {
                if (n >= 256) { var ms = new MemoryStream(); b.Save(ms, ImageFormat.Png); images.Add(ms.ToArray()); }
                else images.Add(Dib(b));
            }
        using (var w = new BinaryWriter(File.Create(outPath)))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length); int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32); w.Write(images[i].Length); w.Write(offset); offset += images[i].Length;
            }
            foreach (byte[] img in images) w.Write(img);
        }
        if (a.Length > 1) using (Bitmap b = Make(256)) b.Save(a[1], ImageFormat.Png);
        Console.WriteLine("icon written: " + outPath + (_art != null ? " (from artwork)" : " (drawn)"));
        return 0;
    }
}
