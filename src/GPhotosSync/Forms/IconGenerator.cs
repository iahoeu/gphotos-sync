using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace GPhotosSync.Forms;

public static class IconGenerator
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon CreateTrayIcon(Color badgeColor)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Pinwheel / Camera rounded square background
            using var bgBrush = new LinearGradientBrush(
                new Rectangle(2, 2, 28, 28),
                Color.FromArgb(38, 115, 235),
                Color.FromArgb(20, 75, 175),
                45f);

            using var path = CreateRoundedRect(new Rectangle(2, 2, 26, 26), 7);
            g.FillPath(bgBrush, path);

            // White lens/photo symbol in center
            using var whitePen = new Pen(Color.White, 2.2f);
            g.DrawEllipse(whitePen, 9, 9, 12, 12);

            // Status badge in bottom-right corner
            using var badgeBrush = new SolidBrush(badgeColor);
            using var badgeBorder = new Pen(Color.White, 2f);
            g.FillEllipse(badgeBrush, 18, 18, 12, 12);
            g.DrawEllipse(badgeBorder, 18, 18, 12, 12);
        }

        IntPtr hIcon = bmp.GetHicon();
        using var tempIcon = Icon.FromHandle(hIcon);
        var clonedIcon = (Icon)tempIcon.Clone();
        DestroyIcon(hIcon);
        return clonedIcon;
    }

    private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
