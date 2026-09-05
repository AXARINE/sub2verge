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
using System.Windows.Shapes;
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
using WpfPath = System.Windows.Shapes.Path;

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
            ("mRefresh", FluentIcons.ArrowSync, 18),
            ("mPower", FluentIcons.Power, 18),
            ("mFolder", FluentIcons.Folder, 18),
            ("mInfo", FluentIcons.Info, 18),
            ("mCancel", FluentIcons.Dismiss, 18),
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
                    : IconRenderer.RenderFluent((string)frame!, size, DrawingColor.FromArgb(90, 90, 90));
                g.DrawImage(img, x, y);
                g.DrawString(name, font, DrawingBrushes.Gray, x + 4, 66);
            }
        }
        sheet.Save(path, ImageFormat.Png);
        Console.WriteLine($"preview saved: {path}");
    }
}

// 微软官方 Fluent System Icons 矢量路径（MIT 协议：https://github.com/microsoft/fluentui-system-icons）
// 纯 Geometry 渲染，不依赖任何字体，从根上避免字体回退乱码
static class FluentIcons
{
    public const string Rocket =
        "M10.7555 6.42502C11.5359 5.64466 12.801 5.64557 13.5813 6.42586C14.3616 7.20616 14.3625 8.47131 13.5822 9.25167C12.8018 10.032 11.5367 10.0311 10.7564 9.25082C9.97608 8.47053 9.97516 7.20537 10.7555 6.42502ZM12.8742 7.13297C12.4839 6.74267 11.8519 6.74282 11.4626 7.13212C11.0733 7.52142 11.0732 8.15342 11.4635 8.54372C11.8538 8.93402 12.4858 8.93386 12.8751 8.54456C13.2644 8.15526 13.2645 7.52327 12.8742 7.13297ZM9.43752 13.5982L10.0469 14.2076C10.5076 14.6683 11.1934 14.7667 11.7503 14.5027L12.8701 15.6226C13.0654 15.8179 13.382 15.8179 13.5772 15.6226L15.0036 14.1963C15.8556 13.3443 15.9589 12.0271 15.3136 11.0623L16.1617 10.2141C17.818 8.55786 18.4175 6.11894 17.7179 3.8836C17.4799 3.12306 16.8843 2.52744 16.1237 2.28942C13.8884 1.58983 11.4495 2.18938 9.79324 3.84561L8.94321 4.69564C7.97391 4.05374 6.65528 4.15975 5.80136 5.01367L4.38474 6.43029C4.29097 6.52406 4.23829 6.65124 4.23829 6.78384C4.23829 6.91645 4.29097 7.04363 4.38474 7.1374L5.50455 8.25721C5.24068 8.81402 5.33907 9.49977 5.79974 9.96044L6.40947 10.5702L5.18924 11.3014C5.05713 11.3806 4.96888 11.5162 4.95002 11.6691C4.93116 11.8219 4.9838 11.9749 5.0927 12.0838L7.92339 14.9145C8.03227 15.0234 8.18524 15.0761 8.33806 15.0572C8.49088 15.0384 8.62651 14.9502 8.70571 14.8182L9.43752 13.5982ZM16.7636 4.18228C17.352 6.06247 16.8477 8.1139 15.4546 9.50698L11.4611 13.5005C11.2658 13.6958 10.9493 13.6958 10.754 13.5005L9.69444 12.4409L9.69177 12.4382L7.56967 10.3161L7.567 10.3135L6.50684 9.25333C6.31158 9.05807 6.31158 8.74148 6.50684 8.54622L10.5004 4.55271C11.8934 3.15963 13.9449 2.65534 15.8251 3.24377C16.2728 3.3839 16.6234 3.73454 16.7636 4.18228ZM5.805 14.9094C6.00026 14.7141 6.00026 14.3975 5.805 14.2023C5.60974 14.007 5.29316 14.007 5.0979 14.2023L3.33013 15.97C3.13487 16.1653 3.13487 16.4819 3.33013 16.6771C3.52539 16.8724 3.84197 16.8724 4.03724 16.6771L5.805 14.9094ZM4.3896 12.7869C4.58486 12.9822 4.58486 13.2988 4.3896 13.4941L3.6807 14.2029C3.48544 14.3982 3.16886 14.3982 2.97359 14.2029C2.77833 14.0077 2.77833 13.6911 2.9736 13.4958L3.68249 12.7869C3.87775 12.5917 4.19433 12.5917 4.3896 12.7869ZM7.22029 16.3248C7.41555 16.1295 7.41555 15.813 7.22029 15.6177C7.02502 15.4224 6.70844 15.4224 6.51318 15.6177L5.80428 16.3266C5.60902 16.5218 5.60902 16.8384 5.80428 17.0337C5.99955 17.229 6.31613 17.229 6.51139 17.0337L7.22029 16.3248Z";

    public const string ArrowSync =
        "M9.88501 3.75004C8.32299 3.77855 6.77186 4.38831 5.58059 5.57957C3.13981 8.02035 3.13981 11.9776 5.58059 14.4184C5.79547 14.6333 6.02176 14.829 6.25739 15.0056C6.5888 15.2541 6.65604 15.7242 6.40758 16.0556C6.15911 16.387 5.68902 16.4543 5.3576 16.2058C5.06528 15.9866 4.78521 15.7443 4.51993 15.4791C1.49336 12.4525 1.49336 7.54548 4.51993 4.51891C5.86716 3.17169 7.58814 2.42408 9.34839 2.2763L8.76258 1.69049C8.46969 1.39759 8.46969 0.922719 8.76258 0.629826C9.05547 0.336933 9.53035 0.336933 9.82324 0.629826L11.9446 2.75115C12.2375 3.04404 12.2375 3.51891 11.9446 3.81181L9.82324 5.93313C9.53035 6.22602 9.05547 6.22602 8.76258 5.93313C8.46969 5.64023 8.46969 5.16536 8.76258 4.87247L9.88501 3.75004ZM10.115 16.2479C11.677 16.2194 13.2281 15.6096 14.4194 14.4184C16.8602 11.9776 16.8602 8.02031 14.4194 5.57953C14.2045 5.36466 13.9782 5.16896 13.7426 4.9923C13.4112 4.74383 13.344 4.27374 13.5924 3.94233C13.8409 3.61091 14.311 3.54367 14.6424 3.79214C14.9347 4.0113 15.2148 4.25359 15.4801 4.51887C18.5066 7.54543 18.5066 12.4525 15.4801 15.479C14.1328 16.8263 12.4119 17.5739 10.6516 17.7216L11.2374 18.3075C11.5303 18.6003 11.5303 19.0752 11.2374 19.3681C10.9445 19.661 10.4696 19.661 10.1768 19.3681L8.05543 17.2468C7.76253 16.9539 7.76253 16.479 8.05543 16.1861L10.1768 14.0648C10.4696 13.7719 10.9445 13.7719 11.2374 14.0648C11.5303 14.3577 11.5303 14.8326 11.2374 15.1255L10.115 16.2479Z";

    public const string Power =
        "M10.75 2.5C10.75 2.08579 10.4142 1.75 10 1.75C9.58579 1.75 9.25 2.08579 9.25 2.5V8.5C9.25 8.91421 9.58579 9.25 10 9.25C10.4142 9.25 10.75 8.91421 10.75 8.5V2.5ZM13.7432 4.00091C13.3843 3.79418 12.9257 3.91757 12.719 4.2765C12.5122 4.63544 12.6356 5.094 12.9946 5.30073C14.1393 5.96007 15.0345 6.9788 15.5412 8.19885C16.0478 9.4189 16.1377 10.7721 15.7968 12.0484C15.4559 13.3247 14.7032 14.4528 13.6557 15.2578C12.6081 16.0627 11.3242 16.4993 10.0031 16.5C8.68207 16.5007 7.3977 16.0654 6.3493 15.2616C5.30091 14.4578 4.54711 13.3304 4.20485 12.0545C3.8626 10.7785 3.95103 9.42523 4.45643 8.20465C4.96182 6.98407 5.85592 5.96441 7 5.30387C7.35872 5.09676 7.48163 4.63807 7.27452 4.27935C7.06742 3.92063 6.60872 3.79773 6.25 4.00483C4.8199 4.8305 3.70227 6.10508 3.07053 7.6308C2.43879 9.15653 2.32825 10.8481 2.75607 12.4431C3.18388 14.038 4.12613 15.4472 5.43663 16.452C6.74712 17.4567 8.35259 18.0009 10.0039 18C11.6553 17.9992 13.2602 17.4533 14.5696 16.4472C15.879 15.4411 16.8198 14.0309 17.246 12.4355C17.6721 10.8401 17.5598 9.14861 16.9265 7.62355C16.2931 6.09849 15.1742 4.82508 13.7432 4.00091Z";

    public const string Folder =
        "M2 5.5C2 4.11929 3.11929 3 4.5 3H6.98223C7.44636 3 7.89148 3.18437 8.21967 3.51256L9.5 4.79289L7.43934 6.85355C7.34557 6.94732 7.21839 7 7.08579 7H2V5.5ZM2 8V14.5C2 15.8807 3.11929 17 4.5 17H15.5C16.8807 17 18 15.8807 18 14.5V7.5C18 6.11929 16.8807 5 15.5 5H10.7071L8.14645 7.56066C7.86514 7.84196 7.48361 8 7.08579 8H2Z";

    public const string Info =
        "M18 10C18 5.58172 14.4183 2 10 2C5.58172 2 2 5.58172 2 10C2 14.4183 5.58172 18 10 18C14.4183 18 18 14.4183 18 10ZM9.50806 8.91012C9.55039 8.67687 9.75454 8.49999 10 8.49999C10.2455 8.49999 10.4496 8.67687 10.4919 8.91012L10.5 8.99999V13.5021L10.4919 13.592C10.4496 13.8253 10.2455 14.0021 10 14.0021C9.75454 14.0021 9.55039 13.8253 9.50806 13.592L9.5 13.5021V8.99999L9.50806 8.91012ZM9.25 6.74999C9.25 6.33578 9.58579 5.99999 10 5.99999C10.4142 5.99999 10.75 6.33578 10.75 6.74999C10.75 7.16421 10.4142 7.49999 10 7.49999C9.58579 7.49999 9.25 7.16421 9.25 6.74999Z";

    public const string Dismiss =
        "M3.89705 4.05379L3.96967 3.96967C4.23594 3.7034 4.6526 3.6792 4.94621 3.89705L5.03033 3.96967L10 8.939L14.9697 3.96967C15.2359 3.7034 15.6526 3.6792 15.9462 3.89705L16.0303 3.96967C16.2966 4.23594 16.3208 4.6526 16.1029 4.94621L16.0303 5.03033L11.061 10L16.0303 14.9697C16.2966 15.2359 16.3208 15.6526 16.1029 15.9462L16.0303 16.0303C15.7641 16.2966 15.3474 16.3208 15.0538 16.1029L14.9697 16.0303L10 11.061L5.03033 16.0303C4.76406 16.2966 4.3474 16.3208 4.05379 16.1029L3.96967 16.0303C3.7034 15.7641 3.6792 15.3474 3.89705 15.0538L3.96967 14.9697L8.939 10L3.96967 5.03033C3.7034 4.76406 3.6792 4.3474 3.89705 4.05379L3.96967 3.96967L3.89705 4.05379Z";
}

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

    // 把 Fluent 矢量路径渲染成位图（预览用；正式菜单直接用 WPF Path，零字体）
    public static Bitmap RenderFluent(string pathData, int size, DrawingColor color)
    {
        var geo = Geometry.Parse(pathData);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 20.0, size / 20.0));
            dc.DrawGeometry(
                new SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B)),
                null, geo);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        return new Bitmap(ms);
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
        menu.Items.Add(MakeItem("更新订阅", FluentIcons.ArrowSync, (_, _) => Update()));
        _autoItem = MakeItem("开机自启", FluentIcons.Power, (_, _) => ToggleAutostart());
        _autoItem.IsCheckable = true;
        _autoItem.IsChecked = Program.IsAutostart();
        menu.Items.Add(_autoItem);
        menu.Items.Add(MakeItem("打开配置目录", FluentIcons.Folder, (_, _) => OpenConfigDir()));
        menu.Items.Add(MakeItem("关于", FluentIcons.Info, (_, _) => new AboutWindow().ShowDialog()));
        menu.Items.Add(new Separator { Style = (Style)Application.Current.Resources["MenuSeparator"] });
        menu.Items.Add(MakeItem("退出", FluentIcons.Dismiss, (_, _) => Exit()));

        _icon.Icon = _idleIcon;
        _icon.ToolTipText = "sub2clash — 就绪";
        _icon.ContextMenu = menu;
        _icon.PopupActivation = PopupActivationMode.RightClick; // 左键=更新，右键=菜单
        _icon.TrayLeftMouseUp += (_, _) => Update();

        _idleTimer.Interval = TimeSpan.FromSeconds(3);
        _idleTimer.Tick += (_, _) => { _idleTimer.Stop(); SetStatus(_idleIcon, "sub2clash — 就绪"); };
    }

    static MenuItem MakeItem(string header, string pathData, RoutedEventHandler onClick)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new WpfPath
            {
                Data = Geometry.Parse(pathData),
                Fill = (MediaBrush)Application.Current.Resources["FgDim"],
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
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
