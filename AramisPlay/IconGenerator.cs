using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AramisPlay;

public static class IconGenerator
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon CreateAppIcon(int size = 64, bool hasNotificationDot = false)
    {
        using var bmp = RenderLogoBitmap(size, hasNotificationDot);
        IntPtr hIcon = bmp.GetHicon();
        var cloned = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return cloned;
    }

    public static Bitmap RenderLogoBitmap(int size, bool hasNotificationDot = false)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        var logoPath = FindLogoPngPath();
        if (logoPath != null && File.Exists(logoPath))
        {
            using var src = Image.FromFile(logoPath);
            g.DrawImage(src, new Rectangle(0, 0, size, size));
        }
        else
        {
            // Fallback sobre marine & or si logo.png absent
            float pad = size * 0.06f;
            var rect = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
            using var bgBrush = new SolidBrush(Color.FromArgb(255, 11, 23, 44));
            using var goldPen = new Pen(Color.FromArgb(255, 224, 179, 84), Math.Max(1.5f, size * 0.04f));
            g.FillEllipse(bgBrush, rect);
            g.DrawEllipse(goldPen, rect);

            var pts = new PointF[]
            {
                new(size * 0.38f, size * 0.29f),
                new(size * 0.38f, size * 0.71f),
                new(size * 0.72f, size * 0.50f)
            };
            using var goldBrush = new SolidBrush(Color.FromArgb(255, 235, 195, 105));
            g.FillPolygon(goldBrush, pts);
        }

        if (hasNotificationDot)
        {
            float dotSize = size * 0.30f;
            float pad = size * 0.04f;
            var dotRect = new RectangleF(size - dotSize - pad, pad, dotSize, dotSize);
            using var dotBrush = new SolidBrush(Color.FromArgb(255, 225, 70, 70));
            using var dotBorder = new Pen(Color.FromArgb(255, 10, 16, 30), Math.Max(1.5f, size * 0.04f));
            g.FillEllipse(dotBrush, dotRect);
            g.DrawEllipse(dotBorder, dotRect);
        }

        return bmp;
    }

    public static void EnsureExtensionIcons(string extensionIconsDir)
    {
        try
        {
            Directory.CreateDirectory(extensionIconsDir);
            foreach (int sz in new[] { 16, 48, 128 })
            {
                var path = Path.Combine(extensionIconsDir, $"icon{sz}.png");
                if (!File.Exists(path))
                {
                    using var bmp = RenderLogoBitmap(sz, false);
                    bmp.Save(path, ImageFormat.Png);
                }
            }
        }
        catch { }
    }

    private static string? FindLogoPngPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var direct = Path.Combine(baseDir, "wwwroot", "logo.png");
        if (File.Exists(direct)) return direct;

        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && dir != null; i++)
        {
            var c1 = Path.Combine(dir.FullName, "wwwroot", "logo.png");
            if (File.Exists(c1)) return c1;
            var c2 = Path.Combine(dir.FullName, "AramisPlay", "wwwroot", "logo.png");
            if (File.Exists(c2)) return c2;
            dir = dir.Parent;
        }
        return null;
    }
}
