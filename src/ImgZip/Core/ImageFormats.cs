using System.IO;

namespace ImgZip.Core;

/// <summary>一种图片格式。</summary>
public sealed class ImageFormatInfo
{
    public required string Name { get; init; }
    public required string Extension { get; init; }
    public required string[] Extensions { get; init; }
    public bool CanEncode { get; init; }
    public bool Lossy { get; init; }
    public override string ToString() => Name;
}

/// <summary>
/// 格式表。
/// 解码全部交给 Windows 成像组件（WIC）：PNG / JPEG / BMP / GIF / TIFF / ICO / JPEG-XR 系统自带，
/// WebP、HEIF(HEIC)、AVIF 在安装了对应"图像扩展"的 Win10/11 上同样可读——
/// 因此"任意格式"是系统能力决定的，程序启动时会实测并把结果告诉用户。
/// 编码使用 WPF 内置的 5 个编码器（JPEG/PNG/BMP/GIF/TIFF）。
/// </summary>
public static class ImageFormats
{
    public static readonly ImageFormatInfo Png = new()
    { Name = "PNG", Extension = ".png", Extensions = new[] { ".png" }, CanEncode = true, Lossy = false };

    public static readonly ImageFormatInfo Jpeg = new()
    { Name = "JPEG", Extension = ".jpg", Extensions = new[] { ".jpg", ".jpeg", ".jpe", ".jfif" }, CanEncode = true, Lossy = true };

    public static readonly ImageFormatInfo Bmp = new()
    { Name = "BMP", Extension = ".bmp", Extensions = new[] { ".bmp", ".dib" }, CanEncode = true, Lossy = false };

    public static readonly ImageFormatInfo Gif = new()
    { Name = "GIF", Extension = ".gif", Extensions = new[] { ".gif" }, CanEncode = true, Lossy = false };

    public static readonly ImageFormatInfo Tiff = new()
    { Name = "TIFF", Extension = ".tif", Extensions = new[] { ".tif", ".tiff" }, CanEncode = true, Lossy = false };

    /// <summary>仅可读、无内置编码器的格式（可选择输出为 PNG/JPEG）。</summary>
    public static readonly ImageFormatInfo[] ReadOnlyFormats =
    {
        new() { Name = "WebP",  Extension = ".webp", Extensions = new[] { ".webp" }, CanEncode = false, Lossy = true },
        new() { Name = "HEIC",  Extension = ".heic", Extensions = new[] { ".heic", ".heif" }, CanEncode = false, Lossy = true },
        new() { Name = "AVIF",  Extension = ".avif", Extensions = new[] { ".avif" }, CanEncode = false, Lossy = true },
        new() { Name = "JPEG-XR", Extension = ".wdp", Extensions = new[] { ".wdp", ".jxr" }, CanEncode = false, Lossy = true },
        new() { Name = "ICO",   Extension = ".ico",  Extensions = new[] { ".ico" }, CanEncode = false, Lossy = false },
    };

    /// <summary>"保持原格式"时可选的编码目标（按优先级）。</summary>
    public static readonly ImageFormatInfo[] Encodable = { Png, Jpeg, Bmp, Gif, Tiff };

    public static IEnumerable<string> AllReadExtensions =>
        Encodable.SelectMany(f => f.Extensions).Concat(ReadOnlyFormats.SelectMany(f => f.Extensions)).Distinct();

    /// <summary>Windows 文件对话框用的过滤器。</summary>
    public static string DialogFilter
    {
        get
        {
            var all = string.Join(";", AllReadExtensions.Select(e => "*" + e));
            return $"所有支持的图片|{all}|PNG|*.png|JPEG|*.jpg;*.jpeg|WebP|*.webp|BMP|*.bmp|GIF|*.gif|TIFF|*.tif;*.tiff|所有文件|*.*";
        }
    }

    /// <summary>按扩展名匹配（用于"保持原格式"与输出文件名后缀）。</summary>
    public static ImageFormatInfo? ByPath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        foreach (var f in Encodable) if (f.Extensions.Contains(ext)) return f;
        foreach (var f in ReadOnlyFormats) if (f.Extensions.Contains(ext)) return f;
        return null;
    }

    public static ImageFormatInfo? ByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var f in Encodable) if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
        foreach (var f in ReadOnlyFormats) if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }

    public static bool IsSupportedExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return AllReadExtensions.Contains(ext);
    }

    /// <summary>
    /// "保持原格式"→ 目标编码器：
    /// 原格式可编码就用原格式；否则（WebP/HEIC/AVIF/ICO）按是否含透明通道选择 PNG 或 JPEG。
    /// </summary>
    public static ImageFormatInfo PickTarget(ImageFormatInfo? source, bool hasAlpha)
    {
        if (source is { CanEncode: true }) return source;
        return hasAlpha ? Png : Jpeg;
    }
}
