using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgZip.Core;

public enum JobStatus { Pending, Working, Done, Failed, Skipped }

/// <summary>队列中的一张图片。</summary>
public sealed class JobItem : INotifyPropertyChanged
{
    public string SourcePath { get; }
    public string FileName { get; }
    public long SourceBytes { get; private set; }
    public int SourceWidth { get; private set; }
    public int SourceHeight { get; private set; }
    public string SourceFormat { get; private set; } = "";

    private ImageSource? _thumb;
    public ImageSource? Thumbnail { get => _thumb; private set => Set(ref _thumb, value); }

    private JobStatus _status = JobStatus.Pending;
    public JobStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusBrush)); OnPropertyChanged(nameof(IsWorking)); } }
    }

    private string _statusText = "等待";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    private string _detail = "";
    /// <summary>第二行说明：原始信息 / 结果信息</summary>
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    private string _preview = "";
    /// <summary>预计目标尺寸，随设置实时更新</summary>
    public string Preview { get => _preview; set => Set(ref _preview, value); }

    public bool IsWorking => _status == JobStatus.Working;

    private readonly bool _isVideo;
    /// <summary>该文件按视频处理（走 ffmpeg 管线）。</summary>
    public bool IsVideo => _isVideo;

    public double DurationSec { get; private set; }

    public CompressResult? Result { get; set; }

    public SolidColorBrush StatusBrush => _status switch
    {
        JobStatus.Done => new SolidColorBrush(Color.FromRgb(0x7F, 0xBF, 0x9A)),
        JobStatus.Failed => new SolidColorBrush(Color.FromRgb(0xD9, 0x7A, 0x8A)),
        JobStatus.Working => new SolidColorBrush(Color.FromRgb(0xEB, 0x9F, 0xAA)),
        JobStatus.Skipped => new SolidColorBrush(Color.FromRgb(0xB7, 0x9F, 0xA7)),
        _ => new SolidColorBrush(Color.FromRgb(0xC0, 0xCE, 0xE4)),
    };

    public JobItem(string path)
    {
        SourcePath = path;
        FileName = Path.GetFileName(path);
        SourceBytes = SafeLen(path);
        _isVideo = VideoFormats.IsVideoPath(path);
        Detail = JobItemFormat.Size(SourceBytes);
        LoadMeta();
    }

    private static long SafeLen(string p) { try { return new FileInfo(p).Length; } catch { return 0; } }

    /// <summary>读取尺寸/格式与缩略图（图片走 WIC 小图解码；视频走 ffprobe + ffmpeg 抽帧）。</summary>
    private void LoadMeta()
    {
        if (_isVideo) { LoadVideoMeta(); return; }
        try
        {
            using var fs = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = dec.Frames[0];
            SourceWidth = frame.PixelWidth;
            SourceHeight = frame.PixelHeight;
            SourceFormat = ImageFormats.ByPath(SourcePath)?.Name ?? dec.CodecInfo?.FriendlyName ?? "?";
            Detail = $"{SourceWidth}×{SourceHeight} · {SourceFormat} · {JobItemFormat.Size(SourceBytes)}";

            // 缩略图：按 96px 解码
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.DecodePixelWidth = 96;
            bi.UriSource = new Uri(SourcePath);
            bi.EndInit();
            bi.Freeze();
            Thumbnail = bi;
        }
        catch (Exception ex)
        {
            SourceFormat = "无法识别";
            Detail = "无法读取：" + ex.Message;
            Status = JobStatus.Failed;
            StatusText = "无法读取";
        }
    }

    private void LoadVideoMeta()
    {
        if (VideoCompressor.FfmpegPath == null)
        {
            SourceFormat = "视频";
            Detail = VideoCompressor.MissingHint;
            Status = JobStatus.Failed;
            StatusText = "缺少 ffmpeg";
            return;
        }
        var info = VideoCompressor.Probe(SourcePath);
        if (info == null)
        {
            SourceFormat = "视频";
            Detail = "ffmpeg 无法识别该视频";
            Status = JobStatus.Failed;
            StatusText = "无法读取";
            return;
        }
        SourceWidth = info.Width;
        SourceHeight = info.Height;
        DurationSec = info.DurationSec;
        SourceFormat = $"{info.Codec}{(info.HasAudio ? "" : " · 无声")}";
        Detail = $"{info.Width}×{info.Height} · {VideoCompressor.FmtDuration(info.DurationSec)} · {SourceFormat} · {JobItemFormat.Size(SourceBytes)}";
        Thumbnail = VideoCompressor.GrabThumbnail(SourcePath, info.DurationSec);
    }

    /// <summary>按当前设置刷新"预计输出"提示。</summary>
    public void RefreshPreview(CompressOptions opt)
    {
        if (SourceWidth <= 0) { Preview = ""; return; }
        var (w, h) = opt.ComputeSize(SourceWidth, SourceHeight);
        if (_isVideo)
        {
            string fmt = VideoFormats.PickTarget(SourcePath, opt.FormatName).Container;
            Preview = $"{w}×{h} · {fmt}";
            return;
        }
        string imgFmt = opt.FormatName ?? ImageFormats.PickTarget(ImageFormats.ByPath(SourcePath), false).Name;
        Preview = $"{w}×{h} · {imgFmt}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class JobItemFormat
{
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#") + " KB",
        _ => (bytes / 1024.0 / 1024.0).ToString("0.##") + " MB",
    };
}
