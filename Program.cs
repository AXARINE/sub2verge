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
        Console.WriteLine($"icon font: {IconRenderer.IconFontName}");
        var items = new (string name, DrawingColor color, char glyph)[]
        {
            ("idle",     DrawingColor.FromArgb(33, 150, 243), Glyphs.Bolt),
            ("running",  DrawingColor.FromArgb(255, 152, 0),  Glyphs.Bolt),
            ("ok",       DrawingColor.FromArgb(76, 175, 80),  Glyphs.Bolt),
            ("err",      DrawingColor.FromArgb(244, 67, 54),  Glyphs.Bolt),
            ("idle-globe", DrawingColor.FromArgb(33, 150, 243), Glyphs.Globe),
            ("idle-sync",  DrawingColor.FromArgb(33, 150, 243), Glyphs.Sync),
        };
        var sheet = new Bitmap(items.Length * 80 + 8, 88);
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(DrawingColor.White);
            var font = new Font("Segoe UI", 9);
            for (int i = 0; i < items.Length; i++)
            {
                using var ic = IconRenderer.MakeStatusIcon(items[i].color, items[i].glyph);
                g.DrawIcon(ic, 8 + i * 80, 8);
                g.DrawString(items[i].name, font, DrawingBrushes.Gray, 8 + i * 80 + 18, 74);
            }
        }
        sheet.Save(path, ImageFormat.Png);
        Console.WriteLine($"preview saved: {path}");
    }
}

// Segoe Fluent Icons / MDL2 字形码（Windows 10/11 内置）
static class Glyphs
{
    public const char Bolt = '\uE945';      // 闪电
    public const char Globe = '\uE774';     // 地球
    public const char Sync = '\uE895';      // 同步
    public const char Refresh = '\uE72C';   // 刷新
    public const char Power = '\uE7E8';     // 电源
    public const char Folder = '\uE838';    // 文件夹
    public const char Info = '\uE946';      // 信息
    public const char Cancel = '\uE711';    // X
}

static class IconRenderer
{
    static readonly DrawingFontFamily IconFont;
    public static string IconFontName => IconFont.Name;

    static IconRenderer()
    {
        var families = DrawingFontFamily.Families;
        IconFont = families.FirstOrDefault(f => f.Name == "Segoe Fluent Icons")
                ?? families.FirstOrDefault(f => f.Name == "Segoe MDL2 Assets")
                ?? DrawingFontFamily.GenericSansSerif;
    }

    public static Icon MakeStatusIcon(DrawingColor c, char glyph)
    {
        var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var rect = new Rectangle(5, 5, 54, 54);
            using var path = RoundedRect(rect, 15);
            using var brush = new DrawingLinearGradientBrush(rect, Lighten(c, 0.30f), Darken(c, 0.18f), 45f);
            g.FillPath(brush, path);
            DrawGlyph(g, glyph, DrawingBrushes.White, 26, new RectangleF(0, 0, 64, 64));
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        bmp.Dispose();
        return icon;
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

    static void DrawGlyph(Graphics g, char glyph, DrawingBrush brush, float fontPx, RectangleF bounds)
    {
        using var f = new Font(IconFont, fontPx, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph.ToString(), f, brush, bounds, sf);
    }

    static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
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
                <Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI"/>
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
                <Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI"/>
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
                <Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI"/>
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
        _idleIcon = IconRenderer.MakeStatusIcon(DrawingColor.FromArgb(33, 150, 243), Glyphs.Bolt);
        _runIcon = IconRenderer.MakeStatusIcon(DrawingColor.FromArgb(255, 152, 0), Glyphs.Bolt);
        _okIcon = IconRenderer.MakeStatusIcon(DrawingColor.FromArgb(76, 175, 80), Glyphs.Bolt);
        _errIcon = IconRenderer.MakeStatusIcon(DrawingColor.FromArgb(244, 67, 54), Glyphs.Bolt);

        var menu = new ContextMenu();
        menu.Items.Add(MakeItem("更新订阅", Glyphs.Refresh, (_, _) => Update()));
        _autoItem = MakeItem("开机自启", Glyphs.Power, (_, _) => ToggleAutostart());
        _autoItem.IsCheckable = true;
        _autoItem.IsChecked = Program.IsAutostart();
        menu.Items.Add(_autoItem);
        menu.Items.Add(MakeItem("打开配置目录", Glyphs.Folder, (_, _) => OpenConfigDir()));
        menu.Items.Add(MakeItem("关于", Glyphs.Info, (_, _) => new AboutWindow().ShowDialog()));
        menu.Items.Add(new Separator { Style = (Style)Application.Current.Resources["MenuSeparator"] });
        menu.Items.Add(MakeItem("退出", Glyphs.Cancel, (_, _) => Exit()));

        _icon.Icon = _idleIcon;
        _icon.ToolTipText = "sub2clash — 就绪";
        _icon.ContextMenu = menu;
        _icon.PopupActivation = PopupActivationMode.RightClick; // 左键=更新，右键=菜单
        _icon.TrayLeftMouseUp += (_, _) => Update();

        _idleTimer.Interval = TimeSpan.FromSeconds(3);
        _idleTimer.Tick += (_, _) => { _idleTimer.Stop(); SetStatus(_idleIcon, "sub2clash — 就绪"); };
    }

    static MenuItem MakeItem(string header, char glyph, RoutedEventHandler onClick)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock
            {
                Text = glyph.ToString(),
                FontFamily = new MediaFontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 15,
                Foreground = (MediaBrush)Application.Current.Resources["FgDim"],
            },
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
        FontFamily = new MediaFontFamily("Segoe UI Variable Text, Segoe UI");

        var grid = new Grid { Margin = new Thickness(24, 20, 24, 20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = grid;

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        using var bmp = IconRenderer.MakeStatusIcon(DrawingColor.FromArgb(33, 150, 243), Glyphs.Bolt).ToBitmap();
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
