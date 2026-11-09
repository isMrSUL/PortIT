using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PortIT.Controls;

/// <summary>The PortIT logo (a sibling of AnimIT's): a "P" whose bowl is a curved arrow coming home to the stem -
/// a wrestler carried from one game into another. 160 x 160 design space, scaled; thicker strokes below 40 px.</summary>
public sealed class LogoMark : Control
{
    static readonly IBrush Tile = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#17335F"), 0), new GradientStop(Color.Parse("#0B1426"), 1) },
    };
    static readonly IPen Rim = new Pen(new SolidColorBrush(Color.Parse("#2E5490")), 2.5);
    static readonly IBrush Light = new SolidColorBrush(Color.Parse("#62B8FF"));
    static readonly IBrush Deep = new SolidColorBrush(Color.Parse("#3B8CFF"));
    static readonly IBrush Cyan = new SolidColorBrush(Color.Parse("#22C7E8"));
    static readonly IBrush Key = new SolidColorBrush(Color.Parse("#EEF3FA"));
    static readonly Geometry Bowl = Geometry.Parse("M 56,32 C 126,26 132,96 74,92");
    static readonly Geometry Head = Geometry.Parse("M 62,92 L 80,79 L 80,105 Z");

    public bool ShowTile { get; set; } = true;

    public override void Render(DrawingContext ctx)
    {
        double size = System.Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        double s = size / 160.0;
        bool small = size < 40;
        double w = small ? 15 : 12;
        using (ctx.PushTransform(Matrix.CreateTranslation((Bounds.Width - size) / 2 / s, (Bounds.Height - size) / 2 / s) * Matrix.CreateScale(s, s)))
        {
            if (ShowTile) ctx.DrawRectangle(Tile, small ? null : Rim, new RoundedRect(new Rect(1.25, 1.25, 157.5, 157.5), 36));
            ctx.DrawLine(new Pen(Light, w, lineCap: PenLineCap.Round), new Point(52, 30), new Point(52, 130));
            ctx.DrawGeometry(null, new Pen(Deep, w, lineCap: PenLineCap.Round), Bowl);
            ctx.DrawGeometry(Key, null, Head);
            if (!small) ctx.DrawEllipse(Cyan, null, new Point(52, 130), 7.5, 7.5);
        }
    }
}
