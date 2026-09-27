using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ImgZip.Core;

namespace ImgZip;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<JobItem> _jobs = new();
    private CancellationTokenSource? _cts;
    private bool _running;
    private string? _lastOutputDir;

    public MainWindow()
    {
        InitializeComponent();

        // 窗口/任务栏图标（exe 图标由 csproj 的 ApplicationIcon 提供）
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/app.ico")); }
        catch { /* 图标缺失不影响运行 */ }

        Queue.ItemsSource = _jobs;
        _jobs.CollectionChanged += (_, _) => { UpdateSummary(); UpdateEmptyState(); };

        InitFormats();
        Loaded += OnLoaded;
        PreviewKeyDown += OnKeyDown;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 适配小屏：窗口不超过工作区
        var wa = SystemParameters.WorkArea;
        if (Height > wa.Height - 40) Height = Math.Max(600, wa.Height - 40);
        if (Width > wa.Width - 40) Width = Math.Max(900, wa.Width - 40);

        FadeIn();
        RefreshPreviews();
        Queue.AllowDrop = true;
        Queue.Drop += OnDrop;

        // 命令行预载（也用于自动化截图/无人值守）
        if (LaunchArgs.Preload.Count > 0)
        {
            AddPaths(LaunchArgs.Preload, autoRunAfter: LaunchArgs.AutoRun);
        }
    }

    private void FadeIn()
    {
        RootGrid.Opacity = 0;
        var sb = new Storyboard();
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, RootGrid);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        sb.Children.Add(fade);
        sb.Begin();
    }

    // ───────────────────────────── 初始化 ─────────────────────────────
    private void InitFormats()
    {
        FormatBox.Items.Add(new ComboBoxItem { Content = "保持原格式" });
        foreach (var f in ImageFormats.Encodable)
            FormatBox.Items.Add(new ComboBoxItem { Content = f.Name });
        FormatBox.SelectedIndex = 0;

        var extra = Cli.DetectedExtraFormats();
        DropSub.Text = "支持 PNG · JPEG · BMP · GIF · TIFF · ICO" +
                       (extra.Count > 0 ? " · " + string.Join(" · ", extra) : "") + " 等格式";
    }

    // ─────────────────────────── 窗口交互 ───────────────────────────
    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) { try { DragMove(); } catch { } }
    }
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _running) OnCancel(sender, e);
        else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control) OnPickFiles(sender, e);
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control) PasteFromClipboard();
    }

    // ───────────────────────────── 导入 ─────────────────────────────
    private void OnPickFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择图片",
            Multiselect = true,
            Filter = ImageFormats.DialogFilter,
        };
        if (dlg.ShowDialog(this) == true) AddPaths(dlg.FileNames);
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择包含图片的文件夹" };
        if (dlg.ShowDialog(this) == true) AddPaths(new[] { dlg.FolderName });
    }

    private void PasteFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null)
                {
                    var dir = Path.Combine(Path.GetTempPath(), "ImgZip");
                    Directory.CreateDirectory(dir);
                    var p = Path.Combine(dir, $"剪贴板_{DateTime.Now:HHmmss}.png");
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                    using (var fs = File.Create(p)) enc.Save(fs);
                    AddPaths(new[] { p });
                    ShowToast("已从剪贴板粘贴一张图片");
                }
            }
        }
        catch { /* 剪贴板不可用时忽略 */ }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (ok) SetDropActive(true);
    }

    private void OnDragLeave(object sender, DragEventArgs e) => SetDropActive(false);

    private void OnDrop(object sender, DragEventArgs e)
    {
        SetDropActive(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
    }

    private void SetDropActive(bool active)
    {
        DropDash.BorderBrush = active ? FindBrush("B.Rose") : new SolidColorBrush(Color.FromArgb(0x66, 0xEB, 0x9F, 0xAA));
        DropDash.BorderThickness = new Thickness(active ? 2 : 1.5);
        DropTitle.Text = active ? "松手即可加入队列" : "把图片拖到这里";
        var target = active ? 1.03 : 1.0;
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var scale = DropIconWrap.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        DropIconWrap.RenderTransformOrigin = new Point(0.5, 0.5);
        DropIconWrap.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private Brush FindBrush(string key) => (Brush)FindResource(key);

    private void AddPaths(IEnumerable<string> paths, bool autoRunAfter = false)
    {
        bool recursive = ChkRecursive.IsChecked == true;
        var files = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p))
                {
                    files.AddRange(Directory.EnumerateFiles(p, "*.*",
                        recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                        .Where(ImageFormats.IsSupportedExtension));
                }
                else if (File.Exists(p) && ImageFormats.IsSupportedExtension(p))
                {
                    files.Add(p);
                }
                else if (File.Exists(p))
                {
                    // 扩展名不在白名单里也尝试一下（可能是不常见的变体）
                    files.Add(p);
                }
            }
            catch { }
        }

        files = files.Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(f => !_jobs.Any(j => string.Equals(j.SourcePath, f, StringComparison.OrdinalIgnoreCase)))
                     .ToList();
        if (files.Count == 0) return;

        // 元数据/缩略图读取放在后台，避免卡界面
        Task.Run(() =>
        {
            var made = new List<JobItem>();
            foreach (var f in files) made.Add(new JobItem(f));
            Dispatcher.Invoke(() =>
            {
                foreach (var j in made) _jobs.Add(j);
                RefreshPreviews();
                UpdateSummary();
                ShowToast($"已加入 {made.Count} 张图片");
                if (autoRunAfter && _jobs.Count > 0) OnRun(this, new RoutedEventArgs());
            });
        });
    }

    private void OnRemoveItem(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is JobItem j && !_running) _jobs.Remove(j);
    }

    private void OnClearQueue(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _jobs.Clear();
        _lastOutputDir = null;
        BtnOpenOut.Visibility = Visibility.Collapsed;
    }

    // ───────────────────────────── 设置 ─────────────────────────────
    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        bool scale = ModeScale.IsChecked == true;
        if (PanelScale == null || PanelRes == null) return;   // 初始化期
        PanelScale.Visibility = scale ? Visibility.Visible : Visibility.Collapsed;
        PanelRes.Visibility = scale ? Visibility.Collapsed : Visibility.Visible;
        RefreshPreviews();
    }

    private void OnScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScaleValue != null) ScaleValue.Text = $"{e.NewValue:0}%";
        RefreshPreviews();
    }

    private void OnScaleChip(object sender, RoutedEventArgs e)
    {
        if ((sender as RadioButton)?.Tag is string s && double.TryParse(s, out var v))
            ScaleSlider.Value = v;
    }

    private void OnResChanged(object sender, RoutedEventArgs e) => RefreshPreviews();
    private void OnResChip(object sender, RoutedEventArgs e)
    {
        if ((sender as RadioButton)?.Tag is not string s) return;
        var parts = s.Split(',');
        if (parts.Length == 2) { ResW.Text = parts[0]; ResH.Text = parts[1]; }
    }

    private void OnOutChanged(object sender, RoutedEventArgs e)
    {
        if (OutPathText == null) return;
        bool custom = OutCustom.IsChecked == true;
        OutPathText.Foreground = custom && string.IsNullOrEmpty(_customDir)
            ? FindBrush("B.RoseDeep") : FindBrush("B.InkFaint");
        if (custom && string.IsNullOrEmpty(_customDir)) OutPathText.Text = "（请选择输出目录）";
    }

    private string? _customDir;

    private void OnPickOutputDir(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择输出目录" };
        if (dlg.ShowDialog(this) == true)
        {
            _customDir = dlg.FolderName;
            OutPathText.Text = _customDir;
            OutPathText.Foreground = FindBrush("B.InkSoft");
            OutCustom.IsChecked = true;
        }
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        bool lossy = SelectedFormat() is "JPEG";
        if (QualitySlider != null) QualitySlider.IsEnabled = lossy;
        if (QualityLabel != null) QualityLabel.Opacity = lossy ? 1 : 0.45;
        if (QualityValue != null) QualityValue.Opacity = lossy ? 1 : 0.45;
        RefreshPreviews();
    }

    private void OnQualityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityValue != null) QualityValue.Text = $"{e.NewValue:0}";
    }

    private void OnOptionsChanged(object sender, RoutedEventArgs e) => RefreshPreviews();

    private string? SelectedFormat()
    {
        if (FormatBox?.SelectedItem is not ComboBoxItem it) return null;
        var s = it.Content?.ToString();
        return string.IsNullOrEmpty(s) || s == "保持原格式" ? null : s;
    }

    private CompressOptions CurrentOptions() => new()
    {
        UseResolution = ModeRes.IsChecked == true,
        ScalePercent = ScaleSlider.Value,
        TargetWidth = ParseInt(ResW.Text, 1920),
        TargetHeight = ParseInt(ResH.Text, 1080),
        KeepAspect = LockAspect.IsChecked == true,
        ShrinkOnly = ChkShrinkOnly.IsChecked == true,
        FormatName = SelectedFormat(),
        Quality = (int)QualitySlider.Value,
    };

    private static int ParseInt(string? s, int fallback)
        => int.TryParse((s ?? "").Trim(), out var v) && v > 0 ? Math.Min(v, 20000) : fallback;

    private void RefreshPreviews()
    {
        if (_jobs.Count == 0) return;
        var opt = CurrentOptions();
        foreach (var j in _jobs) j.RefreshPreview(opt);
        UpdateSummary();
    }

    // ───────────────────────────── 压缩 ─────────────────────────────
    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (_running || _jobs.Count == 0) return;

        var opt = CurrentOptions();
        bool custom = OutCustom.IsChecked == true;
        if (custom && string.IsNullOrEmpty(_customDir))
        {
            ShowToast("请先选择自定义输出目录");
            return;
        }
        string? outDir = custom ? _customDir : null;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _running = true;
        BtnRun.IsEnabled = false;
        BtnRun.Content = "压缩中…";
        BtnCancel.Visibility = Visibility.Visible;
        BtnOpenOut.Visibility = Visibility.Collapsed;

        foreach (var j in _jobs) { j.Progress = 0; j.Status = JobStatus.Pending; j.StatusText = "等待"; j.Result = null; }

        var total = _jobs.Count;
        int done = 0, ok = 0, skipped = 0, failed = 0;
        long before = 0, after = 0;
        var sw = Stopwatch.StartNew();

        var items = _jobs.ToList();
        var sem = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount));
        var progress = new Progress<Action>(a => a());

        // 重要：所有 UI 控件的值必须在 UI 线程上先取出来。
        // 若在 Task.Run 的 lambda 里直接读 ChkSkipBigger.IsChecked，会在工作线程上
        // 访问 DependencyObject，抛"调用线程无法访问此对象"。
        bool skipBigger = ChkSkipBigger.IsChecked == true;
        bool keepTime = ChkKeepTime.IsChecked == true;
        var optLocal = opt;
        var outDirLocal = outDir;

        try
        {
            var tasks = items.Select(async item =>
            {
                await sem.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    ((IProgress<Action>)progress).Report(() =>
                    {
                        item.Status = JobStatus.Working;
                        item.StatusText = "压缩中";
                        item.Progress = 20;
                    });

                    var res = await Task.Run(() => Compressor.Compress(
                        item.SourcePath, optLocal, outDirLocal, "_compressed",
                        skipBigger, keepTime), token).ConfigureAwait(false);

                    ((IProgress<Action>)progress).Report(() =>
                    {
                        item.Result = res;
                        item.Progress = 100;
                        if (!res.Success)
                        {
                            item.Status = JobStatus.Failed;
                            item.StatusText = "失败";
                            item.Detail = res.Error ?? "未知错误";
                            failed++;
                        }
                        else if (res.Skipped)
                        {
                            item.Status = JobStatus.Skipped;
                            item.StatusText = "已跳过";
                            item.Detail = res.Note ?? "结果更大";
                            skipped++;
                        }
                        else
                        {
                            item.Status = JobStatus.Done;
                            item.StatusText = res.SavedRatio >= 0 ? $"省 {res.SavedRatio * 100:0.#}%" : "已完成";
                            item.Detail = $"{res.Width}×{res.Height} · {res.TargetFormat} · {JobItemFormat.Size(res.OutputBytes)}";
                            ok++;
                            before += res.SourceBytes;
                            after += res.OutputBytes;
                            if (_lastOutputDir == null && res.OutputPath != null)
                                _lastOutputDir = Path.GetDirectoryName(res.OutputPath);
                        }
                        done++;
                        BtnRun.Content = $"压缩中… {done}/{total}";
                        SumDetail.Text = $"已完成 {done}/{total}";
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    ((IProgress<Action>)progress).Report(() =>
                    {
                        item.Status = JobStatus.Failed;
                        item.StatusText = "失败";
                        item.Detail = ex.Message;
                        failed++; done++;
                    });
                }
                finally { sem.Release(); }
            }).ToList();

            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }

        sw.Stop();
        _running = false;
        BtnRun.IsEnabled = true;
        BtnRun.Content = "开始压缩";
        BtnCancel.Visibility = Visibility.Collapsed;

        if (ok > 0)
        {
            BtnOpenOut.Visibility = Visibility.Visible;
            var saved = before > 0 ? (1 - (double)after / before) * 100 : 0;
            SumTitle.Text = $"完成 {ok} 张"
                + (skipped > 0 ? $" · 跳过 {skipped}" : "")
                + (failed > 0 ? $" · 失败 {failed}" : "");
            SumDetail.Text = $"{JobItemFormat.Size(before)} → {JobItemFormat.Size(after)}（省 {saved:0.#}%）· 用时 {sw.Elapsed.TotalSeconds:0.0}s";
            ShowToast($"压缩完成：{JobItemFormat.Size(before)} → {JobItemFormat.Size(after)}，省 {saved:0.#}%");
        }
        else if (failed > 0) ShowToast($"全部失败（{failed} 张），请查看列表中的错误信息");
        else if (skipped > 0) ShowToast("所有图片的结果都更大，已按设置跳过");

        // 注意：这里不要再调用 UpdateSummary()，否则会覆盖上面的"完成"统计
        BtnRun.IsEnabled = _jobs.Count > 0;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        ShowToast("正在停止…");
    }

    private void OnOpenOutput(object sender, RoutedEventArgs e)
    {
        var dir = _lastOutputDir ?? _customDir;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { ShowToast("还没有输出目录"); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { ShowToast("打开失败：" + ex.Message); }
    }

    // ───────────────────────────── 状态显示 ─────────────────────────────
    private void UpdateSummary()
    {
        QueueCount.Text = _jobs.Count.ToString();
        if (_jobs.Count == 0)
        {
            SumTitle.Text = "总计";
            SumDetail.Text = "等待添加图片";
            BtnRun.IsEnabled = false;
            return;
        }
        long total = _jobs.Sum(j => j.SourceBytes);
        SumTitle.Text = $"共 {_jobs.Count} 张 · {JobItemFormat.Size(total)}";
        var opt = CurrentOptions();
        long estimate = 0;
        foreach (var j in _jobs)
        {
            var (w, h) = opt.ComputeSize(Math.Max(1, j.SourceWidth), Math.Max(1, j.SourceHeight));
            double ratio = (double)(w * (long)h) / Math.Max(1, (long)j.SourceWidth * j.SourceHeight);
            estimate += (long)(j.SourceBytes * ratio * (opt.FormatName == "PNG" ? 0.75 : 0.35));
        }
        SumDetail.Text = $"预计输出约 {JobItemFormat.Size(estimate)}（实际以压缩结果为准）";
        BtnRun.IsEnabled = !_running;
    }

    private void UpdateEmptyState()
    {
        bool empty = _jobs.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Queue.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        // 队列有内容时把拖拽区收成一行（高度由内容自适应，不做定高动画）
        DropSub.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DropIconWrap.Width = DropIconWrap.Height = empty ? 52 : 36;
        DropIconWrap.CornerRadius = new CornerRadius(empty ? 17 : 12);
        DropContent.Margin = empty ? new Thickness(26, 20, 26, 20) : new Thickness(20, 12, 20, 12);
        DropTitle.Margin = empty ? new Thickness(0, 8, 0, 0) : new Thickness(10, 0, 0, 0);
        DropTitle.FontSize = empty ? 14 : 13;

        // 紧凑模式：图标与文字排成一行，按钮在右侧
        if (empty)
        {
            DropContent.Orientation = Orientation.Vertical;
            DropButtons.HorizontalAlignment = HorizontalAlignment.Center;
            DropButtons.Margin = new Thickness(0, 12, 0, 0);
        }
        else
        {
            DropContent.Orientation = Orientation.Horizontal;
            DropButtons.HorizontalAlignment = HorizontalAlignment.Right;
            DropButtons.Margin = new Thickness(18, 0, 0, 0);
        }
        DropContent.VerticalAlignment = VerticalAlignment.Center;
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        var sb = new Storyboard();
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        var hold = new DoubleAnimation(1, 1, TimeSpan.FromMilliseconds(2200));
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(420));
        Storyboard.SetTarget(fadeIn, Toast); Storyboard.SetTargetProperty(fadeIn, new PropertyPath(OpacityProperty));
        Storyboard.SetTarget(hold, Toast); Storyboard.SetTargetProperty(hold, new PropertyPath(OpacityProperty));
        Storyboard.SetTarget(fadeOut, Toast); Storyboard.SetTargetProperty(fadeOut, new PropertyPath(OpacityProperty));
        hold.BeginTime = TimeSpan.FromMilliseconds(200);
        fadeOut.BeginTime = TimeSpan.FromMilliseconds(2400);
        sb.Children.Add(fadeIn); sb.Children.Add(hold); sb.Children.Add(fadeOut);
        sb.Begin();
    }
}
