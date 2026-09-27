# 图片压缩器 · ImgZip

> **作者：Kurumiiau** · MIT License

一个 **Windows 原生桌面程序**，用于批量压缩图片：
支持各种常见与冷门格式、按 **1%–100% 倍率**、**自定义分辨率** 或 **色深（色位）** 压缩，拖拽即用，
默认输出到源文件同目录（也可自定义）。

![空状态](docs/screenshot-empty.png)

---

## 安装

下载 **`release/ImgZip-Setup-1.1.0.msi`**，双击安装即可。

- **用户级安装**，不需要管理员权限，装到 `%LOCALAPPDATA%\Programs\ImgZip`
- 自动创建**开始菜单**与**桌面**快捷方式
- 在「设置 → 应用 → 已安装的应用」中可正常卸载，卸载干净无残留

> **前置条件**：需要 **.NET 8 桌面运行时 (x64)**。
> 安装包会检测；若缺失会提示下载地址：<https://dotnet.microsoft.com/download/dotnet/8.0>（选 .NET Desktop Runtime 8 · x64）

---

## 功能

### 支持的格式

| | 格式 |
|---|---|
| **读取** | PNG · JPEG · BMP · GIF · TIFF · ICO · JPEG-XR(WDP) · **WebP** · **HEIC** · **AVIF** |
| **写出** | PNG · JPEG · BMP · GIF · TIFF |

读取走 Windows 成像组件（WIC），系统能解码的都能读；WebP/HEIC/AVIF 取决于是否安装了对应的系统图像扩展。
若原图是 WebP/HEIC 而选择"保持原格式"，程序会自动改存 PNG（含透明通道）或 JPEG，并在列表中注明。

### 三种压缩方式

**按倍率**

- 滑块 1% – 100%，附 100% / 75% / 50% / 25% / 10% 快捷胶囊

**按分辨率**

- 填目标宽 × 高，可**锁定宽高比**（按目标框内接缩放）或强制拉伸
- **只缩小，不放大**（默认开）

**按色深**

- 见下方「色深压缩」

通用：

- 缩小时使用**面积平均**重采样（等价精确区域平均，不糊不锯齿）；放大使用 Catmull-Rom 双三次；可选最近邻

### 色深压缩（按色位减色）

按**位深**而不是分辨率来压缩，对插画、图标、UI 截图、扫描件效果最明显。

| 档位 | 输出 |
|---|---|
| 256 / 128 / 64 / 32 色 | **8 位索引色**（1 字节/像素） |
| 16 / 8 色 | **4 位索引色** |
| 4 色 | **2 位索引色** |
| 2 色 | **1 位索引色** |
| 灰度（8 位） | 真灰度图（PNG `colorType=0`） |
| 黑白（1 位） | 双色调，**Otsu 自动阈值**（命令行可指定阈值） |

- 对 PNG / BMP 写出的是**真正的索引色文件**——PNG `colorType=3`（PLTE + 可选 tRNS，1/2/4/8 bpp）、
  BMP 1/4/8 bpp 带调色板，而不是"减色后仍然按 32 位存"的假压缩。
- 调色板：**中位切分（median-cut）+ k-means 迭代细化**。
- **抖动**：不抖动 / **Floyd–Steinberg** 误差扩散 / **有序抖动（Bayer 8×8）**，减到很低色数时用来保住层次。
- 对 JPEG / GIF / TIFF 等没有索引写出口的格式，会先把像素减色、再按目标格式编码。

实测（1600×1200 PNG，原 46.2 KB，同尺寸对比）：

| 输出 | 体积 |
|---|---|
| 256 色 8 位索引 | 30 KB |
| 16 色 4 位索引 | 11.7 KB |
| 2 色 1 位索引 | 897 B |
| 灰度 8 位 | 41.2 KB |
| 黑白 1 位 | 754 B |

索引色 BMP 约为**同尺寸 32 位 BMP 的 25%**（BMP 本身不压缩，所以要和 BMP 比而不是和 PNG 比）。

### 输出规则

- 默认**与源文件同目录**，文件名追加 `_compressed`
- 可切换**自定义目录**（多级目录自动创建）
- 重名**自动加序号**，绝不覆盖原图
- 可选 **结果更大时自动跳过**、**保留文件时间戳**

### 交互

- **拖拽导入**图片或整个文件夹（默认递归子目录）
- `选择图片` / `选择文件夹`、**Ctrl+V 粘贴剪贴板图片**、**Ctrl+O 打开**、**Esc 停止**
- 队列显示缩略图、原始尺寸/格式/大小、**预计输出尺寸**、进度条、完成后的"省 xx%"
- 底部实时汇总总体积与预计输出；完成后显示实际体积变化与耗时

---

## 命令行

同一个 exe 也支持批处理（无界面）：

```bat
:: 缩放 50%，输出到源目录
ImgZip.exe --cli --in "D:\照片" --scale 50

:: 压到 1920×1080 以内，转 JPEG 质量 80，输出到指定目录
ImgZip.exe --cli --in "D:\照片" --recursive --width 1920 --height 1080 --format JPEG --quality 80 --out "D:\输出"

:: 色深压缩：256 色 + Floyd–Steinberg 抖动
ImgZip.exe --cli --in "D:\插画" --scale 100 --format PNG --colors 256 --dither fs

:: 转黑白稿
ImgZip.exe --cli --in "D:\扫描件" --bw --threshold 160 --dither bayer

:: 列出本机可读写格式 / 运行引擎自测（63 项）
ImgZip.exe --formats
ImgZip.exe --selftest D:\temp\imgzip-test
```

参数：`--scale 1-100`｜`--width/--height`｜`--no-aspect`｜`--allow-upscale`｜`--mode area|bilinear|bicubic|nearest|auto`｜
`--colors 2-256`｜`--gray`｜`--bw`｜`--threshold 0-255`｜`--dither fs|bayer|none`｜
`--out 目录`｜`--suffix 文本`｜`--format PNG|JPEG|BMP|GIF|TIFF`｜`--quality 1-100`｜`--recursive`｜
`--skip-bigger`｜`--keep-time`｜`--json 报告.json`

GUI 也可接参数：`ImgZip.exe 图片1.png 图片2.jpg`（资源管理器"打开方式"直接入队）；
`--add <文件/目录> --autorun` 可启动即压缩，配合 `--colors / --gray / --bw / --dither / --format`
把界面选项一并预设好，适合批处理与自动化。

---

## 构建

### 构建程序

```powershell
# 需要 .NET 8 SDK
powershell -ExecutionPolicy Bypass -File build.ps1     # 输出到 release/payload/
```

### 构建安装包

```powershell
# 需要 .NET 8 SDK + WiX Toolset v5（MIT 许可）
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

---

## 项目结构

```
src/ImgZip/            应用源码（Core 引擎 / Ui 主题 / MainWindow）
  Core/Resampler.cs        面积平均 + 双三次重采样（预乘 Alpha，按行并行）
  Core/Compressor.cs       解码 → 缩放 → 减色 → 编码 管线
  Core/Quantizer.cs        中位切分 + k-means 调色板、抖动、灰度、Otsu 黑白
  Core/IndexedWriters.cs   索引色 PNG（1/2/4/8 bpp）与索引色 BMP 写出
  Core/ImageFormats.cs     格式表与目标格式选择
  Core/Cli.cs              命令行入口
  Core/SelfTest.cs         63 项引擎自测
  Ui/GlassTheme.xaml       液态玻璃主题（四色调色板 + 控件模板）
installer/              WiX 安装包定义与构建脚本
docs/                   截图
```

---

## 验证

`ImgZip.exe --selftest` 内置 **63 项**自动化检查，全部通过：

- 5 种格式解码（含 7×3 奇数尺寸、1×1）
- 重采样数值正确性：4×4→2×2 **必须等于精确区域平均**、4×4→1×1 等于全图均值、尺寸不变时像素不变
- 倍率边界 100% / 50% / 25% / 1%；7×3 缩到 1% 仍 ≥1×1
- 分辨率模式：锁比例、不锁比例、只缩不放、允许放大
- 输出规则：默认同目录、自定义多级目录、重名加序号、中文文件名与目录、保留时间戳
- 格式转换与有损质量单调性
- 透明通道：PNG 进出保留 Alpha；无编码器的格式自动退化；透明区转 JPEG 铺白底
- **色深**：写出的是**真索引色**（解析 PNG 块确认 `colorType=3` 与位深、解析 BMP 头确认 bpp 与调色板）；
  解码验证 4 位图颜色数 ≤16、灰度 R=G=B、黑白只有 0/255；位深越低体积越小；
  抖动后颜色数仍受控；索引 BMP 约为同尺寸 32 位 BMP 的 1/4
- 异常：损坏文件、不存在的文件都优雅失败
- **真实文件**：本机搜到的 `.webp / .ico / .gif / .tif / .bmp` 实际文件解码并压缩
- 性能：4000×3000 → 1000×750 约 0.2 秒

安装包也已实测：静默安装 → 文件/快捷方式/卸载入口齐全 → 从安装目录启动正常 → 静默卸载后无残留。

---

## 已知限制

1. **WebP / HEIC / AVIF 只能读不能写**（Windows 未向普通程序开放这些格式的编码接口）；
   这类原图选"保持原格式"时会自动改存 PNG（有透明）或 JPEG，并在列表注明。
2. HEIC/AVIF 的**读取**依赖系统已安装对应"图像扩展"；未安装时该文件会显示失败原因。
3. GIF 只取第一帧（不做动图逐帧压缩）。
4. 索引色写出只做**单帧、单色板**；2 色档写 1 位、4 色档写 2 位，BMP 不支持 2 位所以 4 色档仍写 4 位。
5. 色深压缩是**不可逆**的有损处理（256 色对照片可能有可见色带，建议配合抖动）。

---

## 许可

[MIT License](LICENSE) © 2026 **Kurumiiau**
