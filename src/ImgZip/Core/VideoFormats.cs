using System.IO;

namespace ImgZip.Core;

/// <summary>视频格式表：识别哪些文件按视频处理，以及输出容器映射。</summary>
public static class VideoFormats
{
    /// <summary>可识别的视频扩展名（解码能力由 ffmpeg 决定，实际远不止这些）。</summary>
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".webm", ".flv", ".wmv",
        ".ts", ".mpg", ".mpeg", ".3gp", ".ogv", ".vob", ".rm", ".rmvb",
    };

    /// <summary>可显式选择的输出容器（加进界面"输出格式"下拉）。</summary>
    public static readonly string[] OutputNames = { "MP4", "MKV", "WebM" };

    public static bool IsVideoPath(string path)
        => Extensions.Contains(Path.GetExtension(path ?? ""));

    /// <summary>输出目标：容器 / 扩展名（编码参数在 VideoCompressor 里按色深动态拼装）。</summary>
    public sealed record VideoTarget(string Container, string Ext, string VCodec, string ACodec);

    public static readonly VideoTarget Mp4 = new("MP4", ".mp4", "libx264", "aac");

    public static readonly VideoTarget Mkv = new("MKV", ".mkv", "libx264", "aac");

    public static readonly VideoTarget Webm = new("WebM", ".webm", "libvpx-vp9", "libopus");

    /// <summary>
    /// 选输出容器：显式指定的 MP4/MKV/WebM 优先；
    /// "保持原格式"时按源扩展名就近映射（mp4/m4v/mov→MP4，mkv→MKV，webm→WebM，其余→MP4，兼容性最好）。
    /// </summary>
    public static VideoTarget PickTarget(string srcPath, string? formatName)
    {
        if (!string.IsNullOrEmpty(formatName))
        {
            foreach (var n in OutputNames)
                if (n.Equals(formatName, StringComparison.OrdinalIgnoreCase))
                    return n switch { "MKV" => Mkv, "WebM" => Webm, _ => Mp4 };
        }
        return Path.GetExtension(srcPath).ToLowerInvariant() switch
        {
            ".mkv" => Mkv,
            ".webm" => Webm,
            _ => Mp4,
        };
    }
}
