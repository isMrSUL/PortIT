using PortIT.Engine.Imaging;

namespace PortIT.Engine.Port;

/// <summary>
/// The superstar's picture on the select screen.
///
/// Until now a PortIT mod shipped no render at all, so the game fell back to the BASE superstar's
/// picture - a ported Rhea Ripley showed Michelle McCool's face on the select screen. The .svrmod spec
/// (the Mod Maker's own docs) wants three optional files next to ch.pac:
///
///   render.dds        512 x 512 DXT5   the select picture (also used for SSFB when there is no render_full)
///   render_full.dds   512 x 512 DXT5   the full-body render
///   render_small.dds  256 x 256 DXT5   the 256 bust
///
/// They are made from the built model itself with Viewer3D, on a see-through background - the Mod Maker
/// says "a PNG with a see-through background works best" - or from a picture the user gives.
/// (user request 2026-11-16)
/// </summary>
public static class SelectRender
{
    public const int Big = 512, Small = 256;

    // The game's own renders set the framing (DLC_HD.pac SSFA / SSFB / SSFC, checked against stock 0182):
    // the 512 one runs from just above the head down to about mid-thigh, the 256 bust from the head to the
    // waist - the body fills the frame in both. Viewer3D draws the whole model into 0.92 * H at Zoom 1 and
    // centres it at H/2 + PanY, so for a wanted head line `top` and a cut at fraction `f` down the body:
    //     Zoom = (1 - top) / (0.92 * f)        PanY = H * (top + 0.46 * Zoom - 0.5)
    const double HeadTop = 0.04;        // where the top of the head sits, as a fraction of the height

    static Viewer3D.Camera Frame(int h, double cut)
    {
        double zoom = (1 - HeadTop) / (0.92 * cut);
        return new Viewer3D.Camera { Zoom = zoom, Yaw = 0.18, Pitch = 0.03, PanY = h * (HeadTop + 0.46 * zoom - 0.5) };
    }

    /// <summary>head to about mid-thigh, as the game's SSFA / SSFB</summary>
    public static Viewer3D.Camera FullCamera(int h) => Frame(h, 0.72);

    /// <summary>head to about the waist, as the game's SSFC bust</summary>
    public static Viewer3D.Camera BustCamera(int h) => Frame(h, 0.45);

    /// <summary>
    /// Write render.dds / render_full.dds / render_small.dds into a finished mod folder from its own ch.pac.
    /// Returns the file names written.
    /// </summary>
    public static List<string> FromModel(string modDir, Action<string>? log = null, bool entrance = false)
    {
        string pac = Path.Combine(modDir, "ch.pac");
        var scene = Viewer3D.Load(pac, entrance ? 1 : 0, Viewer3D.HdColor(modDir, entrance));
        var full = Viewer3D.Draw(scene, Big, Big, FullCamera(Big), smooth: true, transparent: true);
        var bust = Viewer3D.Draw(scene, Small, Small, BustCamera(Small), smooth: true, transparent: true);
        return Write(modDir, full, Big, Big, bust, Small, Small, log);
    }

    /// <summary>The same three files from a picture the user chose (BGRA pixels).</summary>
    public static List<string> FromPicture(string modDir, byte[] bgra, int w, int h, Action<string>? log = null)
    {
        var full = Fit(bgra, w, h, Big, Big);
        var bust = Fit(bgra, w, h, Small, Small);
        return Write(modDir, full, Big, Big, bust, Small, Small, log);
    }

    static List<string> Write(string modDir, byte[] full, int fw, int fh, byte[] bust, int bw, int bh, Action<string>? log)
    {
        var made = new List<string>();
        void Put(string name, byte[] px, int w, int h)
        {
            File.WriteAllBytes(Path.Combine(modDir, name), DdsWriter.Encode(new Img(w, h, 4, Rgba(px)), "DXT5"));
            made.Add(name);
        }
        Put("render.dds", full, fw, fh);
        Put("render_full.dds", full, fw, fh);
        Put("render_small.dds", bust, bw, bh);
        log?.Invoke($"select screen pictures: {string.Join(", ", made)} ({fw} x {fh}, bust {bw} x {bh}, DXT5)");
        return made;
    }

    /// <summary>Viewer3D draws BGRA; Img wants RGBA.</summary>
    static byte[] Rgba(byte[] bgra)
    {
        var o = (byte[])bgra.Clone();
        for (int i = 0; i + 3 < o.Length; i += 4) (o[i], o[i + 2]) = (o[i + 2], o[i]);
        return o;
    }

    /// <summary>Scale a BGRA picture into w x h, keeping its shape and leaving the rest see-through.</summary>
    public static byte[] Fit(byte[] src, int sw, int sh, int w, int h)
    {
        var o = new byte[w * h * 4];
        if (sw <= 0 || sh <= 0) return o;
        double k = Math.Min((double)w / sw, (double)h / sh);
        int dw = Math.Max(1, (int)Math.Round(sw * k)), dh = Math.Max(1, (int)Math.Round(sh * k));
        int ox = (w - dw) / 2, oy = (h - dh) / 2;
        for (int y = 0; y < dh; y++)
        {
            int sy = Math.Min(sh - 1, (int)((y + 0.5) / k));
            for (int x = 0; x < dw; x++)
            {
                int sx = Math.Min(sw - 1, (int)((x + 0.5) / k));
                int si = 4 * (sy * sw + sx), di = 4 * ((y + oy) * w + (x + ox));
                o[di] = src[si]; o[di + 1] = src[si + 1]; o[di + 2] = src[si + 2]; o[di + 3] = src[si + 3];
            }
        }
        return o;
    }
}
