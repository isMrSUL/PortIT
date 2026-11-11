namespace PortIT.Engine.Imaging;

/// <summary>
/// Pillow's ImageDraw.polygon(xy, fill=ink) on an 8-bit image, as measured against Pillow 12.3 (portit-check polygon:
/// 18,000 random triangles - slivers, duplicate points, points off the image - pixel-identical): the points cut to
/// whole pixels (C casts), an edge per side (dx in float32, from each side's first point), a scan line per row with
/// float32 crossings (a side's last row doubled when it is not the polygon's last), the corner rule - where two sides
/// leaning the same way meet on a row both start or end on, the crossing is moved to the next (or, on the last row,
/// the previous) row's crossings rounded +/- 1 when it lies over a pixel past both - and spans ROUND_UP .. ROUND_DOWN.
/// </summary>
public static class Draw
{
    sealed class Edge { public int X0, Y0, Xmin, Ymin, Xmax, Ymax; public float Dx; }

    static int RoundUpF(float f) => (int)(f >= 0 ? MathF.Floor(f + 0.5f) : -MathF.Floor(MathF.Abs(f) + 0.5f));
    static int RoundDownF(float f) => (int)(f >= 0 ? MathF.Ceiling(f - 0.5f) : -MathF.Ceiling(MathF.Abs(f) - 0.5f));
    static float Rf(float v) => v >= 0 ? MathF.Floor(MathF.Abs(v) + 0.5f) : -MathF.Floor(MathF.Abs(v) + 0.5f);

    static Edge AddEdge(int x0, int y0, int x1, int y1) => new()
    {
        X0 = x0, Y0 = y0, Xmin = Math.Min(x0, x1), Xmax = Math.Max(x0, x1), Ymin = Math.Min(y0, y1), Ymax = Math.Max(y0, y1),
        Dx = y0 == y1 ? 0f : (float)(x1 - x0) / (float)(y1 - y0),
    };

    static void HLine(byte[] px, int w, int h, int x0, int y, int x1, byte ink)
    {
        if (y < 0 || y >= h) return;
        x0 = Math.Max(x0, 0); x1 = Math.Min(x1, w - 1);
        for (int x = x0; x <= x1; x++) px[y * w + x] = ink;
    }

    static float At(Edge e, int y) => (float)(y - e.Y0) * e.Dx + (float)e.X0;

    public static void Polygon(byte[] px, int w, int h, IReadOnlyList<(double X, double Y)> pts, byte ink)
    {
        int count = pts.Count;
        var xy = pts.Select(p => ((int)p.X, (int)p.Y)).ToArray();
        var es = new List<Edge>();
        for (int i = 0; i < count - 1; i++) es.Add(AddEdge(xy[i].Item1, xy[i].Item2, xy[i + 1].Item1, xy[i + 1].Item2));
        if (xy[^1] != xy[0]) es.Add(AddEdge(xy[^1].Item1, xy[^1].Item2, xy[0].Item1, xy[0].Item2));
        int ymin = h - 1, ymax = 0;
        var tab = new List<Edge>();
        foreach (var e in es)
        {
            ymin = Math.Min(ymin, e.Ymin); ymax = Math.Max(ymax, e.Ymax);
            if (e.Ymin == e.Ymax) { HLine(px, w, h, e.Xmin, e.Ymin, e.Xmax, ink); continue; }
            tab.Add(e);
        }
        ymin = Math.Max(ymin, 0); ymax = Math.Min(ymax, h);
        var xx = new List<float>();
        for (int y = ymin; y <= ymax; y++)
        {
            xx.Clear();
            for (int i = 0; i < tab.Count; i++)
            {
                var e = tab[i];
                if (y < e.Ymin || y > e.Ymax) continue;
                xx.Add(At(e, y));
                if (y == e.Ymax && y < ymax) { xx.Add(xx[^1]); continue; }
                if (e.Dx == 0) continue;
                for (int k = 0; k < i; k++)
                {
                    var o = tab[k];
                    if ((e.Dx > 0 && o.Dx <= 0) || (e.Dx < 0 && o.Dx >= 0)) continue;
                    if (Rf(xx[^1]) != Rf(At(o, y)) || !(y == e.Ymin || y == e.Ymax) || !(y == o.Ymin || y == o.Ymax)) continue;
                    int off = y == ymax ? -1 : 1;
                    float a1 = At(e, y + off);
                    if (y + off < o.Ymin || y + off > o.Ymax) continue;
                    float a2 = At(o, y + off), x = xx[^1];
                    if (x > a1 + 1f && x > a2 + 1f) xx[^1] = Rf(Math.Max(a1, a2)) + 1f;
                    else if (x < a1 - 1f && x < a2 - 1f) xx[^1] = Rf(Math.Min(a1, a2)) - 1f;
                    break;
                }
            }
            xx.Sort();
            for (int i = 1; i < xx.Count; i += 2) HLine(px, w, h, RoundUpF(xx[i - 1]), y, RoundDownF(xx[i]), ink);
        }
    }
}
