namespace ImgZip;

/// <summary>GUI 启动参数（由命令行解析，供主窗口使用）。</summary>
public static class LaunchArgs
{
    /// <summary>启动时预加入队列的文件/目录。</summary>
    public static readonly List<string> Preload = new();

    /// <summary>启动后自动开始压缩（配合 --add 便于批量/无人值守）。</summary>
    public static bool AutoRun { get; set; }
}
