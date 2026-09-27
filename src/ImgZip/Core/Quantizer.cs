using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ImgZip.Core;

/// <summary>抖动方式</summary>
public enum DitherMode { None, FloydSteinberg, Ordered }

/// <summary>索引色图像（调色板 + 每像素索引）</summary>
public sealed class IndexedImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Indices { get; init; }   // 长度 = Width*Height
    public required byte[] PaletteR { get; init; }
    public required byte[] PaletteG { get; init; }
    public required byte[] PaletteB { get; init; }
    public byte[]? PaletteA { get; init; }          // null 表示全部不透明

    public int Count => PaletteR.Length;
    /// <summary>PNG/BMP 的索引位深：1 / 2 / 4 / 8</summary>
    public int BitsPerPixel => Count <= 2 ? 1 : Count <= 4 ? 2 : Count <= 16 ? 4 : 8;
    public bool HasAlpha => PaletteA != null && PaletteA.Any(a => a < 255);
}

/// <summary>
/// 色彩量化：中位切分（median cut）生成调色板，再用 k-means 精修，
/// 最后把每个像素映射到最近的调色板颜色（可选 Floyd–Steinberg / 有序抖动）。
/// 用于"压缩色位"：8 位 256 色 / 4 位 16 色 / 1 位黑白 / 灰度。
/// </summary>
public static class Quantizer
{
    private const int MaxSample = 120_000;   // 取样上限，保证大图也很快

    /// <summary>量化成索引色图。</summary>
    public static IndexedImage Quantize(byte[] bgra, int w, int h, int maxColors, DitherMode dither)
    {
        maxColors = Math.Clamp(maxColors, 2, 256);
        int n = w * h;

        bool hasAlpha = false;
        for (int i = 3; i < n * 4; i += 4) if (bgra[i] < 255) { hasAlpha = true; break; }

        // ---- 1. 取样（大图跳点采样）----
        int step = Math.Max(1, n / MaxSample);
        var samples = new List<(int r, int g, int b, int a, int count)>();
        for (int i = 0; i < n; i += step)
        {
            int p = i * 4;
            samples.Add((bgra[p + 2], bgra[p + 1], bgra[p], hasAlpha ? bgra[p + 3] : 255, 1));
        }

        // ---- 2. 中位切分 ----
        var boxes = new List<List<(int r, int g, int b, int a, int count)>> { samples };
        while (boxes.Count < maxColors)
        {
            int best = -1; long bestScore = -1;
            for (int i = 0; i < boxes.Count; i++)
            {
                var bx = boxes[i];
                if (bx.Count < 2) continue;
                var (range, _) = LongestAxis(bx);
                long score = (long)range * bx.Count;
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best < 0) break;

            var box = boxes[best];
            var (_, axis) = LongestAxis(box);
            box.Sort((x, y) => Key(x, axis).CompareTo(Key(y, axis)));
            int mid = box.Count / 2;
            var left = box.GetRange(0, mid);
            var right = box.GetRange(mid, box.Count - mid);
            if (left.Count == 0 || right.Count == 0) break;
            boxes[best] = left;
            boxes.Add(right);
        }

        var palette = boxes.Where(b => b.Count > 0).Select(Average).ToList();

        // ---- 3. k-means 精修（几轮即可显著提升观感）----
        RefineKMeans(samples, palette, hasAlpha ? 4 : 3, iterations: 6);

        // 去掉重复色
        palette = palette.Distinct().ToList();
        while (palette.Count > maxColors) palette.RemoveAt(palette.Count - 1);

        var pr = palette.Select(c => (byte)c.r).ToArray();
        var pg = palette.Select(c => (byte)c.g).ToArray();
        var pb = palette.Select(c => (byte)c.b).ToArray();
        byte[]? pa = hasAlpha ? palette.Select(c => (byte)c.a).ToArray() : null;

        // ---- 4. 映射（含抖动）----
        var indices = Map(bgra, w, h, pr, pg, pb, pa, dither);

        return new IndexedImage
        {
            Width = w, Height = h, Indices = indices,
            PaletteR = pr, PaletteG = pg, PaletteB = pb, PaletteA = pa,
        };
    }

    private static int Key((int r, int g, int b, int a, int count) c, int axis) => axis switch
    {
        0 => c.r, 1 => c.g, 2 => c.b, _ => c.a,
    };

    private static (int range, int axis) LongestAxis(List<(int r, int g, int b, int a, int count)> box)
    {
        int rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0, aMin = 255, aMax = 0;
        foreach (var c in box)
        {
            if (c.r < rMin) rMin = c.r; if (c.r > rMax) rMax = c.r;
            if (c.g < gMin) gMin = c.g; if (c.g > gMax) gMax = c.g;
            if (c.b < bMin) bMin = c.b; if (c.b > bMax) bMax = c.b;
            if (c.a < aMin) aMin = c.a; if (c.a > aMax) aMax = c.a;
        }
        int dr = rMax - rMin, dg = gMax - gMin, db = bMax - bMin, da = aMax - aMin;
        int range = Math.Max(Math.Max(dr, dg), Math.Max(db, da));
        int axis = range == dr ? 0 : range == dg ? 1 : range == db ? 2 : 3;
        return (range, axis);
    }

    private static (int r, int g, int b, int a, int count) Average(List<(int r, int g, int b, int a, int count)> box)
    {
        long r = 0, g = 0, b = 0, a = 0, n = 0;
        foreach (var c in box) { r += (long)c.r * c.count; g += (long)c.g * c.count; b += (long)c.b * c.count; a += (long)c.a * c.count; n += c.count; }
        if (n == 0) return (0, 0, 0, 255, 0);
        return ((int)(r / n), (int)(g / n), (int)(b / n), (int)(a / n), (int)n);
    }

    private static void RefineKMeans(List<(int r, int g, int b, int a, int count)> pts, List<(int r, int g, int b, int a, int count)> centers, int dims, int iterations)
    {
        int k = centers.Count;
        if (k == 0) return;
        var sumR = new long[k]; var sumG = new long[k]; var sumB = new long[k]; var sumA = new long[k]; var cnt = new long[k];

        for (int it = 0; it < iterations; it++)
        {
            Array.Clear(sumR); Array.Clear(sumG); Array.Clear(sumB); Array.Clear(sumA); Array.Clear(cnt);
            foreach (var p in pts)
            {
                int best = 0; long bestD = long.MaxValue;
                for (int c = 0; c < k; c++)
                {
                    long dr = p.r - centers[c].r, dg = p.g - centers[c].g, db = p.b - centers[c].b, da = dims == 4 ? p.a - centers[c].a : 0;
                    long d = dr * dr + dg * dg + db * db + da * da;
                    if (d < bestD) { bestD = d; best = c; }
                }
                sumR[best] += (long)p.r * p.count; sumG[best] += (long)p.g * p.count;
                sumB[best] += (long)p.b * p.count; sumA[best] += (long)p.a * p.count; cnt[best] += p.count;
            }
            for (int c = 0; c < k; c++)
            {
                if (cnt[c] == 0) continue;
                centers[c] = ((int)(sumR[c] / cnt[c]), (int)(sumG[c] / cnt[c]), (int)(sumB[c] / cnt[c]),
                              dims == 4 ? (int)(sumA[c] / cnt[c]) : 255, (int)cnt[c]);
            }
        }
    }

    // ---------------------------------------------------------------- 映射
    private static byte[] Map(byte[] bgra, int w, int h, byte[] pr, byte[] pg, byte[] pb, byte[]? pa, DitherMode dither)
    {
        int n = w * h;
        var idx = new byte[n];
        int k = pr.Length;

        if (dither == DitherMode.None)
        {
            var cache = new Dictionary<int, byte>(1 << 16);
            for (int i = 0; i < n; i++)
            {
                int p = i * 4;
                int key = (bgra[p + 2] << 16) | (bgra[p + 1] << 8) | bgra[p] | (pa != null ? bgra[p + 3] << 24 : 0);
                if (!cache.TryGetValue(key, out byte v))
                {
                    v = (byte)Nearest(bgra[p + 2], bgra[p + 1], bgra[p], pa != null ? bgra[p + 3] : 255, pr, pg, pb, pa);
                    cache[key] = v;
                }
                idx[i] = v;
            }
            return idx;
        }

        if (dither == DitherMode.Ordered)
        {
            // 8×8 Bayer
            int[,] bayer =
            {
                { 0,32, 8,40, 2,34,10,42 }, { 48,16,56,24,50,18,58,26 },
                { 12,44, 4,36,14,46, 6,38 }, { 60,28,52,20,62,30,54,22 },
                { 3,35,11,43, 1,33, 9,41 }, { 51,19,59,27,49,17,57,25 },
                { 15,47, 7,39,13,45, 5,37 }, { 63,31,55,23,61,29,53,21 },
            };
            double spread = 255.0 / Math.Max(2, k);
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x, p = i * 4;
                    double t = (bayer[y & 7, x & 7] / 64.0 - 0.5) * spread;
                    int r = Clamp(bgra[p + 2] + t), g = Clamp(bgra[p + 1] + t), b = Clamp(bgra[p] + t);
                    idx[i] = (byte)Nearest(r, g, b, pa != null ? bgra[p + 3] : 255, pr, pg, pb, pa);
                }
            });
            return idx;
        }

        // Floyd–Steinberg（误差扩散，串行）
        var buf = new double[n * 3];
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            buf[i * 3] = bgra[p + 2]; buf[i * 3 + 1] = bgra[p + 1]; buf[i * 3 + 2] = bgra[p];
        }
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                int r = Clamp(buf[i * 3]), g = Clamp(buf[i * 3 + 1]), b = Clamp(buf[i * 3 + 2]);
                int a = pa != null ? bgra[i * 4 + 3] : 255;
                int ci = Nearest(r, g, b, a, pr, pg, pb, pa);
                idx[i] = (byte)ci;
                double er = r - pr[ci], eg = g - pg[ci], eb = b - pb[ci];
                Spread(buf, w, h, x + 1, y, er, eg, eb, 7.0 / 16);
                Spread(buf, w, h, x - 1, y + 1, er, eg, eb, 3.0 / 16);
                Spread(buf, w, h, x, y + 1, er, eg, eb, 5.0 / 16);
                Spread(buf, w, h, x + 1, y + 1, er, eg, eb, 1.0 / 16);
            }
        }
        return idx;
    }

    private static void Spread(double[] buf, int w, int h, int x, int y, double er, double eg, double eb, double f)
    {
        if (x < 0 || y < 0 || x >= w || y >= h) return;
        int i = (y * w + x) * 3;
        buf[i] += er * f; buf[i + 1] += eg * f; buf[i + 2] += eb * f;
    }

    private static int Clamp(double v) => v <= 0 ? 0 : v >= 255 ? 255 : (int)(v + 0.5);

    private static int Nearest(int r, int g, int b, int a, byte[] pr, byte[] pg, byte[] pb, byte[]? pa)
    {
        int best = 0; long bestD = long.MaxValue;
        bool useA = pa != null;
        for (int c = 0; c < pr.Length; c++)
        {
            long dr = r - pr[c], dg = g - pg[c], db = b - pb[c];
            long d = dr * dr + dg * dg + db * db;
            if (useA) { long da = a - pa![c]; d += da * da; }
            if (d < bestD) { bestD = d; best = c; if (d == 0) break; }
        }
        return best;
    }

    // ---------------------------------------------------------- 灰度 / 黑白
    /// <summary>8 位灰度（ITU-R BT.601 亮度）</summary>
    public static void ToGrayscale(byte[] bgra, int n)
    {
        Parallel.For(0, n, i =>
        {
            int p = i * 4;
            int y = (int)(0.299 * bgra[p + 2] + 0.587 * bgra[p + 1] + 0.114 * bgra[p] + 0.5);
            bgra[p] = bgra[p + 1] = bgra[p + 2] = (byte)y;
        });
    }

    /// <summary>Otsu 自动阈值</summary>
    public static int OtsuThreshold(byte[] gray, int n)
    {
        var hist = new int[256];
        for (int i = 0; i < n; i++) hist[gray[i * 4]]++;
        double sum = 0;
        for (int t = 0; t < 256; t++) sum += (double)t * hist[t];
        double sumB = 0; int wB = 0; double best = -1; int threshold = 127;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            int wF = n - wB;
            if (wF == 0) break;
            sumB += (double)t * hist[t];
            double mB = sumB / wB, mF = (sum - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; threshold = t; }
        }
        return threshold;
    }

    /// <summary>二值化：转成黑白两色索引图</summary>
    public static IndexedImage ToBiLevel(byte[] bgra, int w, int h, int threshold, DitherMode dither)
    {
        int n = w * h;
        var gray = new byte[n * 4];
        Buffer.BlockCopy(bgra, 0, gray, 0, n * 4);
        ToGrayscale(gray, n);
        int t = threshold >= 0 ? Math.Clamp(threshold, 0, 255) : OtsuThreshold(gray, n);

        var idx = new byte[n];
        if (dither == DitherMode.Ordered)
        {
            int[,] bayer = { { 0,32,8,40,2,34,10,42 }, { 48,16,56,24,50,18,58,26 }, { 12,44,4,36,14,46,6,38 }, { 60,28,52,20,62,30,54,22 },
                             { 3,35,11,43,1,33,9,41 }, { 51,19,59,27,49,17,57,25 }, { 15,47,7,39,13,45,5,37 }, { 63,31,55,23,61,29,53,21 } };
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    double v = gray[i * 4] + (bayer[y & 7, x & 7] / 64.0 - 0.5) * 64.0;
                    idx[i] = (byte)(v > t ? 1 : 0);
                }
            });
        }
        else if (dither == DitherMode.FloydSteinberg)
        {
            var buf = new double[n];
            for (int i = 0; i < n; i++) buf[i] = gray[i * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    double v = buf[i];
                    int bit = v > t ? 1 : 0;
                    idx[i] = (byte)bit;
                    double err = v - (bit == 1 ? 255 : 0);
                    Add(buf, w, h, x + 1, y, err * 7 / 16);
                    Add(buf, w, h, x - 1, y + 1, err * 3 / 16);
                    Add(buf, w, h, x, y + 1, err * 5 / 16);
                    Add(buf, w, h, x + 1, y + 1, err * 1 / 16);
                }
        }
        else
        {
            Parallel.For(0, n, i => idx[i] = (byte)(gray[i * 4] > t ? 1 : 0));
        }

        return new IndexedImage
        {
            Width = w, Height = h, Indices = idx,
            PaletteR = new byte[] { 0, 255 }, PaletteG = new byte[] { 0, 255 }, PaletteB = new byte[] { 0, 255 },
        };
    }

    private static void Add(double[] buf, int w, int h, int x, int y, double e)
    {
        if (x < 0 || y < 0 || x >= w || y >= h) return;
        buf[y * w + x] += e;
    }
}
