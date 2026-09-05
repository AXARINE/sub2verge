// Program.cs — 系统托盘应用（WPF，纯代码，无 XAML 文件）
// 左键更新订阅，右键弹现代风格菜单；托盘图标 Segoe Fluent Icons + 状态色渐变圆角底
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using Sub2Clash.Core;
using Icon = System.Drawing.Icon;
using DrawingBrush = System.Drawing.Brush;
using DrawingBrushes = System.Drawing.Brushes;
using DrawingColor = System.Drawing.Color;
using DrawingFontFamily = System.Drawing.FontFamily;
using DrawingLinearGradientBrush = System.Drawing.Drawing2D.LinearGradientBrush;
using DrawingPen = System.Drawing.Pen;
using MediaBrush = System.Windows.Media.Brush;
using MediaFontFamily = System.Windows.Media.FontFamily;
using WpfImage = System.Windows.Controls.Image;

namespace Sub2Clash;

static class Program
{
    public const string AppName = "sub2clash";
    const string AutostartKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [STAThread]
    static void Main(string[] args)
    {
        // 一次性更新模式（供计划任务/脚本/调试使用）
        if (args.Contains("--once"))
        {
            var r = new UpdateService().Run(Console.WriteLine);
            Console.WriteLine(r.Summary);
            Console.Out.Flush();
            Environment.Exit(r.Ok ? 0 : 1);
        }

        // 图标预览模式：把各状态图标渲染成 PNG 便于调试
        if (args.Length >= 2 && args[0] == "--icon-preview")
        {
            RenderIconPreview(args[1]);
            return;
        }

        // 单实例：避免开机自启和手动启动重复运行
        using var mutex = new Mutex(true, @"Local\sub2clash-tray", out var createdNew);
        if (!createdNew) return;

        new App().Run();
    }

    public static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutostartKey);
        return key?.GetValue(AppName) is string s && s.Length > 0;
    }

    public static void SetAutostart(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AutostartKey);
        if (enable) key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(AppName, false);
    }

    static void RenderIconPreview(string path)
    {
        var idle = DrawingColor.FromArgb(33, 150, 243);
        var items = new List<(string name, object? frame, int size)>
        {
            ("Rocket", IconConcept.Rocket, 32),
            ("Plane", IconConcept.Plane, 32),
            ("Globe", IconConcept.Globe, 32),
            ("Nodes", IconConcept.Nodes, 32),
            ("Bolt", IconConcept.Bolt, 32),
            ("R-16", IconConcept.Rocket, 16),
            ("R-24", IconConcept.Rocket, 24),
            ("R-64", IconConcept.Rocket, 64),
            ("run", IconConcept.Rocket, 32),
            ("ok", IconConcept.Rocket, 32),
            ("err", IconConcept.Rocket, 32),
            ("mRefresh", IconKind.Refresh, 18),
            ("mPower", IconKind.Power, 18),
            ("mFolder", IconKind.Folder, 18),
            ("mInfo", IconKind.Info, 18),
            ("mCancel", IconKind.Cancel, 18),
        };
        var sheet = new Bitmap(items.Count * 72 + 8, 96);
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(DrawingColor.White);
            var font = new Font("Segoe UI", 8);
            for (int i = 0; i < items.Count; i++)
            {
                var (name, frame, size) = items[i];
                int x = 8 + i * 72;
                int y = 8 + (48 - size) / 2 - 4;
                using var img = frame is IconConcept concept
                    ? IconRenderer.DrawFrame(size,
                        name == "run" ? DrawingColor.FromArgb(255, 152, 0)
                        : name == "ok" ? DrawingColor.FromArgb(76, 175, 80)
                        : name == "err" ? DrawingColor.FromArgb(244, 67, 54)
                        : idle, concept)
                    : IconRenderer.MenuIcon((IconKind)frame!, size);
                g.DrawImage(img, x, y);
                g.DrawString(name, font, DrawingBrushes.Gray, x + 4, 66);
            }
        }
        sheet.Save(path, ImageFormat.Png);
        Console.WriteLine($"preview saved: {path}");
    }
}

// 菜单图标：矢量绘制（不用字体字形，避免系统字体缺失时回退到日文字库乱码）
enum IconKind { Refresh, Power, Folder, Info, Cancel }

enum IconConcept { Rocket, Plane, Globe, Nodes, Bolt }

static class IconRenderer
{
    // 矢量图形：全部用路径画，任何尺寸都锐利（不用字体字形，细线字形缩到 16px 会糊）
    static readonly (float x, float y)[] BoltPts =
    [
        (0.62f, 0.03f), (0.16f, 0.55f), (0.44f, 0.55f),
        (0.34f, 0.97f), (0.86f, 0.42f), (0.58f, 0.42f),
    ];

    static readonly (float x, float y)[] RocketPts =
    [
        (0.50f, 0.02f), (0.78f, 0.32f), (0.68f, 0.72f), (0.85f, 0.94f),
        (0.54f, 0.85f), (0.46f, 0.85f), (0.15f, 0.94f), (0.32f, 0.72f), (0.22f, 0.32f),
    ];

    static readonly (float x, float y)[] PlanePts =
    [
        (0.04f, 0.38f), (0.96f, 0.04f), (0.62f, 0.94f), (0.42f, 0.62f),
    ];

    static GraphicsPath PolyPath((float x, float y)[] pts, RectangleF box)
    {
        var p = new GraphicsPath();
        p.AddPolygon(pts.Select(b => new PointF(box.X + b.x * box.Width, box.Y + b.y * box.Height)).ToArray());
        return p;
    }

    public static Bitmap DrawFrame(int size, DrawingColor c, IconConcept concept = IconConcept.Rocket)
    {
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float m = size / 16f;
            var rect = new RectangleF(m, m, size - 2 * m, size - 2 * m);
            using var bg = RoundedRectF(rect, size * 0.26f);
            using var brush = new DrawingLinearGradientBrush(rect, Lighten(c, 0.28f), Darken(c, 0.16f), 45f);
            g.FillPath(brush, bg);

            var box = new RectangleF(rect.X + rect.Width * 0.20f, rect.Y + rect.Height * 0.14f,
                                     rect.Width * 0.60f, rect.Height * 0.72f);
            using var white = new SolidBrush(DrawingColor.White);
            switch (concept)
            {
                case IconConcept.Rocket:
                    g.FillPath(white, PolyPath(RocketPts, box));
                    // 舷窗（用背景色抠出圆点，不需要额外颜色）
                    float wr = size * 0.095f;
                    using (var win = new SolidBrush(Darken(c, 0.12f)))
                        g.FillEllipse(win, box.X + box.Width * 0.5f - wr,
                                      box.Y + box.Height * 0.28f - wr, wr * 2, wr * 2);
                    break;
                case IconConcept.Plane:
                    g.FillPath(white, PolyPath(PlanePts, box));
                    break;
                case IconConcept.Bolt:
                    g.FillPath(white, PolyPath(BoltPts, box));
                    break;
                case IconConcept.Globe:
                    using (var pen = new DrawingPen(DrawingColor.White, Math.Max(1.2f, size * 0.07f)))
                    {
                        g.DrawEllipse(pen, box.X, box.Y, box.Width, box.Height);
                        g.DrawEllipse(pen, box.X + box.Width * 0.38f, box.Y, box.Width * 0.24f, box.Height);
                        g.DrawArc(pen, box.X - box.Width * 0.18f, box.Y + box.Height * 0.30f, box.Width * 1.36f, box.Height * 0.40f, 0, 180);
                        g.DrawArc(pen, box.X - box.Width * 0.18f, box.Y + box.Height * 0.30f, box.Width * 1.36f, box.Height * 0.40f, 180, 180);
                    }
                    break;
                case IconConcept.Nodes:
                    using (var pen = new DrawingPen(DrawingColor.White, Math.Max(1.2f, size * 0.06f)))
                    {
                        float r = size * 0.085f;
                        var c1 = new PointF(box.X + box.Width * 0.50f, box.Y + box.Height * 0.05f);
                        var c2 = new PointF(box.X + box.Width * 0.08f, box.Y + box.Height * 0.92f);
                        var c3 = new PointF(box.X + box.Width * 0.92f, box.Y + box.Height * 0.92f);
                        g.DrawLine(pen, c1, c2);
                        g.DrawLine(pen, c1, c3);
                        g.DrawLine(pen, c2, c3);
                        foreach (var p in new[] { c1, c2, c3 })
                            g.FillEllipse(white, p.X - r, p.Y - r, r * 2, r * 2);
                    }
                    break;
            }
        }
        return bmp;
    }

    public static Bitmap MenuIcon(IconKind kind, int size = 18, DrawingColor? color = null)
    {
        var c = color ?? DrawingColor.FromArgb(90, 90, 90);
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new DrawingPen(c, Math.Max(1.5f, size * 0.12f));
            using var brush = new SolidBrush(c);
            switch (kind)
            {
                case IconKind.Cancel:
                    g.DrawLine(pen, size * 0.22f, size * 0.22f, size * 0.78f, size * 0.78f);
                    g.DrawLine(pen, size * 0.78f, size * 0.22f, size * 0.22f, size * 0.78f);
                    break;
                case IconKind.Power:
                    float pr = size * 0.36f;
                    g.DrawArc(pen, size * 0.5f - pr, size * 0.5f - pr, pr * 2, pr * 2, 110, 320);
                    g.DrawLine(pen, size * 0.5f, size * 0.15f, size * 0.5f, size * 0.55f);
                    break;
                case IconKind.Info:
                    g.DrawEllipse(pen, size * 0.18f, size * 0.18f, size * 0.64f, size * 0.64f);
                    g.DrawLine(pen, size * 0.5f, size * 0.40f, size * 0.5f, size * 0.70f);
                    g.FillEllipse(brush, size * 0.46f, size * 0.24f, size * 0.08f, size * 0.08f);
                    break;
                case IconKind.Folder:
                    g.FillPolygon(brush,
                    [
                        new PointF(size * 0.08f, size * 0.26f), new PointF(size * 0.42f, size * 0.26f),
                        new PointF(size * 0.54f, size * 0.38f), new PointF(size * 0.92f, size * 0.38f),
                        new PointF(size * 0.92f, size * 0.74f), new PointF(size * 0.08f, size * 0.74f),
                    ]);
                    break;
                case IconKind.Refresh:
                    float rr = size * 0.34f;
                    g.DrawArc(pen, size * 0.5f - rr, size * 0.5f - rr, rr * 2, rr * 2, 200, 285);
                    // 箭头头
                    float a = 205f * MathF.PI / 180f; // 弧线端点(80°)切线方向
                    var end = new PointF(size * 0.5f + rr * MathF.Cos(a), size * 0.5f + rr * MathF.Sin(a));
                    float hw = size * 0.16f;
                    var dir = new PointF(-MathF.Sin(a), MathF.Cos(a)); // 顺时针切线
                    var perp = new PointF(-dir.Y, dir.X);
                    g.FillPolygon(brush,
                    [
                        new PointF(end.X + dir.X * hw * 1.2f, end.Y + dir.Y * hw * 1.2f),
                        new PointF(end.X + perp.X * hw, end.Y + perp.Y * hw),
                        new PointF(end.X - perp.X * hw, end.Y - perp.Y * hw),
                    ]);
                    break;
            }
        }
        return bmp;
    }

    // 多分辨率 ICO（PNG 帧，Vista+ 原生支持）
    public static Icon MakeStatusIcon(DrawingColor c, IconConcept concept = IconConcept.Rocket)
    {
        var sizes = new[] { 16, 20, 24, 32, 48, 64 };
        var frames = new List<(byte size, byte[] png)>();
        foreach (var s in sizes)
        {
            using var bmp = DrawFrame(s, c, concept);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            frames.Add(((byte)s, ms.ToArray()));
        }
        var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {// leaveOpen：Icon 会按需从流里读帧，流必须保持存活
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)frames.Count);
            int offset = 6 + 16 * frames.Count;
            foreach (var (sz, png) in frames)
            {
                w.Write(sz);
                w.Write(sz);
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write(png.Length);
                w.Write(offset);
                offset += png.Length;
            }
            foreach (var (_, png) in frames) w.Write(png);
        }
        ico.Position = 0;
        return new Icon(ico);
    }

    public static BitmapSource ToImageSource(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }

    static GraphicsPath RoundedRectF(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static DrawingColor Lighten(DrawingColor c, float f) =>
        DrawingColor.FromArgb(c.A, Lerp(c.R, 255, f), Lerp(c.G, 255, f), Lerp(c.B, 255, f));

    static DrawingColor Darken(DrawingColor c, float f) =>
        DrawingColor.FromArgb(c.A, (int)(c.R * (1 - f)), (int)(c.G * (1 - f)), (int)(c.B * (1 - f)));

    static int Lerp(int a, int b, float f) => (int)(a + (b - a) * f);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}

sealed class App : Application
{
    readonly TaskbarIcon _tray = new();

    public App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources = (ResourceDictionary)XamlReader.Parse(
            """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <SolidColorBrush x:Key="Fg" Color="#1A1A1A"/>
              <SolidColorBrush x:Key="FgDim" Color="#707070"/>
              <SolidColorBrush x:Key="Accent" Color="#2196F3"/>

              <Style TargetType="ContextMenu">
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="ContextMenu">
                      <Border Background="White" CornerRadius="10" Padding="5"
                              BorderBrush="#14000000" BorderThickness="1" SnapsToDevicePixels="True">
                        <Border.Effect>
                          <DropShadowEffect BlurRadius="22" ShadowDepth="3" Direction="270" Opacity="0.22"/>
                        </Border.Effect>
                        <ItemsPresenter/>
                      </Border>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>

              <Style TargetType="MenuItem">
                <Setter Property="Foreground" Value="{StaticResource Fg}"/>
                <Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Microsoft YaHei"/>
                <Setter Property="FontSize" Value="13"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="MenuItem">
                      <Border x:Name="Bd" Background="Transparent" CornerRadius="6" Padding="10,7" Margin="2,0">
                        <Grid>
                          <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="24"/>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="Auto"/>
                          </Grid.ColumnDefinitions>
                          <ContentPresenter ContentSource="Icon" VerticalAlignment="Center" HorizontalAlignment="Left"/>
                          <ContentPresenter ContentSource="Header" Grid.Column="1" VerticalAlignment="Center" Margin="6,0,0,0"/>
                          <Path x:Name="Check" Grid.Column="2" Width="12" Height="10" Stretch="Uniform"
                                Data="M 0 5 L 4 9 L 12 1" Stroke="{StaticResource Accent}" StrokeThickness="2"
                                StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                                VerticalAlignment="Center" Margin="8,0,0,0" Visibility="Collapsed"/>
                        </Grid>
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsHighlighted" Value="True">
                          <Setter TargetName="Bd" Property="Background" Value="#F0F0F0"/>
                        </Trigger>
                        <Trigger Property="IsChecked" Value="True">
                          <Setter TargetName="Check" Property="Visibility" Value="Visible"/>
                        </Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>

              <Style x:Key="MenuSeparator" TargetType="Separator">
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="Separator">
                      <Border Height="1" Background="#14000000" Margin="16,5"/>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>

              <Style x:Key="AccentButton" TargetType="Button">
                <Setter Property="Foreground" Value="White"/>
                <Setter Property="Background" Value="{StaticResource Accent}"/>
                <Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Microsoft YaHei"/>
                <Setter Property="FontSize" Value="13"/>
                <Setter Property="Cursor" Value="Hand"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="Button">
                      <Border x:Name="Bd" Background="{TemplateBinding Background}" CornerRadius="6" Padding="16,7">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                          <Setter TargetName="Bd" Property="Background" Value="#1976D2"/>
                        </Trigger>
                        <Trigger Property="IsPressed" Value="True">
                          <Setter TargetName="Bd" Property="Background" Value="#1565C0"/>
                        </Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>

              <Style x:Key="PlainButton" TargetType="Button">
                <Setter Property="Foreground" Value="{StaticResource Fg}"/>
                <Setter Property="Background" Value="White"/>
                <Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Microsoft YaHei"/>
                <Setter Property="FontSize" Value="13"/>
                <Setter Property="Cursor" Value="Hand"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="Button">
                      <Border x:Name="Bd" Background="{TemplateBinding Background}" CornerRadius="6"
                              BorderBrush="#22000000" BorderThickness="1" Padding="16,7">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                          <Setter TargetName="Bd" Property="Background" Value="#F5F5F5"/>
                        </Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
            </ResourceDictionary>
            """);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _ = new TrayController(_tray);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray.Dispose();
        base.OnExit(e);
    }
}

sealed class TrayController
{
    readonly TaskbarIcon _icon;
    readonly Dispatcher _ui;
    readonly DispatcherTimer _idleTimer = new();
    readonly Icon _idleIcon, _runIcon, _okIcon, _errIcon;
    readonly MenuItem _autoItem;
    bool _updating;

    public TrayController(TaskbarIcon icon)
    {
        _ui = Dispatcher.CurrentDispatcher;
        _icon = icon;
        _idleIcon = IconRenderer.MakeStatusIcon(DrawingColor.FromArgb(33, 150, 243));
        _runIcon = IconRenderer.MakeStatusIcon(DrawingColor.FromArgb(255, 152, 0));
        _okIcon = IconRenderer.MakeStatusIcon(DrawingColor.FromArgb(76, 175, 80));
        _errIcon = IconRenderer.MakeStatusIcon(DrawingColor.FromArgb(244, 67, 54));

        var menu = new ContextMenu();
        menu.Items.Add(MakeItem("更新订阅", IconKind.Refresh, (_, _) => Update()));
        _autoItem = MakeItem("开机自启", IconKind.Power, (_, _) => ToggleAutostart());
        _autoItem.IsCheckable = true;
        _autoItem.IsChecked = Program.IsAutostart();
        menu.Items.Add(_autoItem);
        menu.Items.Add(MakeItem("打开配置目录", IconKind.Folder, (_, _) => OpenConfigDir()));
        menu.Items.Add(MakeItem("关于", IconKind.Info, (_, _) => new AboutWindow().ShowDialog()));
        menu.Items.Add(new Separator { Style = (Style)Application.Current.Resources["MenuSeparator"] });
        menu.Items.Add(MakeItem("退出", IconKind.Cancel, (_, _) => Exit()));

        _icon.Icon = _idleIcon;
        _icon.ToolTipText = "sub2clash — 就绪";
        _icon.ContextMenu = menu;
        _icon.PopupActivation = PopupActivationMode.RightClick; // 左键=更新，右键=菜单
        _icon.TrayLeftMouseUp += (_, _) => Update();

        _idleTimer.Interval = TimeSpan.FromSeconds(3);
        _idleTimer.Tick += (_, _) => { _idleTimer.Stop(); SetStatus(_idleIcon, "sub2clash — 就绪"); };
    }

    static MenuItem MakeItem(string header, IconKind kind, RoutedEventHandler onClick)
    {
        using var bmp = IconRenderer.MenuIcon(kind);
        var item = new MenuItem
        {
            Header = header,
            Icon = new WpfImage { Source = IconRenderer.ToImageSource(bmp), Width = 18, Height = 18 },
        };
        item.Click += onClick;
        return item;
    }

    void SetStatus(Icon ic, string text)
    {
        _icon.Icon = ic;
        _icon.ToolTipText = text;
    }

    void Notify(string msg, string title, bool ok = true) =>
        _icon.ShowBalloonTip(title, msg, ok ? BalloonIcon.Info : BalloonIcon.Error);

    void ToggleAutostart()
    {
        var enable = _autoItem.IsChecked;
        Program.SetAutostart(enable);
        Notify(enable ? "已开启开机自启" : "已关闭开机自启", Program.AppName);
    }

    void OpenConfigDir() =>
        Process.Start(new ProcessStartInfo { FileName = AppContext.BaseDirectory, UseShellExecute = true });

    void Update()
    {
        if (_updating) { Notify("已经在更新中，请稍候", Program.AppName); return; }
        _updating = true;
        SetStatus(_runIcon, "sub2clash — 更新中…");
        Notify("正在更新订阅...", Program.AppName);
        Task.Run(() =>
        {
            var r = new UpdateService().Run(null);
            _ui.Invoke(() =>
            {
                _updating = false;
                SetStatus(r.Ok ? _okIcon : _errIcon, r.Ok ? "sub2clash — 更新成功" : "sub2clash — 更新失败");
                Notify(r.Summary, r.Ok ? "订阅更新成功" : "订阅更新失败", r.Ok);
                _idleTimer.Start();
            });
        });
    }

    void Exit() => Application.Current.Shutdown();
}

sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = "关于 sub2clash";
        Width = 384;
        Height = 186;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = System.Windows.Media.Brushes.White;
        FontFamily = new MediaFontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Microsoft YaHei");

        var grid = new Grid { Margin = new Thickness(24, 20, 24, 20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = grid;

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        using var bmp = IconRenderer.DrawFrame(44, DrawingColor.FromArgb(33, 150, 243));
        top.Children.Add(new WpfImage { Source = IconRenderer.ToImageSource(bmp), Width = 44, Height = 44 });

        var info = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        info.Children.Add(new TextBlock { Text = "sub2clash", FontSize = 18, FontWeight = FontWeights.SemiBold });
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        info.Children.Add(new TextBlock
        {
            Text = $"版本 {ver} · F# 核心 / C# 托盘",
            Foreground = (MediaBrush)Application.Current.Resources["FgDim"],
            Margin = new Thickness(0, 3, 0, 0),
        });
        info.Children.Add(new TextBlock
        {
            Text = "订阅转换 + Clash Verge 配置热重载",
            Foreground = (MediaBrush)Application.Current.Resources["FgDim"],
            Margin = new Thickness(0, 3, 0, 0),
        });
        top.Children.Add(info);
        Grid.SetRow(top, 0);
        grid.Children.Add(top);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var dir = new Button
        {
            Content = "打开配置目录",
            Style = (Style)Application.Current.Resources["PlainButton"],
        };
        dir.Click += (_, _) => Process.Start(new ProcessStartInfo { FileName = AppContext.BaseDirectory, UseShellExecute = true });
        var ok = new Button
        {
            Content = "确定",
            Style = (Style)Application.Current.Resources["AccentButton"],
            Width = 84,
            Margin = new Thickness(10, 0, 0, 0),
        };
        ok.Click += (_, _) => Close();
        btns.Children.Add(dir);
        btns.Children.Add(ok);
        Grid.SetRow(btns, 1);
        grid.Children.Add(btns);
    }
}
