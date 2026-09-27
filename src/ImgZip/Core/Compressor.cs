using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgZip.Core;

/// <summary>一次压缩任务的参数。</summary>
public sealed class CompressOptions
{
    /// <summary>倍率模式：1–100（%）</summary>
    public double ScalePercent { get; set; } = 50;

    /// <summary>true = 用固定分辨率；false = 用倍率</summary>
    public bool UseResolution { get; set; }

    public int TargetWidth { get; set; } = 1920;
    public int TargetHeight { get; set; } = 1080;

    /// <summary>分辨率模式下是否锁定宽高比（按目标框内接缩放）</summary>
    public bool KeepAspect { get; set; } = true;

    /// <summary>只允许缩小，不放大</summary>
    public bool ShrinkOnly { get; set; } = true;

    /// <summary>输出格式名（PNG/JPEG/BMP/GIF/TIFF）；null = 保持原格式</summary>
    public string? FormatName { get; set; }

    /// <summary>有损格式质量 1–100</summary>
    public int Quality { get; set; } = 82;

    /// <summary>重采样算法</summary>
    public ResizeMode Mode { get; set; } = ResizeMode.Auto;

    /// <summary>计算目标尺寸（倍率或分辨率），并按需限制为"只缩不放"。</summary>
    public (int w, int h) ComputeSize(int sw, int sh)
    {
        int w, h;
        if (UseResolution)
        {
            if (KeepAspect)
            {
                double ratio = Math.Min((double)TargetWidth / sw, (double)TargetHeight / sh);
                w = (int)Math.Round(sw * ratio);
                h = (int)Math.Round(sh * ratio);
            }
            else
            {
                w = TargetWidth; h = TargetHeight;
            }
        }
        else
        {
            double k = Math.Clamp(ScalePercent, 1, 100) / 100.0;
            w = (int)Math.Round(sw * k);
            h = (int)Math.Round(sh * k);
        }

        if (ShrinkOnly && (w > sw || h > sh))
        {
            w = Math.Min(w, sw);
            h = Math.Min(h, sh);
        }
        return (Math.Max(1, w), Math.Max(1, h));
    }
}

/// <summary>单个文件的压缩结果。</summary>
public sealed class CompressResult
{
    public required string SourcePath { get; init; }
    public string? OutputPath { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public long SourceBytes { get; init; }
    public long OutputBytes { get; set; }
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string SourceFormat { get; set; } = "";
    public string TargetFormat { get; set; } = "";
    public double ElapsedMs { get; set; }
    public bool Skipped { get; set; }
    public string? Note { get; set; }

    public double SavedRatio => SourceBytes <= 0 || OutputBytes <= 0 ? 0 : 1.0 - (double)OutputBytes / SourceBytes;

    public string SizeText => OutputBytes > 0
        ? $"{Fmt(SourceBytes)} → {Fmt(OutputBytes)}"
        : Fmt(SourceBytes);

    public string SavedText => OutputBytes <= 0 ? "" :
        (SavedRatio >= 0 ? $"省 {SavedRatio * 100:0.#}%" : $"增 {Math.Abs(SavedRatio) * 100:0.#}%");

    public static string Fmt(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#") + " KB",
        _ => (bytes / 1024.0 / 1024.0).ToString("0.##") + " MB",
    };
}

/// <summary>
/// 压缩引擎：解码（WIC）→ 重采样 → 编码（WPF 内置编码器）。
/// 不依赖任何 UI，界面与 CLI 共用同一套逻辑。
/// </summary>
public static class Compressor
{
    /// <summary>解码任意受支持格式；自动应用 EXIF 旋转。</summary>
    public static (byte[] bgra, int w, int h, string format) Decode(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];

        BitmapSource src = frame;
        try
        {
            if (frame.Metadata is BitmapMetadata md && md.ContainsQuery("/app1/ifd/{ushort=274}"))
            {
                int orientation = Convert.ToInt32(md.GetQuery("/app1/ifd/{ushort=274}"));
                src = ApplyOrientation(frame, orientation);
            }
        }
        catch { /* 元数据异常不影响解码 */ }

        var conv = new FormatConvertedBitmap();
        conv.BeginInit();
        conv.Source = src;
        conv.DestinationFormat = PixelFormats.Bgra32;
        conv.EndInit();
        conv.Freeze();

        int w = conv.PixelWidth, h = conv.PixelHeight;
        var buf = new byte[(long)w * h * 4];
        conv.CopyPixels(buf, w * 4, 0);
        return (buf, w, h, decoder.CodecInfo?.FriendlyName ?? "未知");
    }

    private static BitmapSource ApplyOrientation(BitmapSource s, int orientation)
    {
        Transform? t = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            6 => new RotateTransform(90),
            8 => new RotateTransform(270),
            _ => null,
        };
        if (t == null) return s;
        var tb = new TransformedBitmap(s, t);
        tb.Freeze();
        return tb;
    }

    /// <summary>把 BGRA 缓冲编码落盘。</summary>
    public static void Encode(byte[] bgra, int w, int h, string targetFormat, int quality, string outPath, bool hasAlpha)
    {
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);

        BitmapEncoder enc = targetFormat.ToUpperInvariant() switch
        {
            "JPEG" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) },
            "PNG" => new PngBitmapEncoder { Interlace = PngInterlaceOption.Off },
            "BMP" => new BmpBitmapEncoder(),
            "GIF" => new GifBitmapEncoder(),
            "TIFF" => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
            _ => throw new NotSupportedException("不支持的输出格式: " + targetFormat),
        };

        BitmapSource toWrite = src;
        if (targetFormat.Equals("JPEG", StringComparison.OrdinalIgnoreCase) && hasAlpha)
            toWrite = FlattenOnWhite(bgra, w, h);

        enc.Frames.Add(BitmapFrame.Create(toWrite));
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        enc.Save(fs);
    }

    private static BitmapSource FlattenOnWhite(byte[] bgra, int w, int h)
    {
        var flat = new byte[bgra.Length];
        Parallel.For(0, w * h, i =>
        {
            int p = i * 4;
            byte a = bgra[p + 3];
            if (a == 255) { flat[p] = bgra[p]; flat[p + 1] = bgra[p + 1]; flat[p + 2] = bgra[p + 2]; }
            else
            {
                float af = a / 255f;
                flat[p] = (byte)(bgra[p] * af + 255 * (1 - af));
                flat[p + 1] = (byte)(bgra[p + 1] * af + 255 * (1 - af));
                flat[p + 2] = (byte)(bgra[p + 2] * af + 255 * (1 - af));
            }
            flat[p + 3] = 255;
        });
        var s = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, flat, w * 4);
        s.Freeze();
        return s;
    }

    private static bool HasAlpha(byte[] bgra)
    {
        for (int p = 3; p < bgra.Length; p += 4) if (bgra[p] != 255) return true;
        return false;
    }

    /// <summary>压缩单张图片。</summary>
    public static CompressResult Compress(string path, CompressOptions opt, string? outputDir, string suffix,
        bool skipIfBigger, bool preserveTimestamps)
    {
        var sw = Stopwatch.StartNew();
        var res = new CompressResult { SourcePath = path, SourceBytes = SafeLength(path) };
        try
        {
            var (buf, w, h, fmt) = Decode(path);
            var srcInfo = ImageFormats.ByPath(path);
            res.SourceWidth = w;
            res.SourceHeight = h;
            res.SourceFormat = srcInfo?.Name ?? fmt;

            var (tw, th) = opt.ComputeSize(w, h);
            bool alpha = HasAlpha(buf);

            byte[] outBuf;
            if (tw == w && th == h)
            {
                outBuf = buf;
                res.Note = "尺寸未变，仅重新编码";
            }
            else
            {
                outBuf = new byte[(long)tw * th * 4];
                Resampler.Resize(buf, w, h, outBuf, tw, th, opt.Mode);
            }

            var target = opt.FormatName == null
                ? ImageFormats.PickTarget(srcInfo, alpha)
                : ImageFormats.ByName(opt.FormatName) ?? ImageFormats.Png;
            if (!target.CanEncode) target = alpha ? ImageFormats.Png : ImageFormats.Jpeg;

            string outFile = BuildOutputPath(path, outputDir, suffix, target);
            Encode(outBuf, tw, th, target.Name, opt.Quality, outFile, alpha);

            res.OutputPath = outFile;
            res.OutputBytes = SafeLength(outFile);
            res.Width = tw;
            res.Height = th;
            res.TargetFormat = target.Name;
            res.Success = true;

            if (skipIfBigger && res.OutputBytes >= res.SourceBytes)
            {
                try { File.Delete(outFile); } catch { }
                res.Skipped = true;
                res.OutputPath = null;
                res.OutputBytes = 0;
                res.Note = "结果更大，已按设置跳过并删除";
            }
            else if (preserveTimestamps)
            {
                try
                {
                    var s = new FileInfo(path);
                    File.SetCreationTime(outFile, s.CreationTime);
                    File.SetLastWriteTime(outFile, s.LastWriteTime);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            res.Success = false;
            res.Error = Describe(ex);
        }
        sw.Stop();
        res.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        return res;
    }

    private static long SafeLength(string p)
    {
        try { return new FileInfo(p).Length; } catch { return 0; }
    }

    /// <summary>把异常变成"人话 + 首个出错位置"，便于用户与排查。</summary>
    private static string Describe(Exception ex)
    {
        var msg = ex.Message;
        if (ex is System.Runtime.InteropServices.COMException)
            msg = "系统无法解码该图片（可能缺少对应的图像扩展）：" + ex.Message.Split('\n')[0];
        var frame = (ex.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "";
        var text = string.IsNullOrEmpty(frame) ? msg : msg + "  [" + frame + "]";

        // 同时写完整堆栈到日志，便于排查（正常失败不写，避免刷屏）
        try
        {
            if (ex is not FileNotFoundException and not NotSupportedException)
            {
                var log = Path.Combine(AppContext.BaseDirectory, "imgzip-errors.log");
                File.AppendAllText(log, $"[{DateTime.Now:HH:mm:ss}] {ex}\n\n", System.Text.Encoding.UTF8);
            }
        }
        catch { }
        return text;
    }

    /// <summary>输出文件名：同目录加后缀；冲突自动加序号。</summary>
    public static string BuildOutputPath(string srcPath, string? outputDir, string suffix, ImageFormatInfo target)
    {
        string dir = string.IsNullOrWhiteSpace(outputDir) ? (Path.GetDirectoryName(srcPath) ?? ".") : outputDir!;
        Directory.CreateDirectory(dir);
        string baseName = Path.GetFileNameWithoutExtension(srcPath) + suffix;
        string candidate = Path.Combine(dir, baseName + target.Extension);
        int i = 1;
        while (File.Exists(candidate) && !SameFile(candidate, srcPath))
        {
            candidate = Path.Combine(dir, $"{baseName}_{i}{target.Extension}");
            i++;
        }
        return candidate;
    }

    private static bool SameFile(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
