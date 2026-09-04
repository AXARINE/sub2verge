// Program.cs — 系统托盘应用：左键更新订阅，右键菜单（更新/开机自启/配置目录/关于/退出）
// 托盘图标：Segoe Fluent Icons 字形（Windows 自带图标字体）+ 状态色渐变圆角底；菜单项带图标
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Sub2Clash.Core;
using Icon = System.Drawing.Icon;

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

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext());
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
        // 字形渲染诊断：不透明像素占比（正常字形 >5%）
        foreach (var glyph in new[] { Glyphs.Bolt, Glyphs.Globe, Glyphs.Refresh })
        {
            using var b = IconRenderer.MenuIcon(glyph, 32);
            var opaque = 0;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                    if (b.GetPixel(x, y).A > 10) opaque++;
            Console.WriteLine($"glyph U+{((int)glyph):X4} @32: opaque={opaque}/{b.Width * b.Height} ({opaque * 100.0 / b.Width / b.Height:F1}%)");
        }
        var items = new (string name, Color color, char glyph)[]
        {
            ("idle",     Color.FromArgb(33, 150, 243), Glyphs.Bolt),
            ("running",  Color.FromArgb(255, 152, 0),  Glyphs.Bolt),
            ("ok",       Color.FromArgb(76, 175, 80),  Glyphs.Bolt),
            ("err",      Color.FromArgb(244, 67, 54),  Glyphs.Bolt),
            ("idle-globe", Color.FromArgb(33, 150, 243), Glyphs.Globe),
            ("idle-sync",  Color.FromArgb(33, 150, 243), Glyphs.Sync),
        };
        var sheet = new Bitmap(items.Length * 80 + 8, 88);
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.White);
            var font = new Font("Segoe UI", 9);
            for (int i = 0; i < items.Length; i++)
            {
                using var ic = IconRenderer.MakeStatusIcon(items[i].color, items[i].glyph);
                g.DrawIcon(ic, 8 + i * 80, 8);
                g.DrawString(items[i].name, font, Brushes.Gray, 8 + i * 80 + 18, 74);
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
    public const char SignOut = '\uE77B';   // 退出（门+箭头，Fluent 映射不准，已弃用）
    public const char Cancel = '\uE711';    // 退出：X
}

static class IconRenderer
{
    static readonly FontFamily IconFont;
    public static string IconFontName => IconFont.Name;

    static IconRenderer()
    {
        var families = FontFamily.Families;
        IconFont = families.FirstOrDefault(f => f.Name == "Segoe Fluent Icons")
                ?? families.FirstOrDefault(f => f.Name == "Segoe MDL2 Assets")
                ?? FontFamily.GenericSansSerif;
    }

    public static Icon MakeStatusIcon(Color c, char glyph)
    {
        var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var rect = new Rectangle(5, 5, 54, 54);
            using var path = RoundedRect(rect, 15);
            using var brush = new LinearGradientBrush(rect, Lighten(c, 0.30f), Darken(c, 0.18f), 45f);
            g.FillPath(brush, path);
            DrawGlyph(g, glyph, Brushes.White, 26, new RectangleF(0, 0, 64, 64));
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        bmp.Dispose();
        return icon;
    }

    public static Bitmap MenuIcon(char glyph, int size = 18) =>
        RenderGlyph(glyph, Color.FromArgb(80, 80, 80), size, size);

    static Bitmap RenderGlyph(char glyph, Color color, int size, int fontPx)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        DrawGlyph(g, glyph, new SolidBrush(color), fontPx, new RectangleF(0, 0, size, size));
        return bmp;
    }

    static void DrawGlyph(Graphics g, char glyph, Brush brush, float fontPx, RectangleF bounds)
    {
        using var f = new Font(IconFont, fontPx, FontStyle.Regular, GraphicsUnit.Pixel);
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

    static Color Lighten(Color c, float f) =>
        Color.FromArgb(c.A, Lerp(c.R, 255, f), Lerp(c.G, 255, f), Lerp(c.B, 255, f));

    static Color Darken(Color c, float f) =>
        Color.FromArgb(c.A, (int)(c.R * (1 - f)), (int)(c.G * (1 - f)), (int)(c.B * (1 - f)));

    static int Lerp(int a, int b, float f) => (int)(a + (b - a) * f);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}

sealed class TrayContext : ApplicationContext
{
    readonly NotifyIcon _icon;
    readonly ContextMenuStrip _menu;
    readonly ToolStripMenuItem _autostartItem;
    readonly Control _sync = new();
    readonly System.Windows.Forms.Timer _idleTimer = new();
    readonly Icon _idleIcon, _runIcon, _okIcon, _errIcon;
    bool _updating;

    public TrayContext()
    {
        _sync.CreateControl();
        _idleIcon = IconRenderer.MakeStatusIcon(Color.FromArgb(33, 150, 243), Glyphs.Bolt);
        _runIcon = IconRenderer.MakeStatusIcon(Color.FromArgb(255, 152, 0), Glyphs.Bolt);
        _okIcon = IconRenderer.MakeStatusIcon(Color.FromArgb(76, 175, 80), Glyphs.Bolt);
        _errIcon = IconRenderer.MakeStatusIcon(Color.FromArgb(244, 67, 54), Glyphs.Bolt);

        _menu = new ContextMenuStrip();
        _autostartItem = new ToolStripMenuItem("开机自启", IconRenderer.MenuIcon(Glyphs.Power), (_, _) => ToggleAutostart())
        { Checked = Program.IsAutostart() };
        _menu.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripMenuItem("更新订阅", IconRenderer.MenuIcon(Glyphs.Refresh), (_, _) => Update()),
            _autostartItem,
            new ToolStripMenuItem("打开配置目录", IconRenderer.MenuIcon(Glyphs.Folder), (_, _) => OpenConfigDir()),
            new ToolStripMenuItem("关于", IconRenderer.MenuIcon(Glyphs.Info), (_, _) => new AboutForm().ShowDialog()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("退出", IconRenderer.MenuIcon(Glyphs.Cancel), (_, _) => Exit()),
        });

        _icon = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "sub2clash — 就绪",
            Visible = true,
            ContextMenuStrip = _menu, // 交给系统管理菜单弹出/关闭：点外部能正常关掉
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) Update();
        };

        _idleTimer.Interval = 3000;
        _idleTimer.Tick += (_, _) => { _idleTimer.Stop(); SetStatus(_idleIcon, "sub2clash — 就绪"); };
    }

    void SetStatus(Icon ic, string text)
    {
        _icon.Icon = ic;
        _icon.Text = text;
    }

    void Notify(string msg, string title, ToolTipIcon ti = ToolTipIcon.Info) =>
        _icon.ShowBalloonTip(3000, title, msg, ti);

    void ToggleAutostart()
    {
        var enable = !Program.IsAutostart();
        Program.SetAutostart(enable);
        _autostartItem.Checked = enable;
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
            _sync.BeginInvoke(() =>
            {
                _updating = false;
                SetStatus(r.Ok ? _okIcon : _errIcon, r.Ok ? "sub2clash — 更新成功" : "sub2clash — 更新失败");
                Notify(r.Summary, r.Ok ? "订阅更新成功" : "订阅更新失败", r.Ok ? ToolTipIcon.Info : ToolTipIcon.Error);
                _idleTimer.Start();
            });
        });
    }

    void Exit()
    {
        _idleTimer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _sync.Dispose();
        foreach (var ic in new[] { _idleIcon, _runIcon, _okIcon, _errIcon }) ic.Dispose();
        ExitThread();
    }
}

sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text = "关于 sub2clash";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(372, 176);
        Font = new Font("Segoe UI", 9);

        using var logo = IconRenderer.MakeStatusIcon(Color.FromArgb(33, 150, 243), Glyphs.Bolt);
        var pic = new PictureBox { Image = logo.ToBitmap(), Size = new Size(48, 48), Location = new Point(20, 24) };
        Controls.Add(pic);

        var title = new Label
        {
            Text = "sub2clash",
            Font = new Font("Segoe UI Semibold", 13),
            AutoSize = true,
            Location = new Point(84, 20),
        };
        Controls.Add(title);

        var version = new Label
        {
            Text = $"版本 {Application.ProductVersion}  ·  F# 核心 / C# 托盘",
            ForeColor = Color.FromArgb(120, 120, 120),
            AutoSize = true,
            Location = new Point(86, 48),
        };
        Controls.Add(version);

        var desc = new Label
        {
            Text = "订阅转换 + Clash Verge 配置热重载",
            ForeColor = Color.FromArgb(80, 80, 80),
            AutoSize = true,
            Location = new Point(20, 96),
        };
        Controls.Add(desc);

        var dirBtn = new Button
        {
            Text = "打开配置目录",
            AutoSize = true,
            Location = new Point(20, 134),
        };
        dirBtn.Click += (_, _) => Process.Start(new ProcessStartInfo { FileName = AppContext.BaseDirectory, UseShellExecute = true });
        Controls.Add(dirBtn);

        var okBtn = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Size = new Size(80, 30),
            Location = new Point(272, 130),
        };
        Controls.Add(okBtn);
        AcceptButton = okBtn;
        CancelButton = okBtn;
    }
}
