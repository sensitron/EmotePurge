$ErrorActionPreference = 'Stop'

$csharp = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class LogoTool
{
    public static Bitmap Load(string path)
    {
        using (var img = Image.FromFile(path))
        {
            var b = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b)) { g.DrawImage(img, 0, 0, img.Width, img.Height); }
            return b;
        }
    }

    // Flood fill from the image border: every background-colored pixel connected to the
    // border becomes transparent. Enclosed dark areas (eyes, mouth) stay opaque.
    public static void KeyOut(Bitmap bmp, int tol)
    {
        int w = bmp.Width, h = bmp.Height;
        var rect = new Rectangle(0, 0, w, h);
        var d = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int stride = d.Stride;
        byte[] px = new byte[stride * h];
        Marshal.Copy(d.Scan0, px, 0, px.Length);
        int ci = stride + 4; // pixel (1,1)
        int bB = px[ci], bG = px[ci + 1], bR = px[ci + 2];
        bool[] visited = new bool[w * h];
        var q = new Queue<int>();
        int tolSq = tol * tol * 3;
        Action<int, int> push = delegate(int x, int y)
        {
            int idx = y * w + x;
            if (visited[idx]) return;
            int o = y * stride + x * 4;
            int db = px[o] - bB, dg = px[o + 1] - bG, dr = px[o + 2] - bR;
            if (db * db + dg * dg + dr * dr <= tolSq) { visited[idx] = true; q.Enqueue(idx); }
        };
        for (int x = 0; x < w; x++) { push(x, 0); push(x, h - 1); }
        for (int y = 0; y < h; y++) { push(0, y); push(w - 1, y); }
        while (q.Count > 0)
        {
            int idx = q.Dequeue();
            int x = idx % w, y = idx / w;
            px[y * stride + x * 4 + 3] = 0;
            if (x > 0) push(x - 1, y);
            if (x < w - 1) push(x + 1, y);
            if (y > 0) push(x, y - 1);
            if (y < h - 1) push(x, y + 1);
        }
        Marshal.Copy(px, 0, d.Scan0, px.Length);
        bmp.UnlockBits(d);
    }

    public static Rectangle OpaqueBBox(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var rect = new Rectangle(0, 0, w, h);
        var d = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = d.Stride;
        byte[] px = new byte[stride * h];
        Marshal.Copy(d.Scan0, px, 0, px.Length);
        bmp.UnlockBits(d);
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (px[y * stride + x * 4 + 3] > 0)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    public static Bitmap CropSquare(Bitmap src, Rectangle bbox, double pad, Color bg)
    {
        int side = (int)Math.Ceiling(Math.Max(bbox.Width, bbox.Height) * (1 + 2 * pad));
        var dst = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            if (bg.A > 0) g.Clear(bg);
            int cx = bbox.X + bbox.Width / 2, cy = bbox.Y + bbox.Height / 2;
            g.DrawImageUnscaled(src, side / 2 - cx, side / 2 - cy);
        }
        return dst;
    }

    public static Bitmap Resize(Bitmap src, int size)
    {
        var dst = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            var attr = new ImageAttributes();
            attr.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(src, new Rectangle(0, 0, size, size), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attr);
        }
        return dst;
    }

    public static byte[] Png(Bitmap b)
    {
        using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); }
    }

    // ICO container with PNG-compressed entries (supported by every current browser).
    public static void WriteIco(string path, Bitmap[] images)
    {
        var blobs = new List<byte[]>();
        foreach (var im in images) blobs.Add(Png(im));
        using (var fs = new FileStream(path, FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((short)0); bw.Write((short)1); bw.Write((short)images.Length);
            int offset = 6 + 16 * images.Length;
            for (int i = 0; i < images.Length; i++)
            {
                var im = images[i];
                bw.Write((byte)(im.Width >= 256 ? 0 : im.Width));
                bw.Write((byte)(im.Height >= 256 ? 0 : im.Height));
                bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((short)1); bw.Write((short)32);
                bw.Write(blobs[i].Length); bw.Write(offset);
                offset += blobs[i].Length;
            }
            foreach (var b in blobs) bw.Write(b);
        }
    }

    public static int AlphaAt(Bitmap b, int x, int y) { return b.GetPixel(x, y).A; }
}
'@

Add-Type -TypeDefinition $csharp -ReferencedAssemblies System.Drawing

$branding = Split-Path -Parent $MyInvocation.MyCommand.Path
$pub = Join-Path (Split-Path -Parent $branding) 'public'
$bgColor = [System.Drawing.Color]::FromArgb(255, 2, 6, 23)   # slate-950 / #020617
$transparent = [System.Drawing.Color]::FromArgb(0, 0, 0, 0)

# ---- logo-mark: favicon + header mark + app icons ----
$b = [LogoTool]::Load("$branding\logo-mark.png")
[LogoTool]::KeyOut($b, 30)
$bboxB = [LogoTool]::OpaqueBBox($b)
Write-Output "bbox b: $bboxB"

$favBase = [LogoTool]::CropSquare($b, $bboxB, 0.03, $transparent)
$sizes = @{}
foreach ($s in 16, 32, 48) { $sizes[$s] = [LogoTool]::Resize($favBase, $s) }
[LogoTool]::WriteIco("$pub\favicon.ico", @($sizes[48], $sizes[32], $sizes[16]))

$mark = [LogoTool]::Resize($favBase, 128)
$mark.Save("$pub\logo.png", [System.Drawing.Imaging.ImageFormat]::Png)

$touchBase = [LogoTool]::CropSquare($b, $bboxB, 0.12, $bgColor)
$touch = [LogoTool]::Resize($touchBase, 180)
$touch.Save("$pub\apple-touch-icon.png", [System.Drawing.Imaging.ImageFormat]::Png)

$iconBase = [LogoTool]::CropSquare($b, $bboxB, 0.10, $bgColor)
$icon192 = [LogoTool]::Resize($iconBase, 192)
$icon192.Save("$pub\icon-192.png", [System.Drawing.Imaging.ImageFormat]::Png)
$icon512 = [LogoTool]::Resize($iconBase, 512)
$icon512.Save("$pub\icon-512.png", [System.Drawing.Imaging.ImageFormat]::Png)

# ---- logo-full: hero version (with the pixel squares) ----
$a = [LogoTool]::Load("$branding\logo-full.png")
[LogoTool]::KeyOut($a, 30)
$bboxA = [LogoTool]::OpaqueBBox($a)
Write-Output "bbox a: $bboxA"
$heroBase = [LogoTool]::CropSquare($a, $bboxA, 0.03, $transparent)
$hero = [LogoTool]::Resize($heroBase, 512)
$hero.Save("$pub\logo-hero.png", [System.Drawing.Imaging.ImageFormat]::Png)

# Sanity: corners transparent, center opaque
Write-Output ("mark corner alpha: " + [LogoTool]::AlphaAt($mark, 2, 2) + ", center alpha: " + [LogoTool]::AlphaAt($mark, 64, 64))
Write-Output ("hero corner alpha: " + [LogoTool]::AlphaAt($hero, 2, 2) + ", center alpha: " + [LogoTool]::AlphaAt($hero, 256, 256))

foreach ($obj in @($b, $a, $favBase, $touchBase, $iconBase, $heroBase, $mark, $touch, $icon192, $icon512, $hero) + $sizes.Values) { $obj.Dispose() }
Get-ChildItem $pub -File | Select-Object Name, Length | Format-Table -AutoSize
