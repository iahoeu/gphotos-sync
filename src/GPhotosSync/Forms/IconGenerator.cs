using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace GPhotosSync.Forms;

public static class IconGenerator
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    // Stav ikony u hodin: celá ikona má barvu stavu
    public static readonly (Color Light, Color Dark) Green = (Color.FromArgb(46, 184, 92), Color.FromArgb(22, 128, 58));
    public static readonly (Color Light, Color Dark) Blue = (Color.FromArgb(48, 128, 240), Color.FromArgb(20, 78, 185));
    private static Icon? _appIcon;

    /// <summary>
    /// Ikona aplikace a jejích oken (modrá varianta, soubor app.ico vložený do sestavení).
    /// </summary>
    public static Icon AppIcon
    {
        get
        {
            if (_appIcon != null)
                return _appIcon;

            using var stream = typeof(IconGenerator).Assembly.GetManifestResourceStream("GPhotosSync.app.ico");
            _appIcon = stream != null ? new Icon(stream) : CreateStatusIcon(Blue);
            return _appIcon;
        }
    }

    public static readonly (Color Light, Color Dark) Red = (Color.FromArgb(232, 64, 56), Color.FromArgb(170, 28, 28));

    /// <summary>
    /// Výrazná ikona: celý zaoblený čtverec v barvě stavu, uprostřed bílý symbol objektivu.
    /// Stejný návrh používá i soubor app.ico (aplikace, okna, instalátor) v modré barvě.
    /// </summary>
    public static Icon CreateStatusIcon((Color Light, Color Dark) colors, int size = 32)
    {
        using var bmp = RenderBitmap(colors, size);
        IntPtr hIcon = bmp.GetHicon();
        using var tempIcon = Icon.FromHandle(hIcon);
        var clonedIcon = (Icon)tempIcon.Clone();
        DestroyIcon(hIcon);
        return clonedIcon;
    }

    public static Bitmap RenderBitmap((Color Light, Color Dark) colors, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size / 32f;
        var square = new RectangleF(1 * s, 1 * s, 30 * s, 30 * s);
        using var bgBrush = new LinearGradientBrush(square, colors.Light, colors.Dark, 45f);
        using var path = CreateRoundedRect(square, 8 * s);
        g.FillPath(bgBrush, path);

        // Bílý objektiv: silný kroužek a středová tečka (čitelné i v 16 px)
        using var whitePen = new Pen(Color.White, 3.4f * s);
        g.DrawEllipse(whitePen, 8.5f * s, 8.5f * s, 15f * s, 15f * s);
        using var whiteBrush = new SolidBrush(Color.White);
        g.FillEllipse(whiteBrush, 13.5f * s, 13.5f * s, 5f * s, 5f * s);

        return bmp;
    }

    private static GraphicsPath CreateRoundedRect(RectangleF bounds, float radius)
    {
        float diameter = radius * 2;
        var path = new GraphicsPath();
        var arc = new RectangleF(bounds.Location, new SizeF(diameter, diameter));

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
