using System.IO;
using System.Windows;
using System.Windows.Threading;
using ImgZip.Core;

namespace ImgZip;

public partial class App : Application
{
    /// <summary>崩溃日志位置（放在程序同目录，便于用户反馈问题）。</summary>
    public static string CrashLog => Path.Combine(AppContext.BaseDirectory, "imgzip-error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        // 无界面模式（CLI / 自测）优先，便于自动化验证与批量使用
        if (Cli.IsCliRequest(e.Args))
        {
            int code = Cli.Run(e.Args);
            Shutdown(code);
            return;
        }

        base.OnStartup(e);

        // GUI 模式的可选参数：
        //   ImgZip.exe 图片1.png 图片2.jpg        ← 资源管理器"打开方式"直接入队
        //   ImgZip.exe --add <文件/目录...> [--autorun]
        //   [--colors 2-256 | --gray | --bw] [--dither fs|bayer|none] [--format PNG]
        //   ↑ 后三项与命令行同名，用于启动时预设界面选项（也便于自动化截图/批处理）
        LaunchArgs.Preload.AddRange(ParsePreload(e.Args));
        LaunchArgs.AutoRun = e.Args.Contains("--autorun", StringComparer.OrdinalIgnoreCase);
        LaunchArgs.DepthTag = ParseDepth(e.Args);
        LaunchArgs.Dither = ParseDither(ArgValue(e.Args, "--dither"));
        LaunchArgs.FormatName = ArgValue(e.Args, "--format");

        // 任何未处理异常都写日志 + 提示，避免"双击没反应"
        DispatcherUnhandledException += (_, args) =>
        {
            Report(args.Exception);
            MessageBox.Show("程序遇到错误：\n\n" + args.Exception.Message +
                            "\n\n详情已写入：\n" + CrashLog, "图片压缩器", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Report(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Report(args.Exception); args.SetObserved(); };

        try
        {
            var win = new MainWindow();
            MainWindow = win;
            win.Show();
        }
        catch (Exception ex)
        {
            Report(ex);
            MessageBox.Show("启动失败：\n\n" + ex.Message + "\n\n详情已写入：\n" + CrashLog,
                "图片压缩器", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>把命令行里的文件/目录收集成待入队列表。</summary>
    private static IEnumerable<string> ParsePreload(string[] args)
    {
        var list = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--add", StringComparison.OrdinalIgnoreCase))
            {
                for (int j = i + 1; j < args.Length && !args[j].StartsWith("--"); j++) list.Add(args[j]);
            }
            else if (!args[i].StartsWith("--") && (File.Exists(args[i]) || Directory.Exists(args[i])))
            {
                list.Add(args[i]);
            }
        }
        return list;
    }

    /// <summary>取 <c>--名字 值</c> 形式参数的值（无则返回 null）。</summary>
    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    /// <summary>色深预设：--colors / --gray / --bw。</summary>
    private static int ParseDepth(string[] args)
    {
        if (args.Contains("--bw", StringComparer.OrdinalIgnoreCase)) return -2;
        if (args.Contains("--gray", StringComparer.OrdinalIgnoreCase)) return -1;
        return int.TryParse(ArgValue(args, "--colors"), out int n) && n is >= 2 and <= 256 ? n : int.MinValue;
    }

    private static DitherMode? ParseDither(string? mode) => mode?.ToLowerInvariant() switch
    {
        "fs" or "floyd" or "floydsteinberg" => DitherMode.FloydSteinberg,
        "bayer" or "ordered" => DitherMode.Ordered,
        "none" or "off" => DitherMode.None,
        _ => null,
    };

    private static void Report(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            File.AppendAllText(CrashLog,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n", System.Text.Encoding.UTF8);
        }
        catch { }
    }
}
