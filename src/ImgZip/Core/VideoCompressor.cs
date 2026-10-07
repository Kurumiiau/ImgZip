using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgZip.Core;

/// <summary>
/// 视频压缩引擎：基于 ffmpeg。
/// 职责：定位 ffmpeg / ffprobe → 探测视频信息 → 抽帧缩略图 → 压缩（分辨率缩放 + 色深压缩 + CRF 质量）。
/// 与图片共用 CompressOptions（倍率 / 目标分辨率 / 只缩不放 / 质量映射 CRF / 灰度 / 黑白）。
/// </summary>
public static class VideoCompressor
{
    // ───────────────────────── ffmpeg 定位 ─────────────────────────

    private static string? _ffmpeg;
    private static string? _ffprobe;
    private static readonly object _lock = new();

    public static string? FfmpegPath { get { Locate(); return _ffmpeg; } }
    public static string? FfprobePath { get { Locate(); return _ffprobe; } }

    /// <summary>未找到 ffmpeg 时给用户的指引。</summary>
    public static string MissingHint =>
        "视频功能需要 ffmpeg。请把 ffmpeg.exe 和 ffprobe.exe 放到本程序同目录，或加入系统 PATH 后重启本程序。";

    private static void Locate()
    {
        lock (_lock)
        {
            if (_ffmpeg != null) return;

            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImgZip", "ffmpeg", "ffmpeg.exe"),
            };

            _ffmpeg = candidates.FirstOrDefault(File.Exists) ?? FindOnPath("ffmpeg.exe");
            _ffprobe = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "ffprobe.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImgZip", "ffmpeg", "ffprobe.exe"),
            }.FirstOrDefault(File.Exists) ?? FindOnPath("ffprobe.exe");

            static string? FindOnPath(string exe)
            {
                try
                {
                    var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var d in dirs)
                    {
                        try { var p = Path.Combine(d, exe); if (File.Exists(p)) return p; } catch { }
                    }
                }
                catch { }
                return null;
            }
        }
    }

    // ───────────────────────── 探测与缩略图 ─────────────────────────

    public sealed record VideoInfo(int Width, int Height, double DurationSec, string Codec, string PixFmt, bool HasAudio);

    /// <summary>ffprobe 读取宽高 / 时长 / 编码 / 像素格式 / 有无音轨。</summary>
    public static VideoInfo? Probe(string path)
    {
        Locate();
        if (_ffprobe == null) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffprobe,
                Arguments = "-v error -select_streams v:0 -show_entries stream=width,height,codec_name,pix_fmt " +
                            "-show_entries format=duration -of json -- \"" + path + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            if (p.ExitCode != 0) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var stream = root.GetProperty("streams")[0];

            int w = stream.GetProperty("width").GetInt32();
            int h = stream.GetProperty("height").GetInt32();
            string codec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
            string pix = stream.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() ?? "" : "";
            double dur = 0;
            if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d))
                double.TryParse(d.GetString(), System.Globalization.CultureInfo.InvariantCulture, out dur);

            // 有无音轨：再来一次轻量探测
            bool hasAudio = HasAudioStream(path);

            return new VideoInfo(w, h, dur, codec, pix, hasAudio);
        }
        catch { return null; }
    }

    private static bool HasAudioStream(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffprobe!,
                Arguments = "-v error -select_streams a -show_entries stream=codec_type -of csv=p=0 -- \"" + path + "\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var s = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10000);
            return s.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>抽一帧做缩略图（返回已冻结的 BitmapImage；失败返回 null）。</summary>
    public static ImageSource? GrabThumbnail(string path, double durationSec = 0)
    {
        Locate();
        if (_ffmpeg == null) return null;
        try
        {
            // 取前 1 秒（太短的视频取第 0 秒）
            string ss = durationSec > 1.2 ? "1" : "0";
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpeg,
                Arguments = $"-ss {ss} -i \"{path}\" -frames:v 1 -vf scale=128:-2 -f mjpeg pipe:1",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit(15000);
            byte[] jpg = ms.ToArray();
            if (jpg.Length < 100) return null;

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = new MemoryStream(jpg);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    public static string FmtDuration(double sec)
    {
        if (sec <= 0) return "?";
        var t = TimeSpan.FromSeconds(sec);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    // ───────────────────────── GPU 硬件编码 ─────────────────────────

    private static string? _gpu;   // "nvenc" / "amf" / "qsv" / null（无可用 GPU 编码器）

    /// <summary>可用的硬件编码器（每次进程首次调用时探测并缓存）。</summary>
    public static string? DetectGpu()
    {
        if (_gpu != null) return _gpu;
        Locate();
        if (_ffmpeg == null) { _gpu = ""; return null; }

        foreach (var (kind, codec) in new[]
        {
            ("nvenc", "h264_nvenc"),   // NVIDIA
            ("amf",   "h264_amf"),     // AMD
            ("qsv",   "h264_qsv"),     // Intel 核显
        })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpeg,
                    Arguments = $"-y -hide_banner -loglevel error -f lavfi -i color=black:s=256x256:d=0.3 -c:v {codec} -f null -",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) continue;
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                if (p.ExitCode == 0) { _gpu = kind; return _gpu; }
            }
            catch { }
        }
        _gpu = "";
        return null;
    }

    // ───────────────────────── 压缩 ─────────────────────────

    /// <summary>
    /// 压缩单个视频。分辨率沿用 CompressOptions 的倍率/目标分辨率逻辑；
    /// 质量 1–100 映射 CRF；色深压缩支持 灰度 / 黑白 / 8 位（高位深源自动降到 8 位）。
    /// </summary>
    public static CompressResult Compress(string path, CompressOptions opt, string? outputDir, string suffix,
        bool skipIfBigger, bool preserveTimestamps, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var res = new CompressResult { SourcePath = path, SourceBytes = SafeLength(path) };
        try
        {
            Locate();
            if (_ffmpeg == null)
                throw new FileNotFoundException(MissingHint);

            var info = Probe(path) ?? throw new NotSupportedException("ffmpeg 无法识别该视频（文件损坏或编码不支持）");
            res.SourceWidth = info.Width;
            res.SourceHeight = info.Height;
            res.SourceFormat = $"{info.Codec} · {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()}";

            var (tw, th) = opt.ComputeSize(info.Width, info.Height);
            var target = VideoFormats.PickTarget(path, opt.FormatName);

            string outFile = BuildOutputPath(path, outputDir, suffix, target);
            progress?.Report(0.02);

            // ---- 组装滤镜链 ----
            var filters = new List<string>();
            if (tw != info.Width || th != info.Height)
                filters.Add($"scale={tw}:{th}:flags=lanczos");

            // 色深压缩：灰度 / 黑白（阈值法）/ 高位深→8 位
            bool depthNote = false;
            if (opt.BiLevel)
            {
                // 先在 YUV 上做阈值，再压成单通道灰度（lutyuv 需要完整 YUV 平面）
                filters.Add("lutyuv=y='if(gt(val\\,128)\\,255\\,0)'");
                filters.Add("format=gray");
                depthNote = true;
            }
            else if (opt.Grayscale)
            {
                filters.Add("format=gray");
                depthNote = true;
            }
            else if (info.PixFmt.Contains("10") || info.PixFmt.Contains("12") || info.PixFmt.Contains("16"))
            {
                filters.Add("format=yuv420p");
                depthNote = true;   // 高位深源 → 8 位
            }
            else if (opt.ColorCount >= 2)
            {
                // 视频没有"调色板索引"的概念：按 8 位 yuv420p 输出（本身就是色深压缩的常用档）
                filters.Add("format=yuv420p");
                depthNote = true;
            }

            // ---- 编码参数 ----
            // 速度与体验优先（实测校准）：
            //  · 有 GPU 时优先硬件编码（NVENC/AMF/QSV）：CPU 占用极低、速度数倍提升
            //  · CPU 路径：h264 用 veryfast + 质量 1–100 → CRF 45–18（82 → 23），
            //    并限制编码线程数为 核数-2，给系统留出余量，避免"卡到爆"
            bool isVp9 = target.Container == "WebM";
            int crf = isVp9
                ? Math.Clamp((int)Math.Round(58 - 0.30 * opt.Quality), 15, 47)
                : Math.Clamp((int)Math.Round(46 - 0.28 * opt.Quality), 16, 45);

            string? gpu = opt.PreferGpu && !isVp9 ? DetectGpu() : null;   // WebM(VP9) 无通用硬编码器
            string gpuNote = "";
            string vArgs;
            // 硬件编码器的质量标尺普遍偏软（同等数值码率更高），按实测补偿：
            // NVENC +9、AMF +4、QSV +2 后，体积与 x264 CRF 大致同档
            int gpuCq = Math.Clamp(crf + (gpu == "nvenc" ? 9 : gpu == "amf" ? 4 : 2), 16, 51);
            if (gpu == "nvenc")
            {
                vArgs = $"-c:v h264_nvenc -preset p4 -tune hq -rc vbr -cq {gpuCq} -b:v 0 -spatial-aq 1 -pix_fmt yuv420p";
                gpuNote = "GPU 加速（NVENC）";
            }
            else if (gpu == "amf")
            {
                vArgs = $"-c:v h264_amf -quality balanced -rc cqp -qp_i {gpuCq} -qp_p {gpuCq} -pix_fmt yuv420p";
                gpuNote = "GPU 加速（AMF）";
            }
            else if (gpu == "qsv")
            {
                vArgs = $"-c:v h264_qsv -preset faster -global_quality {gpuCq} -pix_fmt yuv420p";
                gpuNote = "GPU 加速（QSV）";
            }
            else
            {
                // CPU 软编码：限流，留 2 个核心给系统
                int threads = Math.Max(2, Environment.ProcessorCount - 2);
                vArgs = isVp9
                    ? $"-c:v {target.VCodec} -deadline good -cpu-used 4 -row-mt 1 -threads {threads}"
                    : $"-c:v {target.VCodec} -preset veryfast -threads {threads}";

                if (opt.Grayscale || opt.BiLevel)
                {
                    vArgs += " -pix_fmt gray";   // 真正的单通道输出（h264/vp9 均支持）
                }
                else
                {
                    vArgs += " -pix_fmt yuv420p";
                }
                if (opt.PreferGpu && isVp9) gpuNote = "WebM 使用 CPU 编码";
            }
            // GPU 编码器只吃 yuv420p：灰度/黑白时补一个转换（画面仍是纯灰，只是封装为彩色平面）
            if (gpu != null && (opt.Grayscale || opt.BiLevel)) filters.Add("format=yuv420p");
            string aArgs = isVp9 ? $"-c:a {target.ACodec} -b:a 96k" : $"-c:a {target.ACodec} -b:a 128k";

            var args = new StringBuilder();
            args.Append("-y -hide_banner -nostdin -loglevel error ");
            args.Append($"-i \"{path}\" ");
            if (filters.Count > 0) args.Append($"-vf \"{string.Join(",", filters)}\" ");
            args.Append(vArgs).Append(' ');
            args.Append($"-crf {crf} ");
            args.Append(aArgs).Append(' ');
            if (target.Container == "MP4") args.Append("-movflags +faststart ");
            args.Append("-progress pipe:1 -nostats ");
            args.Append($"\"{outFile}\"");

            RunFfmpeg(args.ToString(), info.DurationSec, progress, ct);
            progress?.Report(0.98);

            res.OutputPath = outFile;
            res.OutputBytes = SafeLength(outFile);
            res.Width = tw;
            res.Height = th;
            res.TargetFormat = target.Container;
            res.Success = true;
            if (tw == info.Width && th == info.Height) res.Note = "分辨率未变，仅重编码";
            if (gpuNote != "")
                res.Note = (res.Note == null ? "" : res.Note + "；") + gpuNote;
            if (depthNote)
                res.Note = (res.Note == null ? "" : res.Note + "；") + "色深：" +
                    (opt.BiLevel ? "黑白 1 位" : opt.Grayscale ? "灰度 8 位" : "8 位");

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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            res.Success = false;
            res.Error = ex is FileNotFoundException or NotSupportedException ? ex.Message : "ffmpeg: " + ex.Message;
        }
        sw.Stop();
        res.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        return res;
    }

    /// <summary>执行 ffmpeg 并解析 -progress 输出（支持取消：取消时杀进程并删半成品）。</summary>
    private static void RunFfmpeg(string args, double durationSec, IProgress<double>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg!,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.ASCII,
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ffmpeg");
        using var reg = ct.Register(() => { try { p.Kill(); } catch { } });

        // stderr 只在出错时读尾部（loglevel error 时才有内容）
        string err = "";
        var errTask = p.StandardError.ReadToEndAsync(ct);

        if (progress != null)
        {
            // stdout: out_time_us=NNN 一行一行输出
            var so = p.StandardOutput;
            string? line;
            while ((line = so.ReadLine()) != null)
            {
                if (ct.IsCancellationRequested) break;
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && durationSec > 0)
                {
                    if (long.TryParse(line.AsSpan("out_time_us=".Length), out long us))
                    {
                        double r = Math.Clamp(us / 1_000_000.0 / durationSec, 0, 0.97);
                        progress.Report(r);
                    }
                }
            }
        }

        if (!p.WaitForExit(10 * 60 * 1000))   // 上限 10 分钟
            throw new TimeoutException("压缩超时");

        ct.ThrowIfCancellationRequested();

        err = errTask.IsCompleted ? (errTask.Result ?? "") : "";
        if (p.ExitCode != 0)
        {
            string msg = string.IsNullOrWhiteSpace(err) ? $"ffmpeg 退出码 {p.ExitCode}" : err.Trim();
            throw new Exception(msg.Split('\n').Last(l => l.Length > 0));
        }
    }

    private static long SafeLength(string p)
    {
        try { return new FileInfo(p).Length; } catch { return 0; }
    }

    /// <summary>输出文件名：同目录加后缀；冲突自动加序号（与图片版逻辑一致）。</summary>
    public static string BuildOutputPath(string srcPath, string? outputDir, string suffix, VideoFormats.VideoTarget target)
    {
        // 注意相对路径：GetDirectoryName("a.mp4") 返回空串而非 null，必须用 GetFullPath 兜底
        string dir = string.IsNullOrWhiteSpace(outputDir)
            ? (Path.GetDirectoryName(Path.GetFullPath(srcPath)) ?? ".")
            : outputDir!;
        Directory.CreateDirectory(dir);
        string baseName = Path.GetFileNameWithoutExtension(srcPath) + suffix;
        string candidate = Path.Combine(dir, baseName + target.Ext);
        int i = 1;
        while (File.Exists(candidate) &&
               !string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(srcPath), StringComparison.OrdinalIgnoreCase))
        {
            candidate = Path.Combine(dir, $"{baseName}_{i}{target.Ext}");
            i++;
        }
        return candidate;
    }
}
