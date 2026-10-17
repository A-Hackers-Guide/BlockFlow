using System.Drawing.Drawing2D;

namespace BlockFlow;

static class Theme
{
    public static readonly Color Window = Color.FromArgb(248, 249, 252);
    public static readonly Color Canvas = Color.FromArgb(240, 243, 248);
    public static readonly Color CanvasDots = Color.FromArgb(214, 220, 230);
    public static readonly Color Panel = Color.White;
    public static readonly Color Border = Color.FromArgb(220, 224, 232);
    public static readonly Color Text = Color.FromArgb(30, 34, 42);
    public static readonly Color SubText = Color.FromArgb(108, 116, 128);
    public static readonly Color Accent = Color.FromArgb(255, 191, 0);
    public static readonly Color Running = Color.FromArgb(0, 200, 120);
    public static readonly Color Error = Color.FromArgb(220, 40, 40);

    public static readonly Font Ui = new("Segoe UI", 9f);
    public static readonly Font UiBold = new("Segoe UI Semibold", 9f);
    public static readonly Font Header = new("Segoe UI Semibold", 11f);
    public static readonly Font BlockTitle = new("Segoe UI Semibold", 9.75f);
    public static readonly Font BlockSummary = new("Segoe UI", 8.75f);
    public static readonly Font Icon = new("Segoe UI Symbol", 11f);
    public static readonly Font Mono = new("Consolas", 9f);

    public static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static Color Lighter(Color c, float t) => Blend(c, Color.White, t);
    public static Color Darker(Color c, float t) => Blend(c, Color.Black, t);
}
