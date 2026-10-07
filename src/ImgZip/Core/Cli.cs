using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImgZip.Core;

namespace ImgZip.Core;

/// <summary>
/// 命令行 / 自测入口。GUI 之外的第二条路径，用于自动化验证引擎正确性。
///   ImgZip.exe --cli --in a.png b.jpg --scale 50 --out D:\out
///   ImgZip.exe --cli --in C:\pics --recursive --width 1920 --height 1080 --format JPEG --quality 80
///   ImgZip.exe --selftest [工作目录]
/// </summary>
public static class Cli
{
    public static bool IsCliRequest(string[] args)
        => args.Any(a => a is "--cli" or "--selftest" or "-h" or "--help" or "--formats");

    public static int Run(string[] args)
    {
        Log.AttachToParentConsole();
        var work = Environment.CurrentDirectory;
        Log.InitFile(Path.Combine(work, "imgzip-cli.log"));
        Log.Verbose = args.Contains("--verbose");

        try
        {
            if (args.Contains("--help") || args.Contains("-h")) { PrintHelp(); return 0; }
            if (args.Contains("--formats")) { PrintFormats(); return 0; }
            if (args.Contains("--selftest")) return SelfTest.Run(ArgValue(args, "--selftest") ?? Path.Combine(work, "imgzip-selftest"));
            return RunCompress(args);
        }
        catch (Exception ex)
        {
            Log.Fail("异常: " + ex);
            return 2;
        }
    }

    private static void PrintHelp()
    {
        Log.Info("图片压缩器 ImgZip · 命令行模式");
        Log.Info("");
        Log.Info("用法:");
        Log.Info("  ImgZip.exe --cli --in <文件或目录...> [选项]");
        Log.Info("  支持图片（PNG/JPEG/WebP/…）与视频（MP4/MKV/AVI/WebM/…，需 ffmpeg）");
        Log.Info("");
        Log.Info("尺寸选项（图片与视频通用，二选一）:");
        Log.Info("  --scale <1-100>        缩放倍率百分比（默认 50）");
        Log.Info("  --width <n> --height <n>  目标分辨率");
        Log.Info("  --no-aspect            分辨率模式下不锁宽高比");
        Log.Info("  --allow-upscale        允许放大（默认只缩不放）");
        Log.Info("  --mode area|bilinear|bicubic|nearest|auto   重采样算法（仅图片）");
        Log.Info("");
        Log.Info("色深压缩:");
        Log.Info("  图片（PNG/BMP 写成真正的索引色文件）:");
        Log.Info("  --colors <2-256>       调色板颜色数（视频按 8 位处理）");
        Log.Info("  --gray                 转 8 位灰度（图片/视频）");
        Log.Info("  --bw                   转 1 位黑白（图片/视频）");
        Log.Info("  --threshold <0-255>    指定黑白阈值（仅图片）");
        Log.Info("  --dither fs|bayer|none 抖动方式（仅图片，减色时建议 fs）");
        Log.Info("");
        Log.Info("输出选项:");
        Log.Info("  --out <目录>           输出目录（默认与源文件同目录）");
        Log.Info("  --suffix <文本>        文件名后缀（默认 _compressed）");
        Log.Info("  --format <名称>        图片: PNG/JPEG/BMP/GIF/TIFF；视频: MP4/MKV/WebM（默认保持原格式）");
        Log.Info("  --quality <1-100>      有损格式质量（视频映射 CRF，默认 82）");
        Log.Info("  --cpu                  视频强制 CPU 编码（默认检测到 GPU 时优先硬件编码）");
        Log.Info("  --recursive            递归子目录");
        Log.Info("  --skip-bigger          结果更大时跳过");
        Log.Info("  --keep-time            保留原始时间戳");
        Log.Info("  --json <文件>          输出 JSON 报告");
        Log.Info("");
        Log.Info("其他:");
        Log.Info("  --formats              列出本机可读/可写的格式");
        Log.Info("  --selftest [目录]      运行内置自测");
        Log.Info("  --verbose              详细输出");
    }

    private static void PrintFormats()
    {
        Log.Info("内置支持（Windows 自带，必定可读）: PNG, JPEG, BMP, GIF, TIFF, ICO, JPEG-XR(WDP)");
        Log.Info("可写格式: PNG, JPEG, BMP, GIF, TIFF");
        var extra = DetectedExtraFormats();
        Log.Info(extra.Count > 0
            ? "系统扩展额外支持: " + string.Join(", ", extra)
            : "系统扩展额外支持: 无（安装 Windows 的 WebP/HEIF 图像扩展后即可读写对应格式）");
    }

    /// <summary>
    /// 探测系统是否注册了扩展格式的编解码器（WebP / HEIC / AVIF 等）。
    /// 判据：该扩展名在注册表中登记了 image/* 的 Content Type（说明有 shell/WIC 处理器）。
    /// </summary>
    public static List<string> DetectedExtraFormats()
    {
        var found = new List<string>();
        foreach (var f in ImageFormats.ReadOnlyFormats)
        {
            foreach (var ext in f.Extensions)
            {
                if (TryProbeExtension(ext)) { found.Add(f.Name); break; }
            }
        }
        return found;
    }

    private static readonly Dictionary<string, bool> _probeCache = new();
    private static bool TryProbeExtension(string ext)
    {
        if (_probeCache.TryGetValue(ext, out var cached)) return cached;
        bool ok = false;
        try
        {
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(ext);
            if (key?.GetValue("Content Type") is string ct && ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                ok = true;
        }
        catch { }
        _probeCache[ext] = ok;
        return ok;
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int RunCompress(string[] args)
    {
        var inputs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--in")
            {
                int j = i + 1;
                while (j < args.Length && !args[j].StartsWith("--")) { inputs.Add(args[j]); j++; }
                i = j - 1;
            }
        }
        if (inputs.Count == 0) { Log.Fail("缺少 --in 参数"); PrintHelp(); return 2; }

        bool recursive = args.Contains("--recursive");
        var files = new List<string>();
        foreach (var p in inputs)
        {
            if (Directory.Exists(p))
                files.AddRange(Directory.EnumerateFiles(p, "*.*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(f => ImageFormats.IsSupportedExtension(f) || VideoFormats.IsVideoPath(f)));
            else if (File.Exists(p)) files.Add(p);
            else Log.Fail("找不到: " + p);
        }
        if (files.Count == 0) { Log.Fail("没有可处理的文件"); return 2; }

        var opt = new CompressOptions
        {
            UseResolution = args.Contains("--width") || args.Contains("--height"),
            ScalePercent = double.TryParse(ArgValue(args, "--scale"), out var s) ? s : 50,
            TargetWidth = int.TryParse(ArgValue(args, "--width"), out var w) ? w : 1920,
            TargetHeight = int.TryParse(ArgValue(args, "--height"), out var h) ? h : 1080,
            KeepAspect = !args.Contains("--no-aspect"),
            ShrinkOnly = !args.Contains("--allow-upscale"),
            FormatName = ArgValue(args, "--format"),
            Quality = int.TryParse(ArgValue(args, "--quality"), out var q) ? q : 82,
            PreferGpu = !args.Contains("--cpu"),
            Mode = ParseMode(ArgValue(args, "--mode")),
            ColorCount = int.TryParse(ArgValue(args, "--colors"), out var cc) ? cc : 0,
            Grayscale = args.Contains("--gray"),
            BiLevel = args.Contains("--bw"),
            Threshold = int.TryParse(ArgValue(args, "--threshold"), out var th) ? th : -1,
            Dither = ParseDither(ArgValue(args, "--dither")),
        };

        string? outDir = ArgValue(args, "--out");
        string suffix = ArgValue(args, "--suffix") ?? "_compressed";
        bool skipBigger = args.Contains("--skip-bigger");
        bool keepTime = args.Contains("--keep-time");

        Log.Info($"图片压缩器 · 命令行模式");
        Log.Info($"  待处理: {files.Count} 个文件");
        Log.Info($"  模式: {(opt.UseResolution ? $"分辨率 {opt.TargetWidth}×{opt.TargetHeight}{(opt.KeepAspect ? " (锁比例)" : "")}" : $"倍率 {opt.ScalePercent:0.##}%")}");
        Log.Info($"  输出: {(outDir ?? "与源文件同目录")}  格式: {opt.FormatName ?? "保持原格式"}  质量: {opt.Quality}");
        if (opt.ReduceColor) Log.Info($"  色深: {opt.ColorDepthText}  抖动: {opt.Dither}");
        Log.Info("");

        var results = new List<CompressResult>();
        var progress = new Progress<double>(_ => { });
        bool parallel = args.Contains("--parallel");   // 复现 GUI 的线程池场景，用于排查线程相关问题
        if (parallel)
        {
            var tasks = files.Select(f => Task.Run(() => CompressOne(f, opt, outDir, suffix, skipBigger, keepTime, progress))).ToArray();
            Task.WaitAll(tasks);
            results.AddRange(tasks.Select(t => t.Result));
        }
        else
        {
            foreach (var f in files)
                results.Add(CompressOne(f, opt, outDir, suffix, skipBigger, keepTime, progress));
        }

        long before = 0, after = 0;
        int ok = 0, failed = 0, skipped = 0;
        foreach (var r in results)
        {
            if (!r.Success) { failed++; Log.Fail($"{Path.GetFileName(r.SourcePath)}: {r.Error}"); continue; }
            if (r.Skipped) { skipped++; Log.Info($"  [跳过] {Path.GetFileName(r.SourcePath)}  {r.Note}"); continue; }
            ok++;
            before += r.SourceBytes; after += r.OutputBytes;
            Log.Info($"  [完成] {Path.GetFileName(r.SourcePath)}  {r.SourceWidth}×{r.SourceHeight} → {r.Width}×{r.Height}  {r.SizeText}  {r.SavedText}  ({r.ElapsedMs:0} ms)");
            Log.Detail("→ " + r.OutputPath);
        }
        Log.Info("");
        Log.Info($"完成: 成功 {ok} / 跳过 {skipped} / 失败 {failed}");
        if (before > 0)
            Log.Info($"总体积: {CompressResult.Fmt(before)} → {CompressResult.Fmt(after)}  ({(1 - (double)after / before) * 100:0.#}% 更小)");

        var jsonPath = ArgValue(args, "--json");
        if (jsonPath != null) WriteJsonReport(jsonPath, opt, results);

        return failed > 0 ? 1 : 0;
    }

    /// <summary>按文件类型分流：图片走 WIC 管线，视频走 ffmpeg 管线。</summary>
    private static CompressResult CompressOne(string f, CompressOptions opt, string? outDir, string suffix,
        bool skipBigger, bool keepTime, IProgress<double> progress)
        => VideoFormats.IsVideoPath(f)
            ? VideoCompressor.Compress(f, opt, outDir, suffix, skipBigger, keepTime, progress)
            : Compressor.Compress(f, opt, outDir, suffix, skipBigger, keepTime);

    private static ResizeMode ParseMode(string? m) => (m ?? "auto").ToLowerInvariant() switch
    {
        "area" => ResizeMode.Area,
        "bilinear" => ResizeMode.Bilinear,
        "bicubic" => ResizeMode.Bicubic,
        "nearest" => ResizeMode.Nearest,
        _ => ResizeMode.Auto,
    };

    private static DitherMode ParseDither(string? m) => (m ?? "none").ToLowerInvariant() switch
    {
        "fs" or "floyd" or "floydsteinberg" => DitherMode.FloydSteinberg,
        "bayer" or "ordered" => DitherMode.Ordered,
        _ => DitherMode.None,
    };

    private static void WriteJsonReport(string path, CompressOptions opt, List<CompressResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"scale\": {opt.ScalePercent.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
        sb.AppendLine($"  \"useResolution\": {(opt.UseResolution ? "true" : "false")},");
        sb.AppendLine($"  \"format\": \"{opt.FormatName ?? "keep"}\",");
        sb.AppendLine($"  \"quality\": {opt.Quality},");
        sb.AppendLine("  \"items\": [");
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var p = r.SourcePath.Replace("\\", "\\\\");
            var o = (r.OutputPath ?? "").Replace("\\", "\\\\");
            sb.AppendLine($"    {{\"src\":\"{p}\",\"out\":\"{o}\",\"ok\":{(r.Success ? "true" : "false")}," +
                          $"\"srcW\":{r.SourceWidth},\"srcH\":{r.SourceHeight},\"w\":{r.Width},\"h\":{r.Height}," +
                          $"\"srcBytes\":{r.SourceBytes},\"outBytes\":{r.OutputBytes},\"ms\":{r.ElapsedMs:0}}}");
            if (i < results.Count - 1) sb.Length -= Environment.NewLine.Length;
            sb.AppendLine(i < results.Count - 1 ? "," : "");
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        Log.Info("JSON 报告: " + path);
    }
}
