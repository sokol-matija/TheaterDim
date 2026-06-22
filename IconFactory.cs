using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TheaterDim;

// Draws the tray icon at runtime (no .ico file). Clapperboard glyph,
// blue idle / amber while the theater dim is active.
static class IconFactory
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    public static Icon Clapper(bool active)
    {
        const int S = 32;
        using var bmp = new Bitmap(S, S);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            Color accent = active ? Color.FromArgb(230, 168, 70)    // amber = theater on
                                  : Color.FromArgb(80, 150, 255);   // blue  = idle
            Color board = Color.FromArgb(28, 32, 44);

            // board body
            using (var body = Rounded(new RectangleF(3, 12, 26, 17), 4))
            using (var bb = new SolidBrush(board))
            using (var pen = new Pen(accent, 2.2f))
            {
                g.FillPath(bb, body);
                g.DrawPath(pen, body);
            }

            // clapper top bar + diagonal stripes
            using (var tb = new SolidBrush(accent))
                g.FillRectangle(tb, new RectangleF(3, 4, 26, 7));
            using (var sp = new Pen(board, 2f))
                for (float x = 7; x < 30; x += 7)
                    g.DrawLine(sp, x, 4, x - 4, 11);

            // play triangle on the board
            using (var wb = new SolidBrush(Color.FromArgb(235, 240, 250)))
                g.FillPolygon(wb, new[]
                {
                    new PointF(13, 16f), new PointF(13, 26f), new PointF(23, 21f)
                });
        }

        IntPtr hicon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hicon).Clone(); // own copy...
        DestroyIcon(hicon);                              // ...so source handle is safe to free
        return icon;
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        float d = rad * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
