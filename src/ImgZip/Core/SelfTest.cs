using System.Diagnostics;
using System.IO;

namespace ImgZip.Core;

/// <summary>
/// 内置自测：不依赖界面，覆盖格式读写、重采样精度、两种压缩模式、输出路径规则、
/// 有损质量、透明通道、异常处理与性能。用于交付前自动化验证。
/// </summary>
public static class SelfTest
{
    private static int _pass, _fail;
    private static readonly List<string> Failures = new();

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Log.Ok(name + (detail.Length > 0 ? "  " + detail : "")); }
        else { _fail++; Failures.Add(name); Log.Fail(name + (detail.Length > 0 ? "  " + detail : "")); }
    }

    public static int Run(string workDir)
    {
        Log.Info("════════════════════════════════════════════════════════════");
        Log.Info(" 图片压缩器 · 引擎自测");
        Log.Info("════════════════════════════════════════════════════════════");
        Log.Info("工作目录: " + workDir);
        Directory.CreateDirectory(workDir);
        Log.InitFile(Path.Combine(workDir, "selftest.log"));

        try
        {
            var samples = MakeSamples(workDir);
            TestDecode(samples);
            TestResampler();
            TestScaleMode(samples, workDir);
            TestResolutionMode(samples, workDir);
            TestOutputRules(samples, workDir);
            TestFormatsAndQuality(samples, workDir);
            TestAlpha(workDir);
            TestEdgeCases(samples, workDir);
            TestRealWorldFiles(workDir);
            TestPerformance(workDir);
        }
        catch (Exception ex)
        {
            _fail++;
            Failures.Add("自测过程异常");
            Log.Fail("自测过程异常: " + ex);
        }

        Log.Info("");
        Log.Info("════════════════════════════════════════════════════════════");
        Log.Info($" 结果: 通过 {_pass}   失败 {_fail}");
        if (_fail > 0)
        {
            Log.Info(" 失败项:");
            foreach (var f in Failures) Log.Info("   - " + f);
        }
        Log.Info("════════════════════════════════════════════════════════════");
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ 样本
    private sealed record Sample(string Path, int W, int H, string Format);

    private static List<Sample> MakeSamples(string dir)
    {
        var sdir = Path.Combine(dir, "samples");
        Directory.CreateDirectory(sdir);
        var list = new List<Sample>();

        // 彩色渐变 + 圆形 + 网格，便于肉眼与数值检查
        var big = MakePattern(1600, 1200);
        var small = MakePattern(320, 240);
        var odd = MakePattern(7, 3);
        var tiny = MakePattern(1, 1);

        list.Add(Write(sdir, "gradient", big, 1600, 1200, ImageFormats.Png));
        list.Add(Write(sdir, "photo", big, 1600, 1200, ImageFormats.Jpeg));
        list.Add(Write(sdir, "bitmap", small, 320, 240, ImageFormats.Bmp));
        list.Add(Write(sdir, "anim", small, 320, 240, ImageFormats.Gif));
        list.Add(Write(sdir, "scan", small, 320, 240, ImageFormats.Tiff));
        list.Add(Write(sdir, "odd", odd, 7, 3, ImageFormats.Png));
        list.Add(Write(sdir, "一像素", tiny, 1, 1, ImageFormats.Png));
        return list;
    }

    private static Sample Write(string dir, string name, byte[] bgra, int w, int h, ImageFormatInfo fmt)
    {
        var p = Path.Combine(dir, name + fmt.Extension);
        Compressor.Encode(bgra, w, h, fmt.Name, 90, p, hasAlpha: HasAlpha(bgra));
        Log.Detail($"生成样本 {Path.GetFileName(p)}  {new FileInfo(p).Length} 字节");
        return new Sample(p, w, h, fmt.Name);
    }

    private static bool HasAlpha(byte[] b) { for (int i = 3; i < b.Length; i += 4) if (b[i] != 255) return true; return false; }

    private static byte[] MakePattern(int w, int h)
    {
        var b = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * 4;
                b[p] = (byte)(255.0 * x / Math.Max(1, w - 1));                   // B
                b[p + 1] = (byte)(255.0 * y / Math.Max(1, h - 1));               // G
                b[p + 2] = (byte)(((x / 16) + (y / 16)) % 2 == 0 ? 210 : 90);    // R
                b[p + 3] = 255;
            }
        }
        return b;
    }

    /// <summary>
    /// "照片型"样本：叠加确定性噪声，使其像真实照片一样难以被 PNG 无损压缩，
    /// 这样"压缩率"断言才有意义（纯色/渐变图 PNG 本身已经极小）。
    /// </summary>
    private static byte[] MakePhoto(int w, int h, int seed = 12345)
    {
        var b = MakePattern(w, h);
        uint s = (uint)seed;
        for (int i = 0; i < w * h; i++)
        {
            s = s * 1664525u + 1013904223u;
            int p = i * 4;
            int n = (int)((s >> 24) & 0x3F) - 32;
            b[p] = (byte)Math.Clamp(b[p] + n, 0, 255);
            b[p + 1] = (byte)Math.Clamp(b[p + 1] + (n >> 1), 0, 255);
            b[p + 2] = (byte)Math.Clamp(b[p + 2] - (n >> 1), 0, 255);
        }
        return b;
    }

    // ------------------------------------------------------------ 1. 解码
    private static void TestDecode(List<Sample> samples)
    {
        Log.Info("");
        Log.Info("【1】格式解码（PNG/JPEG/BMP/GIF/TIFF，含奇数尺寸与 1×1）");
        foreach (var s in samples)
        {
            try
            {
                var (buf, w, h, fmt) = Compressor.Decode(s.Path);
                bool ok = w == s.W && h == s.H && buf.Length == w * h * 4;
                Check($"解码 {Path.GetFileName(s.Path)} ({s.Format})", ok, $"{w}×{h} 编解码器={fmt}");
            }
            catch (Exception ex)
            {
                Check($"解码 {Path.GetFileName(s.Path)} ({s.Format})", false, ex.Message);
            }
        }
    }

    // ------------------------------------------------------- 2. 重采样精度
    private static void TestResampler()
    {
        Log.Info("");
        Log.Info("【2】重采样数值正确性（面积平均必须等于精确区域平均）");
        // 4×4 → 2×2：每个目标像素应为 2×2 区域的算术平均
        var src = new byte[4 * 4 * 4];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int p = (y * 4 + x) * 4;
                byte v = (byte)(y * 4 + x);
                src[p] = v; src[p + 1] = v; src[p + 2] = v; src[p + 3] = 255;
            }
        var dst = new byte[2 * 2 * 4];
        Resampler.Resize(src, 4, 4, dst, 2, 2, ResizeMode.Area);
        double[] expect = { (0 + 1 + 4 + 5) / 4.0, (2 + 3 + 6 + 7) / 4.0, (8 + 9 + 12 + 13) / 4.0, (10 + 11 + 14 + 15) / 4.0 };
        bool ok = true;
        for (int i = 0; i < 4; i++)
        {
            double got = dst[i * 4];
            if (Math.Abs(got - expect[i]) > 0.6) { ok = false; Log.Detail($"  期望 {expect[i]} 实际 {got}"); }
        }
        Check("4×4→2×2 面积平均精确", ok);

        // 1×1 输出 = 全图平均
        var all = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++) { all[i * 4] = (byte)(i * 15); all[i * 4 + 1] = (byte)(i * 15); all[i * 4 + 2] = (byte)(i * 15); all[i * 4 + 3] = 255; }
        var one = new byte[4];
        Resampler.Resize(all, 4, 4, one, 1, 1, ResizeMode.Area);
        double mean = Enumerable.Range(0, 16).Select(i => i * 15.0).Average();
        Check("4×4→1×1 等于全图均值", Math.Abs(one[0] - mean) < 1.5, $"期望 {mean:0.#} 实际 {one[0]}");

        // 恒等尺寸不改变像素
        var same = new byte[4 * 4 * 4];
        Resampler.Resize(src, 4, 4, same, 4, 4, ResizeMode.Auto);
        Check("尺寸不变时像素不变", same.SequenceEqual(src));

        // 不透明图不应产生 alpha 误差
        var opaque = MakePattern(100, 100);
        var half = new byte[50 * 50 * 4];
        Resampler.Resize(opaque, 100, 100, half, 50, 50, ResizeMode.Area);
        bool opaqueOk = true;
        for (int i = 3; i < half.Length; i += 4) if (half[i] != 255) { opaqueOk = false; break; }
        Check("不透明图缩放后仍为全不透明", opaqueOk);
    }

    // ------------------------------------------------------- 3. 倍率模式
    private static void TestScaleMode(List<Sample> samples, string dir)
    {
        Log.Info("");
        Log.Info("【3】倍率压缩（100% / 50% / 1% 边界）");
        var outDir = Path.Combine(dir, "scale");
        var s = samples.First(x => x.Format == "PNG" && x.W == 1600);

        foreach (var (pct, ew, eh) in new[] { (100.0, 1600, 1200), (50.0, 800, 600), (25.0, 400, 300), (1.0, 16, 12) })
        {
            var opt = new CompressOptions { ScalePercent = pct, ShrinkOnly = false, Quality = 90 };
            var r = Compressor.Compress(s.Path, opt, outDir, "_s", false, false);
            Check($"倍率 {pct}% → {ew}×{eh}", r.Success && r.Width == ew && r.Height == eh,
                r.Success ? $"{r.Width}×{r.Height}" : r.Error ?? "");
        }

        // 2×2 图缩到 1% 仍应至少 1×1
        var tiny = samples.First(x => x.W == 7);
        var r2 = Compressor.Compress(tiny.Path, new CompressOptions { ScalePercent = 1, ShrinkOnly = false }, outDir, "_s", false, false);
        Check("7×3 缩到 1% 仍 ≥1×1", r2.Success && r2.Width >= 1 && r2.Height >= 1, $"{r2.Width}×{r2.Height}");
    }

    // --------------------------------------------------- 4. 分辨率模式
    private static void TestResolutionMode(List<Sample> samples, string dir)
    {
        Log.Info("");
        Log.Info("【4】分辨率压缩（锁比例 / 不锁比例 / 只缩不放）");
        var outDir = Path.Combine(dir, "res");
        var s = samples.First(x => x.Format == "PNG" && x.W == 1600);   // 1600×1200 (4:3)

        var a = Compressor.Compress(s.Path, new CompressOptions { UseResolution = true, TargetWidth = 800, TargetHeight = 800, KeepAspect = true, ShrinkOnly = false }, outDir, "_r", false, false);
        Check("锁比例 800×800 框 → 800×600", a.Success && a.Width == 800 && a.Height == 600, $"{a.Width}×{a.Height}");

        var b = Compressor.Compress(s.Path, new CompressOptions { UseResolution = true, TargetWidth = 640, TargetHeight = 480, KeepAspect = false, ShrinkOnly = false }, outDir, "_r", false, false);
        Check("不锁比例 → 精确 640×480", b.Success && b.Width == 640 && b.Height == 480, $"{b.Width}×{b.Height}");

        var c = Compressor.Compress(s.Path, new CompressOptions { UseResolution = true, TargetWidth = 4000, TargetHeight = 3000, KeepAspect = true, ShrinkOnly = true }, outDir, "_r", false, false);
        Check("只缩不放：目标大于原图时保持原尺寸", c.Success && c.Width == 1600 && c.Height == 1200, $"{c.Width}×{c.Height}");

        var d = Compressor.Compress(s.Path, new CompressOptions { UseResolution = true, TargetWidth = 4000, TargetHeight = 3000, KeepAspect = true, ShrinkOnly = false }, outDir, "_r", false, false);
        Check("允许放大 → 4000×3000", d.Success && d.Width == 4000 && d.Height == 3000, $"{d.Width}×{d.Height}");
    }

    // ------------------------------------------------------- 5. 输出规则
    private static void TestOutputRules(List<Sample> samples, string dir)
    {
        Log.Info("");
        Log.Info("【5】输出路径（默认同目录 / 自定义目录 / 重名自动加序号 / 中文名）");
        var s = samples.First(x => x.Format == "BMP");

        var opt = new CompressOptions { ScalePercent = 50 };
        var r1 = Compressor.Compress(s.Path, opt, null, "_compressed", false, false);
        Check("默认输出到源文件同目录", r1.Success && Path.GetDirectoryName(r1.OutputPath!) == Path.GetDirectoryName(s.Path),
            Path.GetFileName(r1.OutputPath ?? ""));
        Check("默认文件名带后缀", Path.GetFileNameWithoutExtension(r1.OutputPath!).EndsWith("_compressed"));

        var outDir = Path.Combine(dir, "custom", "深一层");
        var r2 = Compressor.Compress(s.Path, opt, outDir, "_c", false, false);
        Check("自定义输出目录（含不存在的子目录）", r2.Success && Directory.Exists(outDir), r2.OutputPath ?? r2.Error ?? "");

        var r3 = Compressor.Compress(s.Path, opt, outDir, "_c", false, false);
        var r4 = Compressor.Compress(s.Path, opt, outDir, "_c", false, false);
        Check("重名自动加序号且不覆盖", r3.Success && r4.Success && r3.OutputPath != r4.OutputPath,
            $"{Path.GetFileName(r3.OutputPath!)} / {Path.GetFileName(r4.OutputPath!)}");

        var cn = samples.First(x => x.Path.Contains("一像素"));
        var r5 = Compressor.Compress(cn.Path, new CompressOptions { ScalePercent = 100 }, Path.Combine(dir, "中文目录"), "_中", false, false);
        Check("中文文件名与目录", r5.Success, Path.GetFileName(r5.OutputPath ?? r5.Error ?? ""));

        var r6 = Compressor.Compress(s.Path, opt, outDir, "_c", true, true);
        Check("保留时间戳选项可用", r6.Success);
    }

    // --------------------------------------------------- 6. 格式与质量
    private static void TestFormatsAndQuality(List<Sample> samples, string dir)
    {
        Log.Info("");
        Log.Info("【6】格式转换与有损质量");
        var outDir = Path.Combine(dir, "fmt");
        var bmp = samples.First(x => x.Format == "BMP");
        var png = samples.First(x => x.Format == "PNG" && x.W == 1600);

        foreach (var target in new[] { "PNG", "JPEG", "BMP", "GIF", "TIFF" })
        {
            var r = Compressor.Compress(bmp.Path, new CompressOptions { ScalePercent = 50, FormatName = target, Quality = 85 }, outDir, "_" + target, false, false);
            bool ok = r.Success && r.TargetFormat == target && r.OutputPath!.EndsWith(ImageFormats.ByName(target)!.Extension);
            Check($"BMP → {target}", ok, r.Success ? r.SizeText : r.Error ?? "");
        }

        // 保持原格式：BMP 进 → BMP 出
        var keep = Compressor.Compress(bmp.Path, new CompressOptions { ScalePercent = 50 }, outDir, "_keep", false, false);
        Check("保持原格式（BMP→BMP）", keep.Success && keep.TargetFormat == "BMP", keep.TargetFormat);

        // JPEG 质量必须显著影响体积
        var low = Compressor.Compress(png.Path, new CompressOptions { ScalePercent = 50, FormatName = "JPEG", Quality = 20 }, outDir, "_q20", false, false);
        var high = Compressor.Compress(png.Path, new CompressOptions { ScalePercent = 50, FormatName = "JPEG", Quality = 95 }, outDir, "_q95", false, false);
        Check("JPEG 质量 20 明显小于质量 95", low.Success && high.Success && low.OutputBytes < high.OutputBytes,
            $"{CompressResult.Fmt(low.OutputBytes)} < {CompressResult.Fmt(high.OutputBytes)}");

        // 压缩率：用"照片型"噪声源（PNG 本身就压不下去），50% JPEG 应大幅变小
        var photo = Path.Combine(dir, "photo-src.png");
        Compressor.Encode(MakePhoto(1600, 1200), 1600, 1200, "PNG", 95, photo, false);
        var r2 = Compressor.Compress(photo, new CompressOptions { ScalePercent = 50, FormatName = "JPEG", Quality = 80 }, outDir, "_small", false, false);
        Check("照片型 1600×1200 → 50% JPEG 体积大幅下降", r2.Success && r2.SavedRatio > 0.5,
            r2.SizeText + "  " + r2.SavedText + $"  (源 {CompressResult.Fmt(r2.SourceBytes)})");
    }

    // --------------------------------------------------------- 7. 透明通道
    private static void TestAlpha(string dir)
    {
        Log.Info("");
        Log.Info("【7】透明通道处理");
        var p = Path.Combine(dir, "alpha.png");
        int w = 64, h = 64;
        var buf = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                buf[i] = 200; buf[i + 1] = 120; buf[i + 2] = 60;
                buf[i + 3] = (byte)(x < w / 2 ? 255 : 0);   // 左半不透明，右半全透明
            }
        Compressor.Encode(buf, w, h, "PNG", 90, p, true);

        var r = Compressor.Compress(p, new CompressOptions { ScalePercent = 50 }, Path.Combine(dir, "alpha-out"), "_a", false, false);
        Check("透明 PNG → PNG 仍为 PNG", r.Success && r.TargetFormat == "PNG", r.TargetFormat);

        var (ob, ow, oh, _) = Compressor.Decode(r.OutputPath!);
        Check("输出仍是半透明图（保留 alpha）", HasAlpha(ob), $"{ow}×{oh}");

        // WebP/HEIC 之类没有内置编码器时，应自动退化为 PNG（带 alpha）
        var r2 = Compressor.Compress(p, new CompressOptions { ScalePercent = 50, FormatName = "WebP" }, Path.Combine(dir, "alpha-out"), "_w", false, false);
        Check("请求无编码器的格式时自动退化且不崩溃", r2.Success, r2.TargetFormat + "  " + (r2.Note ?? ""));

        // JPEG 输出必须铺白底（不能出现黑边）
        var r3 = Compressor.Compress(p, new CompressOptions { ScalePercent = 100, FormatName = "JPEG" }, Path.Combine(dir, "alpha-out"), "_j", false, false);
        var (jb, jw, jh, _) = Compressor.Decode(r3.OutputPath!);
        int px = ((jh / 2) * jw + jw / 2) * 4;   // 取一个来自透明区的像素
        bool whiteish = jb[px] > 200 && jb[px + 1] > 200 && jb[px + 2] > 200;
        Check("透明区转 JPEG 后铺白底（不出现黑边）", r3.Success && whiteish, $"BGR=({jb[px]},{jb[px + 1]},{jb[px + 2]})");
    }

    // ------------------------------------------------------- 8. 边界情况
    private static void TestEdgeCases(List<Sample> samples, string dir)
    {
        Log.Info("");
        Log.Info("【8】异常与边界");
        var bad = Path.Combine(dir, "broken.png");
        File.WriteAllText(bad, "这不是一张图片");
        var r = Compressor.Compress(bad, new CompressOptions { ScalePercent = 50 }, null, "_x", false, false);
        Check("损坏文件优雅失败（不抛异常）", !r.Success && r.Error != null, r.Error ?? "");

        var missing = Path.Combine(dir, "不存在.png");
        var r2 = Compressor.Compress(missing, new CompressOptions { ScalePercent = 50 }, null, "_x", false, false);
        Check("不存在的文件优雅失败", !r2.Success, r2.Error ?? "");

        // 1×1 缩放
        var one = samples.First(x => x.W == 1);
        var r3 = Compressor.Compress(one.Path, new CompressOptions { ScalePercent = 50, ShrinkOnly = false }, Path.Combine(dir, "one"), "_o", false, false);
        Check("1×1 图片可处理", r3.Success && r3.Width >= 1 && r3.Height >= 1, $"{r3.Width}×{r3.Height}");

        // 超大缩放倍率组合：100% 仅重编码
        var png = samples.First(x => x.Format == "PNG" && x.W == 1600);
        var r4 = Compressor.Compress(png.Path, new CompressOptions { ScalePercent = 100, FormatName = "PNG" }, Path.Combine(dir, "same"), "_same", false, false);
        Check("100% 且同格式：尺寸不变、结果有效", r4.Success && r4.Width == 1600 && r4.Height == 1200, r4.Note ?? "");

        // skip-bigger
        var r5 = Compressor.Compress(png.Path, new CompressOptions { ScalePercent = 100, FormatName = "BMP" }, Path.Combine(dir, "skip"), "_big", true, false);
        Check("skip-bigger 生效（BMP 变大时跳过并清理）", r5.Success && r5.Skipped && r5.OutputPath == null, r5.Note ?? "");
    }

    // ------------------------------------------------- 9. 真实世界的稀有格式
    private static void TestRealWorldFiles(string dir)
    {
        Log.Info("");
        Log.Info("【9】真实文件测试（在本机搜索 WebP / ICO / GIF / TIFF 实际文件）");

        var roots = new[]
        {
            @"E:\KimiWork", @"C:\Users\Rin\Pictures", @"C:\Windows\Web", @"C:\Users\Rin\AppData\Local\Temp",
        };
        var wanted = new[] { ".webp", ".ico", ".tif", ".tiff", ".gif", ".bmp", ".heic", ".avif" };
        var picked = new Dictionary<string, string>();

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                {
                    var e = Path.GetExtension(f).ToLowerInvariant();
                    if (!wanted.Contains(e) || picked.ContainsKey(e)) continue;
                    var len = new FileInfo(f).Length;
                    if (len < 1024 || len > 8 * 1024 * 1024) continue;      // 跳过极小/超大
                    picked[e] = f;
                    if (picked.Count == wanted.Length) break;
                }
            }
            catch { }
            if (picked.Count == wanted.Length) break;
        }

        if (picked.Count == 0)
        {
            Check("本机存在可用于测试的稀有格式文件", false, "未找到样本");
            return;
        }

        var outDir = Path.Combine(dir, "real");
        foreach (var (ext, path) in picked)
        {
            try
            {
                var (buf, w, h, fmt) = Compressor.Decode(path);
                bool decoded = w > 0 && h > 0 && buf.Length == w * h * 4;
                var r = Compressor.Compress(path, new CompressOptions { ScalePercent = 50, Quality = 80 }, outDir, "_real", false, false);
                Check($"真实 {ext} 文件（{Path.GetFileName(path)}）解码并压缩",
                    decoded && r.Success,
                    $"{w}×{h} → {(r.Success ? $"{r.Width}×{r.Height} {r.TargetFormat}" : r.Error)}  {r.SizeText}");
            }
            catch (Exception ex)
            {
                Check($"真实 {ext} 文件（{Path.GetFileName(path)}）", false, ex.Message);
            }
        }
    }

    // ---------------------------------------------------------- 10. 性能
    private static void TestPerformance(string dir)
    {
        Log.Info("");
        Log.Info("【10】性能（4000×3000 大图）");
        var p = Path.Combine(dir, "huge.jpg");
        var buf = MakePattern(4000, 3000);
        Compressor.Encode(buf, 4000, 3000, "JPEG", 92, p, false);

        var sw = Stopwatch.StartNew();
        var r = Compressor.Compress(p, new CompressOptions { ScalePercent = 25, FormatName = "JPEG", Quality = 80 }, Path.Combine(dir, "huge-out"), "_h", false, false);
        sw.Stop();
        Check("4000×3000 → 1000×750 成功", r.Success && r.Width == 1000 && r.Height == 750, $"{r.Width}×{r.Height}");
        Check("大图处理耗时 < 3 秒", sw.Elapsed.TotalSeconds < 3.0, $"{sw.Elapsed.TotalSeconds:0.00}s  {r.SizeText}");
    }
}
