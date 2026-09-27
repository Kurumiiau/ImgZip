using System.Threading.Tasks;

namespace ImgZip.Core;

public enum ResizeMode
{
    /// <summary>缩小用面积平均、放大用双三次（默认，兼顾质量与速度）</summary>
    Auto,
    /// <summary>面积平均：缩小时最保真，推荐</summary>
    Area,
    /// <summary>双线性</summary>
    Bilinear,
    /// <summary>双三次 Catmull-Rom</summary>
    Bicubic,
    /// <summary>最近邻：像素画/线稿保边缘</summary>
    Nearest,
}

/// <summary>
/// 32 位 BGRA 位图的分离式高质量重采样。
/// 关键点：
///   * 缩小走"面积平均"（先横后纵两次一维盒式滤波），等价于对每个目标像素做源区域加权平均，
///     不会出现抽样混叠（这是缩略图质量的关键）。
///   * 放大走 Catmull-Rom 双三次。
///   * Alpha 全程预乘，避免透明图边缘出现黑边。
///   * 按行并行，大图也能秒级完成。
/// </summary>
public static class Resampler
{
    public static void Resize(byte[] src, int sw, int sh, byte[] dst, int dw, int dh, ResizeMode mode)
    {
        if (dw <= 0 || dh <= 0) throw new ArgumentException("目标尺寸必须为正");
        if (dw == sw && dh == sh) { Buffer.BlockCopy(src, 0, dst, 0, Math.Min(src.Length, dst.Length)); return; }

        var m = mode == ResizeMode.Auto ? (dw < sw || dh < sh ? ResizeMode.Area : ResizeMode.Bicubic) : mode;

        // 预乘 alpha
        var pre = new float[sw * sh * 4];
        Premultiply(src, pre, sw * sh);

        float[] mid;
        int mw, mh;

        if (m == ResizeMode.Nearest)
        {
            Nearest(pre, sw, sh, dst, dw, dh);
            Unpremultiply(dst, dw * dh);
            return;
        }

        if (m == ResizeMode.Area || m == ResizeMode.Bilinear)
        {
            // 横向
            mw = dw; mh = sh;
            mid = new float[mw * mh * 4];
            ScaleAxis(pre, sw, sh, mid, mw, mh, horizontal: true, area: m == ResizeMode.Area);
            // 纵向
            var fin = new float[dw * dh * 4];
            ScaleAxis(mid, mw, mh, fin, dw, dh, horizontal: false, area: m == ResizeMode.Area);
            ToBytes(fin, dst, dw * dh);
            Unpremultiply(dst, dw * dh);
            return;
        }

        // 双三次
        mw = dw; mh = sh;
        mid = new float[mw * mh * 4];
        ScaleAxisCubic(pre, sw, sh, mid, mw, mh, horizontal: true);
        var fin2 = new float[dw * dh * 4];
        ScaleAxisCubic(mid, mw, mh, fin2, dw, dh, horizontal: false);
        ToBytes(fin2, dst, dw * dh);
        Unpremultiply(dst, dw * dh);
    }

    private static void Premultiply(byte[] src, float[] dst, int count)
    {
        Parallel.For(0, count, i =>
        {
            int p = i * 4;
            float a = src[p + 3] / 255f;
            dst[p] = src[p + 2] * a;      // R   (BGRA 内存序)
            dst[p + 1] = src[p + 1] * a;  // G
            dst[p + 2] = src[p] * a;      // B
            dst[p + 3] = src[p + 3];
        });
    }

    private static void Unpremultiply(byte[] buf, int count)
    {
        Parallel.For(0, count, i =>
        {
            int p = i * 4;
            byte a = buf[p + 3];
            if (a == 0) { buf[p] = buf[p + 1] = buf[p + 2] = 0; return; }
            float inv = 255f / a;
            buf[p] = Clamp(buf[p] * inv);          // B
            buf[p + 1] = Clamp(buf[p + 1] * inv);  // G
            buf[p + 2] = Clamp(buf[p + 2] * inv);  // R
        });
    }

    private static byte Clamp(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);

    private static void ToBytes(float[] src, byte[] dst, int count)
    {
        Parallel.For(0, count, i =>
        {
            int p = i * 4;
            dst[p] = Clamp(src[p + 2]);      // B
            dst[p + 1] = Clamp(src[p + 1]);  // G
            dst[p + 2] = Clamp(src[p]);      // R
            dst[p + 3] = Clamp(src[p + 3]);  // A
        });
    }

    private static void Nearest(float[] src, int sw, int sh, byte[] dst, int dw, int dh)
    {
        Parallel.For(0, dh, y =>
        {
            int sy = Math.Min(sh - 1, (int)((y + 0.5) * sh / dh));
            for (int x = 0; x < dw; x++)
            {
                int sx = Math.Min(sw - 1, (int)((x + 0.5) * sw / dw));
                int s = (sy * sw + sx) * 4, d = (y * dw + x) * 4;
                dst[d] = Clamp(src[s + 2]);
                dst[d + 1] = Clamp(src[s + 1]);
                dst[d + 2] = Clamp(src[s]);
                dst[d + 3] = Clamp(src[s + 3]);
            }
        });
    }

    /// <summary>
    /// 一维缩放（面积平均或线性）。area=true 时按覆盖面积加权，等价于精确的盒式重采样。
    /// </summary>
    private static void ScaleAxis(float[] src, int sw, int sh, float[] dst, int dw, int dh, bool horizontal, bool area)
    {
        if (horizontal)
        {
            Parallel.For(0, sh, y =>
            {
                int sRow = y * sw * 4, dRow = y * dw * 4;
                if (area && dw < sw)
                {
                    double ratio = (double)sw / dw;
                    for (int x = 0; x < dw; x++)
                    {
                        double x0 = x * ratio, x1 = (x + 1) * ratio;
                        int i0 = (int)Math.Floor(x0), i1 = (int)Math.Ceiling(x1) - 1;
                        if (i1 >= sw) i1 = sw - 1;
                        float r = 0, g = 0, b = 0, a = 0, wsum = 0;
                        for (int i = i0; i <= i1; i++)
                        {
                            float w = (float)(Math.Min(x1, i + 1) - Math.Max(x0, i));
                            if (w <= 0) continue;
                            int s = sRow + i * 4;
                            r += src[s] * w; g += src[s + 1] * w; b += src[s + 2] * w; a += src[s + 3] * w;
                            wsum += w;
                        }
                        if (wsum <= 0) wsum = 1;
                        int d = dRow + x * 4;
                        dst[d] = r / wsum; dst[d + 1] = g / wsum; dst[d + 2] = b / wsum; dst[d + 3] = a / wsum;
                    }
                }
                else
                {
                    double ratio = (double)sw / dw;
                    for (int x = 0; x < dw; x++)
                    {
                        double cx = Math.Min(sw - 1.0, Math.Max(0.0, (x + 0.5) * ratio - 0.5));
                        int i0 = (int)Math.Floor(cx);
                        int i1 = Math.Min(sw - 1, i0 + 1);
                        float t = (float)(cx - i0);
                        int s0 = sRow + i0 * 4, s1 = sRow + i1 * 4, d = dRow + x * 4;
                        for (int c = 0; c < 4; c++) dst[d + c] = src[s0 + c] * (1 - t) + src[s1 + c] * t;
                    }
                }
            });
        }
        else
        {
            Parallel.For(0, dw, x =>
            {
                if (area && dh < sh)
                {
                    double ratio = (double)sh / dh;
                    for (int y = 0; y < dh; y++)
                    {
                        double y0 = y * ratio, y1 = (y + 1) * ratio;
                        int j0 = (int)Math.Floor(y0), j1 = (int)Math.Ceiling(y1) - 1;
                        if (j1 >= sh) j1 = sh - 1;
                        float r = 0, g = 0, b = 0, a = 0, wsum = 0;
                        for (int j = j0; j <= j1; j++)
                        {
                            float w = (float)(Math.Min(y1, j + 1) - Math.Max(y0, j));
                            if (w <= 0) continue;
                            int s = (j * sw + x) * 4;
                            r += src[s] * w; g += src[s + 1] * w; b += src[s + 2] * w; a += src[s + 3] * w;
                            wsum += w;
                        }
                        if (wsum <= 0) wsum = 1;
                        int d = (y * dw + x) * 4;
                        dst[d] = r / wsum; dst[d + 1] = g / wsum; dst[d + 2] = b / wsum; dst[d + 3] = a / wsum;
                    }
                }
                else
                {
                    double ratio = (double)sh / dh;
                    for (int y = 0; y < dh; y++)
                    {
                        double cy = Math.Min(sh - 1.0, Math.Max(0.0, (y + 0.5) * ratio - 0.5));
                        int j0 = (int)Math.Floor(cy);
                        int j1 = Math.Min(sh - 1, j0 + 1);
                        float t = (float)(cy - j0);
                        int s0 = (j0 * sw + x) * 4, s1 = (j1 * sw + x) * 4, d = (y * dw + x) * 4;
                        for (int c = 0; c < 4; c++) dst[d + c] = src[s0 + c] * (1 - t) + src[s1 + c] * t;
                    }
                }
            });
        }
    }

    /// <summary>Catmull-Rom 双三次（仅放大时使用）</summary>
    private static void ScaleAxisCubic(float[] src, int sw, int sh, float[] dst, int dw, int dh, bool horizontal)
    {
        if (horizontal)
        {
            Parallel.For(0, sh, y =>
            {
                int sRow = y * sw * 4, dRow = y * dw * 4;
                double ratio = (double)sw / dw;
                for (int x = 0; x < dw; x++)
                {
                    double cx = (x + 0.5) * ratio - 0.5;
                    int i1 = (int)Math.Floor(cx);
                    float t = (float)(cx - i1);
                    for (int c = 0; c < 4; c++)
                    {
                        float v = 0;
                        for (int k = -1; k <= 2; k++)
                        {
                            int i = Math.Clamp(i1 + k, 0, sw - 1);
                            v += src[sRow + i * 4 + c] * CubicWeight(t - k);
                        }
                        dst[dRow + x * 4 + c] = v;
                    }
                }
            });
        }
        else
        {
            Parallel.For(0, dw, x =>
            {
                double ratio = (double)sh / dh;
                for (int y = 0; y < dh; y++)
                {
                    double cy = (y + 0.5) * ratio - 0.5;
                    int j1 = (int)Math.Floor(cy);
                    float t = (float)(cy - j1);
                    for (int c = 0; c < 4; c++)
                    {
                        float v = 0;
                        for (int k = -1; k <= 2; k++)
                        {
                            int j = Math.Clamp(j1 + k, 0, sh - 1);
                            v += src[(j * sw + x) * 4 + c] * CubicWeight(t - k);
                        }
                        dst[(y * dw + x) * 4 + c] = v;
                    }
                }
            });
        }
    }

    private static float CubicWeight(float t)
    {
        // Catmull-Rom (a = -0.5)
        t = Math.Abs(t);
        if (t < 1) return 1.5f * t * t * t - 2.5f * t * t + 1f;
        if (t < 2) return -0.5f * t * t * t + 2.5f * t * t - 4f * t + 2f;
        return 0f;
    }
}
