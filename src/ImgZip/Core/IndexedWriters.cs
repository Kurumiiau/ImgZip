using System;
using System.IO;
using System.IO.Compression;

namespace ImgZip.Core;

/// <summary>
/// 索引色 PNG / BMP 写出器，以及 8 位灰度 PNG。
/// 为什么自己写：WPF 的 PngBitmapEncoder / BmpBitmapEncoder 只能写 32 位真彩，
/// 量化出来的调色板根本落不到盘上，体积也就降不下来。
///  · PNG：1/2/4/8 位索引色（含 PLTE、可选 tRNS），zlib 用 DeflateStream 手工封装
///  · BMP：1/4/8 位索引色（含调色板）
///  · 灰度：8 位灰度 PNG（colorType 0）
/// </summary>
public static class IndexedWriters
{
    // ------------------------------------------------------------ PNG
    /// <summary>索引色 PNG（自动选择 1/2/4/8 位）</summary>
    public static void WriteIndexedPng(IndexedImage img, string path)
    {
        int bpp = img.BitsPerPixel;
        int n = img.Width * img.Height;
        int rowBytes = (img.Width * bpp + 7) / 8;

        // 原始扫描线：每行前面加一个 filter 字节 0
        var raw = new byte[(rowBytes + 1) * img.Height];
        int o = 0;
        for (int y = 0; y < img.Height; y++)
        {
            raw[o++] = 0;
            int bitPos = 0, cur = 0;
            for (int x = 0; x < img.Width; x++)
            {
                int v = img.Indices[y * img.Width + x];
                cur = (cur << bpp) | (v & ((1 << bpp) - 1));
                bitPos += bpp;
                if (bitPos == 8) { raw[o++] = (byte)cur; bitPos = 0; cur = 0; }
            }
            if (bitPos > 0) raw[o++] = (byte)(cur << (8 - bitPos));
        }
        if (o != raw.Length) throw new InvalidOperationException("PNG 扫描线长度计算错误");

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

        // IHDR: width, height, bitDepth, colorType=3(索引), compression, filter, interlace
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, img.Width);
        WriteBE(ihdr, 4, img.Height);
        ihdr[8] = (byte)bpp;
        ihdr[9] = 3;
        WriteChunk(fs, "IHDR", ihdr);

        // PLTE
        var plte = new byte[img.Count * 3];
        for (int i = 0; i < img.Count; i++)
        {
            plte[i * 3] = img.PaletteR[i];
            plte[i * 3 + 1] = img.PaletteG[i];
            plte[i * 3 + 2] = img.PaletteB[i];
        }
        WriteChunk(fs, "PLTE", plte);

        // tRNS（调色板透明度）
        if (img.HasAlpha)
        {
            var trns = new byte[img.Count];
            for (int i = 0; i < img.Count; i++) trns[i] = img.PaletteA![i];
            WriteChunk(fs, "tRNS", trns);
        }

        WriteChunk(fs, "IDAT", ZlibCompress(raw));
        WriteChunk(fs, "IEND", Array.Empty<byte>());
    }

    /// <summary>8 位灰度 PNG（colorType 0）</summary>
    public static void WriteGrayPng(byte[] gray, int w, int h, string path)
    {
        var raw = new byte[(w + 1) * h];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0;
            Buffer.BlockCopy(gray, y * w, raw, o, w);
            o += w;
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, w);
        WriteBE(ihdr, 4, h);
        ihdr[8] = 8;
        ihdr[9] = 0;      // 灰度
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(fs, "IHDR", ihdr);
        WriteChunk(fs, "IDAT", ZlibCompress(raw));
        WriteChunk(fs, "IEND", Array.Empty<byte>());
    }

    // ------------------------------------------------------------ BMP
    /// <summary>索引色 BMP（1/4/8 位，自带调色板）</summary>
    public static void WriteIndexedBmp(IndexedImage img, string path)
    {
        int bpp = img.BitsPerPixel;                 // 1/2/4/8，BMP 不支持 2 位 → 用 4 位表示
        if (bpp == 2) bpp = 4;
        int paletteCount = img.Count;
        int rowBytes = ((img.Width * bpp + 31) / 32) * 4;
        int pixelBytes = rowBytes * img.Height;
        int paletteBytes = paletteCount * 4;
        int offset = 14 + 40 + paletteBytes;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // BITMAPFILEHEADER
        bw.Write((byte)'B'); bw.Write((byte)'M');
        bw.Write(offset + pixelBytes);
        bw.Write((short)0); bw.Write((short)0);
        bw.Write(offset);

        // BITMAPINFOHEADER
        bw.Write(40);
        bw.Write(img.Width);
        bw.Write(img.Height);
        bw.Write((short)1);
        bw.Write((short)bpp);
        bw.Write(0);                       // 无压缩
        bw.Write(pixelBytes);
        bw.Write(2835); bw.Write(2835);    // 72 DPI
        bw.Write(paletteCount);
        bw.Write(0);

        // 调色板（BGRA 四元组）
        for (int i = 0; i < paletteCount; i++)
        {
            bw.Write(img.PaletteB[i]);
            bw.Write(img.PaletteG[i]);
            bw.Write(img.PaletteR[i]);
            bw.Write((byte)0);
        }

        // 像素（自下而上，每行 4 字节对齐）
        var row = new byte[rowBytes];
        for (int y = img.Height - 1; y >= 0; y--)
        {
            Array.Clear(row);
            if (bpp == 8)
            {
                for (int x = 0; x < img.Width; x++) row[x] = img.Indices[y * img.Width + x];
            }
            else
            {
                int perByte = 8 / bpp;
                int bitPos = 8 - bpp;
                for (int x = 0; x < img.Width; x++)
                {
                    int bi = x / perByte;
                    row[bi] |= (byte)((img.Indices[y * img.Width + x] & ((1 << bpp) - 1)) << bitPos);
                    bitPos -= bpp;
                    if (bitPos < 0) bitPos = 8 - bpp;
                }
            }
            bw.Write(row);
        }
    }

    // ------------------------------------------------------ PNG 基础设施
    private static void WriteBE(byte[] b, int off, int v)
    {
        b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16); b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, data.Length);
        s.Write(len, 0, 4);
        var t = new byte[4];
        for (int i = 0; i < 4; i++) t[i] = (byte)type[i];
        s.Write(t, 0, 4);
        s.Write(data, 0, data.Length);

        // PNG 的 CRC 覆盖「类型 + 数据」，标准 CRC-32（初值/终值均取反）
        uint crc = 0xFFFFFFFF;
        crc = UpdateCrc(crc, t, t.Length);
        crc = UpdateCrc(crc, data, data.Length);
        crc ^= 0xFFFFFFFF;
        var c = new byte[4];
        WriteBE(c, 0, unchecked((int)crc));
        s.Write(c, 0, 4);
    }

    private static uint UpdateCrc(uint crc, byte[] data, int len)
    {
        for (int i = 0; i < len; i++)
        {
            crc ^= data[i];
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc;
    }

    /// <summary>zlib 封装：2 字节头 + raw deflate + big-endian adler32</summary>
    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78);   // CMF: deflate, 32K window
        ms.WriteByte(0x9C);   // FLG: 默认压缩级别
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(data, 0, data.Length);
        uint a = 1, b = 0;
        foreach (byte v in data)
        {
            a = (a + v) % 65521;
            b = (b + a) % 65521;
        }
        uint adler = (b << 16) | a;
        ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
        return ms.ToArray();
    }
}
