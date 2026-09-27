using ImgZip.Core;

namespace ImgZip;

/// <summary>GUI 启动参数（由命令行解析，供主窗口使用）。</summary>
public static class LaunchArgs
{
    /// <summary>启动时预加入队列的文件/目录。</summary>
    public static readonly List<string> Preload = new();

    /// <summary>启动后自动开始压缩（配合 --add 便于批量/无人值守）。</summary>
    public static bool AutoRun { get; set; }

    /// <summary>
    /// 启动时预设的色深，语义同色深下拉的 Tag：
    /// &gt;0 调色板颜色数 · -1 灰度 · -2 黑白 · <see cref="int.MinValue"/> 未指定。
    /// </summary>
    public static int DepthTag { get; set; } = int.MinValue;

    /// <summary>启动时预设的抖动方式（null 表示未指定）。</summary>
    public static DitherMode? Dither { get; set; }

    /// <summary>启动时预设的输出格式名称（null 表示未指定）。</summary>
    public static string? FormatName { get; set; }
}
