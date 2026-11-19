using PortIT.Engine.Imaging;
using PortIT.Engine.Readers;

namespace PortIT.Engine.Port;

/// <summary>
/// The SOURCE model (the 2K / AEW / mod model, before porting) as a Viewer3D scene, so it can be turned, zoomed and
/// looked at from any side in the same 3D window the built model already had - and so the flat preview picture and the
/// 3D view are drawn by one renderer and always agree (2026-11-18, the user's "big update to the previewer").
///
/// The built model carries ONE texture sheet for the whole body; a source model has one map per piece, so each piece
/// gets its own (Viewer3D.Part.Tex). The decisions about hair and cut-outs are the ones the flat preview already made:
///
///   * hair with no colour map (every 2K26 model) -> one flat colour, the median of its vertex colours, made sRGB
///   * its shape comes from the strand map (&lt;mat&gt;_rai), green channel, read at its OWN size
///   * a cut-out piece with a colour map is cut by that map's alpha, also at its own size
/// </summary>
public static class SourceScene
{
    /// <summary>colour maps are shrunk to this (a whole body is 8 - 12 maps; at 2048 they would be 16 MB each)</summary>
    public const int ColourSide = 1024;

    public static Viewer3D.Scene Build(SourceAew.Model src, int lod = 0)
    {
        var scene = new Viewer3D.Scene();
        var lods = new Dictionary<string, List<List<Mcd.LodMesh>>>();
        var colours = new Dictionary<string, (byte[] Px, int W, int H)?>();
        var masks = new Dictionary<string, (byte[] A, int W, int H)?>();

        int[] TrisFor(SourceAew.SourceMesh me)
        {
            if (me.SourcePath == null || lod <= 0) return me.Tris;
            if (!lods.TryGetValue(me.SourcePath, out var L))
            {
                try { L = Mcd.Load(me.SourcePath).Lods; } catch (Exception) { L = []; }
                lods[me.SourcePath] = L;
            }
            if (L.Count > lod)
                foreach (var row in L[lod]) if (row.Mesh == me.Index) return row.Triangles;
            return me.Tris;
        }

        Img? Raw(SourceAew.SourceMesh me, string role)
        {
            if (me.Images.TryGetValue(role, out var im)) return im;                 // AEW: already in memory
            if (!me.Textures.TryGetValue(role, out var path)) return null;
            try { return ImageIO.Load(path); } catch (Exception) { return null; }
        }

        (byte[] Px, int W, int H)? Colour(SourceAew.SourceMesh me)
        {
            string key = me.Textures.TryGetValue("color", out var p) ? p : "img:" + me.Material;
            if (colours.TryGetValue(key, out var c)) return c;
            var raw = Raw(me, "color");
            if (raw == null) { colours[key] = null; return null; }
            var im = raw.ToRgba();
            if (Math.Max(im.W, im.H) > ColourSide)
                im = Resample.ResizeRgbaPremultiplied(im, ColourSide, Math.Max(1, ColourSide * im.H / im.W), Filter.Bilinear);
            c = (im.Px, im.W, im.H);
            colours[key] = c;
            return c;
        }

        (byte[] A, int W, int H)? Mask(SourceAew.SourceMesh me, string role, int ch)
        {
            string key = (me.Textures.TryGetValue(role, out var p) ? p : "img:" + me.Material + ":" + role) + "|" + ch;
            if (masks.TryGetValue(key, out var c)) return c;
            var raw = Raw(me, role);
            if (raw == null) { masks[key] = null; return null; }
            var im = raw.ToRgba();
            var a = new byte[im.W * im.H];
            for (int i = 0; i < a.Length; i++) a[i] = im.Px[4 * i + ch];
            c = (a, im.W, im.H);
            masks[key] = c;
            return c;
        }

        foreach (var me in src.Meshes)
        {
            if (me.Variant || me.Kind == "teeth" || me.Material.ToLowerInvariant() is "m_tongue" or "m_teeth") continue;
            var T = TrisFor(me);
            if (T.Length == 0 || me.N == 0) continue;
            var part = new Viewer3D.Part
            {
                Name = me.Material,
                // (only the skin takes the wet "sweat" highlight in the 3D view, as on the built model)
                Model = me.Kind is "skin" or "head" ? 0 : 1,
                V = me.Positions.Select(x => (float)x).ToArray(),
                N = me.Normals.Select(x => (float)x).ToArray(),
                UV = me.Uvs.Select(x => (float)x).ToArray(),
                T = T,
            };
            var col = Colour(me);
            bool hair = me.Kind == "hair";
            if (hair && col != null && !me.Textures.ContainsKey("rai") && !me.Images.ContainsKey("rai"))
            {
                // hair that DOES have a colour map: its own alpha cuts it
                var m = Mask(me, "color", 3);
                if (m != null) { part.Mask = m.Value.A; part.Mw = m.Value.W; part.Mh = m.Value.H; }
            }
            else if (hair || (me.Cutout && col == null))
            {
                var m = Mask(me, "rai", 1);
                if (m != null) { part.Mask = m.Value.A; part.Mw = m.Value.W; part.Mh = m.Value.H; }
                part.Solid = HairColour(me);
                col = null;                        // 2K hair is coloured by its vertex colours, not by a map
            }
            else if (me.Cutout && col != null)
            {
                var m = Mask(me, "color", 3);
                if (m != null) { part.Mask = m.Value.A; part.Mw = m.Value.W; part.Mh = m.Value.H; }
            }
            if (col != null) { part.Tex = col.Value.Px; part.TW = col.Value.W; part.TH = col.Value.H; }
            scene.Parts.Add(part);
        }
        Measure(scene);
        return scene;
    }

    /// <summary>2K hair has no colour map: the median of its vertex colours, made sRGB (the same sum the flat preview
    /// and the port both use)</summary>
    static float[] HairColour(SourceAew.SourceMesh me)
    {
        var c = new double[] { .05, .04, .03 };
        if (me.Colors != null && me.Colors.Length > 0 && me.ColorC > 0)
            for (int k = 0; k < 3; k++)
            {
                var v = Enumerable.Range(0, me.Colors.Length / me.ColorC).Select(i => me.Colors[me.ColorC * i + k]).Order().ToArray();
                int n = v.Length;
                c[k] = n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2;
            }
        for (int k = 0; k < 3; k++) c[k] = c[k] <= 0.0031308 ? 12.92 * c[k] : 1.055 * Math.Pow(Math.Clamp(c[k], 0, 1), 1 / 2.4) - 0.055;
        return c.Select(x => (float)(x * 255)).ToArray();
    }

    /// <summary>the box round what is DRAWN, and the point the camera turns about</summary>
    public static void Measure(Viewer3D.Scene s)
    {
        for (int k = 0; k < 3; k++) { s.Lo[k] = float.PositiveInfinity; s.Hi[k] = float.NegativeInfinity; }
        s.Verts = s.Tris = 0;
        foreach (var p in s.Parts)
        {
            if (p.Hidden) continue;
            s.Verts += p.Verts; s.Tris += p.T.Length / 3;
            for (int i = 0; i < p.Verts; i++)
                for (int k = 0; k < 3; k++)
                {
                    s.Lo[k] = Math.Min(s.Lo[k], p.V[3 * i + k]);
                    s.Hi[k] = Math.Max(s.Hi[k], p.V[3 * i + k]);
                }
        }
        if (float.IsInfinity(s.Lo[0])) for (int k = 0; k < 3; k++) { s.Lo[k] = 0; s.Hi[k] = 1; }
        for (int k = 0; k < 3; k++) s.Centre[k] = (s.Lo[k] + s.Hi[k]) / 2;
    }
}
