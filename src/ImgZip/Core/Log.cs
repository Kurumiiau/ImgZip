using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ImgZip.Core;

/// <summary>
/// 统一日志：同时写控制台（若已附加）与文件，便于 CLI 自动化验证。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _file;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    private const int ATTACH_PARENT_PROCESS = -1;

    public static bool Verbose { get; set; }

    /// <summary>让 WinExe 也能向调用它的控制台输出（否则 CLI 模式看不到任何东西）。</summary>
    public static void AttachToParentConsole()
    {
        if (!AttachConsole(ATTACH_PARENT_PROCESS))
            AllocConsole();
    }

    public static void InitFile(string path)
    {
        _file = path;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, "", Encoding.UTF8);
        }
        catch { /* 写不了日志不影响主流程 */ }
    }

    public static void Write(string msg)
    {
        lock (Gate)
        {
            try { Console.WriteLine(msg); } catch { /* 无控制台 */ }
            try { Console.Out.Flush(); } catch { }
            if (_file != null)
            {
                try { File.AppendAllText(_file, msg + Environment.NewLine, Encoding.UTF8); } catch { }
            }
            Debug.WriteLine(msg);
        }
    }

    public static void Info(string msg) => Write(msg);
    public static void Ok(string msg) => Write("  [OK]   " + msg);
    public static void Fail(string msg) => Write("  [FAIL] " + msg);
    public static void Detail(string msg) { if (Verbose) Write("         " + msg); }
}
