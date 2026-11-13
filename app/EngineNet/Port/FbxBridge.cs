using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PortIT.Engine.Imaging;
using PortIT.Engine.Readers;

namespace PortIT.Engine.Port;

/// <summary>
/// An FBX character (Red Dead Redemption 1 / 2 rips so far) into the 2K pipeline through AewBridge: its bones renamed
/// to 2K's (the missing twist / finger-tip bones added), feet on the floor, scaled to a man's size when the file's
/// units are off, each mesh given its kind and its textures by a small config (meshes: name -> [kind, texture, name]).
/// Kinds: head (head + body in one mesh, split by the head's weights), headonly, body, eye, teeth, hair, cutout (a
/// see-through gear piece), gear, drop. A texture is a file in tex_dir, or a base name: base_Alb / base_Ab (.tga/.png)
/// as colour and base_Nm as normals.
/// </summary>
public static class FbxBridge
{
    static readonly Regex RageJaw = new(@"(^J_Jaw$|LipLower|CornerInteriorLower|LipCornerSkinLower|Chin|Tongue)"), RageUpper = new(@"(LipUpper|CornerInteriorUpper|LipCornerSkinUpper)");
    static readonly Regex Rdr1Jaw = new(@"(^J_Jaw$|lowerLip|chin_c|tongue)"), Rdr1Upper = new(@"upperLip");

    /// <summary>RDR2 (RAGE) bone names -> 2K's</summary>
    static string? Rage(string n)
    {
        var c = new Dictionary<string, string>
        {
            ["SKEL_Pelvis"] = "J_Hips", ["SKEL_Spine_Root"] = "H_Gut_Jig1", ["SKEL_Spine0"] = "H_Gut_Jig0", ["SKEL_Spine1"] = "J_Spine1", ["SKEL_Spine3"] = "J_Spine2", ["SKEL_Spine5"] = "J_Chest",
            ["SKEL_Neck0"] = "J_Neck", ["SKEL_Head"] = "J_Head", ["FACIAL_C_Jaw"] = "J_Jaw",
        };
        if (c.TryGetValue(n, out var r)) return r;
        var m = Regex.Match(n, @"^(SKEL|FACIAL|RB)_([LR])_(\w+)$");
        if (!m.Success) return null;
        string s = m.Groups[2].Value, b = m.Groups[3].Value;
        string? t = (m.Groups[1].Value, b) switch
        {
            ("FACIAL", "Eyeball") => "J_Eye", ("SKEL", "Clavicle") => "J_Clavicle", ("SKEL", "UpperArm") => "J_Shoulder", ("SKEL", "Forearm") => "J_Elbow",
            ("SKEL", "Hand") => "J_Wrist", ("SKEL", "Thigh") => "J_Leg", ("SKEL", "Calf") => "J_Knee", ("SKEL", "Foot") => "J_Foot", ("SKEL", "Toe0") => "J_Toe",
            ("RB", "ForeArmRoll") => $"H_Elbow_{s}_tw02", _ => null,
        };
        if (t != null) return t.StartsWith("H_") ? t : $"{t}_{s}";
        var f = Regex.Match(b, @"^Finger(\d)(\d)$");
        if (m.Groups[1].Value == "SKEL" && f.Success)
        {
            int fi = int.Parse(f.Groups[1].Value), k = int.Parse(f.Groups[2].Value);
            string fn = new[] { "Thumb", "Index", "Middle", "Ring", "Pinky" }[fi];
            if (fi == 0) return k <= 2 ? $"J_ThumbF{k + 1}_{s}" : null;
            return k == 0 ? $"J_{fn}F0_{s}" : $"J_{fn}F{k}_{s}";
        }
        return null;
    }

    /// <summary>Red Dead Redemption 1 bone names -> 2K's</summary>
    static string? Rdr1(string n)
    {
        var c = new Dictionary<string, string>
        {
            ["pelvis"] = "J_Hips", ["spine00"] = "H_Gut_Jig0", ["spine01"] = "J_Spine1", ["spine02"] = "J_Spine2", ["spine03"] = "J_Chest", ["neck"] = "J_Neck", ["head"] = "J_Head", ["jaw_c"] = "J_Jaw",
        };
        if (c.TryGetValue(n, out var r)) return r;
        var m = Regex.Match(n, @"^(\w+?)_([lr])$");
        if (!m.Success) return null;
        string s = m.Groups[2].Value.ToUpperInvariant(), b = m.Groups[1].Value;
        string? t = b switch
        {
            "eyePivot" => "J_Eye", "clavicle" => "J_Clavicle", "arm" => "J_Shoulder", "elbow" => "J_Elbow", "wrist" => "J_Wrist",
            "hip" => "J_Leg", "knee" => "J_Knee", "ankle" => "J_Foot", "ball" => "J_Toe", "wristroll" => $"H_Elbow_{s}_tw01", _ => null,
        };
        if (t != null) return t.StartsWith("H_") ? t : $"{t}_{s}";
        var th = Regex.Match(b, @"^thumb_0([123])$");
        if (th.Success) return $"J_ThumbF{th.Groups[1].Value}_{s}";
        var f = Regex.Match(b, @"^finger_([1-4])([123])$");
        if (f.Success) return $"J_{new[] { "", "Index", "Middle", "Ring", "Pinky" }[int.Parse(f.Groups[1].Value)]}F{f.Groups[2].Value}_{s}";
        return null;
    }

    static double[] Pos(SourceAew.Bone b) => [b.World[12], b.World[13], b.World[14]];
    static double[] Lerp(double[] a, double[] b, double t) => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];

    static string? FindFile(string dir, string name)
    {
        foreach (var ext in new[] { "", ".tga", ".png", ".dds" })
        {
            var p = Path.Combine(dir, name + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>the colour / normal maps for a texture entry</summary>
    static (Img? Col, Img? Nrm) Textures(string dir, string tex)
    {
        string? col = Path.HasExtension(tex) ? FindFile(dir, tex) : FindFile(dir, tex + "_Alb") ?? FindFile(dir, tex + "_Ab");
        string? nrm = Path.HasExtension(tex) ? null : FindFile(dir, tex + "_Nm");
        Img? c = col != null ? ImageIO.Load(col).ToRgba() : null;
        Img? n = nrm != null ? ImageIO.Load(nrm).ToRgba() : null;
        if (n != null && n.W < 64) n = null;
        return (c, n);
    }

    static (double R, double G, double B) Median(Img im)
    {
        var o = new double[3];
        int step = Math.Max(1, im.W * im.H / 100000);
        for (int c = 0; c < 3; c++)
        {
            var v = new List<int>();
            for (int i = 0; i < im.W * im.H; i += step) if (im.Px[4 * i + 3] > 128) v.Add(im.Px[4 * i + c]);
            v.Sort();
            o[c] = v.Count > 0 ? v[v.Count / 2] : 128;
        }
        return (o[0], o[1], o[2]);
    }

    public static AewBridge Load(JsonObject cfg, string work, Action<string> log)
    {
        string fbx = cfg["fbx"]!.GetValue<string>();
        string texDir = cfg["tex_dir"]?.GetValue<string>() ?? Path.GetDirectoryName(fbx)!;
        string rig = cfg["rig"]?.GetValue<string>() ?? "rage";
        string name = cfg["name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(fbx);
        log($"reading the FBX {Path.GetFileName(fbx)}...");
        var sc = SourceFbx.Load(fbx);
        Func<string, string?> map = rig == "rdr1" ? Rdr1 : Rage;

        // bones: 2K names where known, the rest kept (x_ so no 2K rule takes them)
        var bones = new List<SourceAew.Bone>();
        var used = new HashSet<string>();
        foreach (var b in sc.Bones)
        {
            string? t = map(b.Name);
            if (t != null && !used.Add(t)) t = null;
            bones.Add(new SourceAew.Bone { Name = t ?? "x_" + b.Name, Parent = b.Parent, World = (double[])b.World.Clone() });
        }
        int Idx(string n) => bones.FindIndex(b => b.Name == n);
        foreach (var need in new[] { "J_Hips", "J_Head", "J_Neck", "J_Leg_L", "J_Leg_R", "J_Wrist_L", "J_Wrist_R" })
            if (Idx(need) < 0) throw new InvalidDataException($"this FBX's skeleton is not one PortIT knows (no bone for {need})");

        // size: eye height to a man's (170 cm) when the file's units are off; feet on the floor
        double floor = sc.Meshes.SelectMany(m => Enumerable.Range(0, m.Count).Select(i => m.V[3 * i + 1])).DefaultIfEmpty(0).Min();
        int el = Idx("J_Eye_L");
        double eyeY = el >= 0 ? bones[el].World[13] : bones[Idx("J_Head")].World[13] + 6;
        double scale = eyeY - floor is > 140 and < 200 ? 1.0 : 170.0 / (eyeY - floor);
        if (scale != 1.0) log($"FBX: scaled x{scale:F2} (eye height {eyeY - floor:F1} -> 170 cm)");
        foreach (var b in bones) { b.World[12] *= scale; b.World[13] = (b.World[13] - floor) * scale; b.World[14] *= scale; }
        foreach (var m in sc.Meshes) for (int i = 0; i < m.Count; i++) { m.V[3 * i] *= scale; m.V[3 * i + 1] = (m.V[3 * i + 1] - floor) * scale; m.V[3 * i + 2] *= scale; }

        SeatHats(sc, (JsonObject)cfg["meshes"]!, eyeY: (el >= 0 ? bones[el].World[13] : bones[Idx("J_Head")].World[13] + 6 * scale), log);

        // bones 2K has that this skeleton lacks: a neck twist, both forearm twists, the finger tips
        void AddBone(string n, int parent, double[] p)
        {
            var w = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, p[0], p[1], p[2], 1 };
            bones.Add(new SourceAew.Bone { Name = n, Parent = parent, World = w });
        }
        if (Idx("H_Neck_tw") < 0) AddBone("H_Neck_tw", Idx("J_Neck"), Lerp(Pos(bones[Idx("J_Neck")]), Pos(bones[Idx("J_Head")]), 0.5));
        foreach (var s in new[] { "L", "R" })
        {
            var e = Pos(bones[Idx("J_Elbow_" + s)]); var wr = Pos(bones[Idx("J_Wrist_" + s)]);
            if (Idx($"H_Elbow_{s}_tw01") < 0) AddBone($"H_Elbow_{s}_tw01", Idx("J_Elbow_" + s), Lerp(e, wr, 1 / 3.0));
            if (Idx($"H_Elbow_{s}_tw02") < 0) AddBone($"H_Elbow_{s}_tw02", Idx("J_Elbow_" + s), Lerp(e, wr, 2 / 3.0));
            foreach (var f in new[] { "Thumb", "Index", "Middle", "Ring", "Pinky" })
            {
                int f2 = Idx($"J_{f}F2_{s}"), f3 = Idx($"J_{f}F3_{s}");
                if (f2 < 0 || f3 < 0) throw new InvalidDataException($"this FBX's hand has no {f} bones");
                var a = Pos(bones[f2]); var b = Pos(bones[f3]);
                AddBone($"J_{f}F3_{s}_stub", f3, Lerp(a, b, 1.8));
            }
        }

        // meshes
        var cm = cfg["meshes"]!.AsObject();
        var model = new SourceAew.Model { Game = "fbx", Name = Regex.Replace(name, @"[^\w]+", "_"), Bones = bones, Female = cfg["female"]?.GetValue<bool>() ?? false };
        model.Attire = model.Name; model.Attires = [model.Name];
        var skinCol = new List<(SourceAew.SourceMesh M, string Kind)>();
        foreach (var m in sc.Meshes)
        {
            if (cm[m.Name] is not JsonArray e) throw new InvalidDataException($"FBX: no kind / texture given for the mesh {m.Name}");
            string kind = e[0]!.GetValue<string>();
            if (kind == "drop") { log($"FBX: {m.Name} left out"); continue; }
            string tex = e.Count > 1 ? e[1]!.GetValue<string>() : "";
            string slot = e.Count > 2 ? e[2]!.GetValue<string>() : Regex.Replace(Path.GetFileNameWithoutExtension(tex), @"^[A-Za-z]+_", "");
            var (col, nrm) = tex.Length > 0 ? Textures(texDir, tex) : (null, null);
            if (tex.Length > 0 && col == null) throw new InvalidDataException($"FBX: no colour texture found for {tex}");
            if (kind == "eye" && col != null)
            {
                // an iris on its own (RDR2's eyes): laid over an eye's white
                var o = new Img(col.W, col.H, 4);
                for (int i = 0; i < col.W * col.H; i++)
                {
                    double a = col.Px[4 * i + 3] / 255.0;
                    byte[] white = [226, 218, 212];
                    for (int c = 0; c < 3; c++) o.Px[4 * i + c] = (byte)(col.Px[4 * i + c] * a + white[c] * (1 - a));
                    o.Px[4 * i + 3] = 255;
                }
                col = o;
            }
            bool cut = kind is "hair" or "cutout";
            if (!cut && col != null) for (int i = 0; i < col.W * col.H; i++) col.Px[4 * i + 3] = 255;
            // uv: the tile it sits in moved to 0-1 (RDR1's v from -1), then v down as AEW's
            int n = m.Count;
            var uv = (double[])m.Uv.Clone();
            for (int k = 0; k < 2; k++)
            {
                double lo = Enumerable.Range(0, n).Min(i => uv[2 * i + k]), hi = Enumerable.Range(0, n).Max(i => uv[2 * i + k]);
                double t = Math.Floor(lo + 1e-4);
                if (hi - t <= 1.0001 && t != 0) for (int i = 0; i < n; i++) uv[2 * i + k] -= t;
            }
            for (int i = 0; i < n; i++) uv[2 * i + 1] = 1.0 - uv[2 * i + 1];
            // weights: the 8 biggest per vertex
            const int MaxInf = 8;
            var bi = new long[n * MaxInf]; var bw = new double[n * MaxInf];
            var keys = m.W.Keys.ToArray();
            for (int i = 0; i < n; i++)
            {
                var top = keys.Select(k => (k, w: m.W[k][i])).Where(x => x.w > 0).OrderByDescending(x => x.w).Take(MaxInf).ToList();
                double s = top.Sum(x => x.w);
                if (s <= 0) { bi[i * MaxInf] = Idx("J_Hips"); bw[i * MaxInf] = 1; continue; }
                for (int k = 0; k < top.Count; k++) { bi[i * MaxInf + k] = top[k].k; bw[i * MaxInf + k] = top[k].w / s; }
            }
            // a texture repeated along the piece (belts, straps, a lasso): each triangle moved into the first repeat (the
            //  2K pipeline's texture sheet holds one copy), its corners copied where triangles of two repeats meet
            var V = m.V; var N = m.N; var T = m.Tris;
            if (uv.Any(x => x < -0.01 || x > 1.01))
            {
                var nv = new List<int>(); var nuv = new List<double>(); var key = new Dictionary<(int, int, int), int>(); var nt = new int[T.Length];
                for (int t = 0; t < T.Length; t += 3)
                {
                    double cu = 0, cv = 0;
                    for (int k = 0; k < 3; k++) { cu += uv[2 * T[t + k]] / 3; cv += uv[2 * T[t + k] + 1] / 3; }
                    int su = (int)Math.Floor(cu), sv = (int)Math.Floor(cv);
                    for (int k = 0; k < 3; k++)
                    {
                        int v = T[t + k];
                        if (!key.TryGetValue((v, su, sv), out int id)) { id = nv.Count; key[(v, su, sv)] = id; nv.Add(v); nuv.Add(uv[2 * v] - su); nuv.Add(uv[2 * v + 1] - sv); }
                        nt[t + k] = id;
                    }
                }
                var V2 = new double[3 * nv.Count]; var N2 = new double[3 * nv.Count]; var bi2 = new long[nv.Count * MaxInf]; var bw2 = new double[nv.Count * MaxInf];
                for (int i = 0; i < nv.Count; i++)
                {
                    Array.Copy(V, 3 * nv[i], V2, 3 * i, 3); Array.Copy(N, 3 * nv[i], N2, 3 * i, 3);
                    Array.Copy(bi, MaxInf * nv[i], bi2, MaxInf * i, MaxInf); Array.Copy(bw, MaxInf * nv[i], bw2, MaxInf * i, MaxInf);
                }
                log($"FBX: {m.Name} repeats its texture (uv {uv.Where((_, i) => i % 2 == 0).Min():F1}..{uv.Where((_, i) => i % 2 == 0).Max():F1}) - folded into one repeat, {nv.Count - n} corners copied");
                V = V2; N = N2; T = nt; uv = [.. nuv]; bi = bi2; bw = bw2;
            }
            var parts = new List<(int[] Tris, string Kind, string Slot)> { (T, kind, slot) };
            if (kind == "head" && col != null && e.Count > 3 && e[3]!.GetValue<string>() == "split_alpha")
            {
                // the see-through bits of a one-texture model (lashes, stray hair) -> their own cut-out piece
                var solid = new List<int>(); var see = new List<int>();
                for (int t = 0; t < T.Length; t += 3)
                {
                    int low = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        int v = T[t + k];
                        double u = uv[2 * v] - Math.Floor(uv[2 * v]), vv = uv[2 * v + 1] - Math.Floor(uv[2 * v + 1]);
                        int x = Math.Clamp((int)(u * col.W), 0, col.W - 1), y = Math.Clamp((int)(vv * col.H), 0, col.H - 1);
                        if (Alpha(texDir, tex, x, y) < 200) low++;
                    }
                    (low >= 2 ? see : solid).AddRange(T[t..(t + 3)]);
                }
                parts = [([.. solid], "head", slot)];
                if (see.Count > 0) { parts.Add(([.. see], "cutout", slot + "_bits")); log($"FBX: {see.Count / 3} see-through triangles of {m.Name} -> their own cut-out piece"); }
            }
            foreach (var (tris, k2, sl) in parts)
            {
                var sm = new SourceAew.SourceMesh
                {
                    Name = m.Name, Material = sl, Kind = k2 == "cutout" ? "other" : k2, Cutout = k2 is "hair" or "cutout", Source = model.Name, MaxInf = MaxInf,
                    Positions = (double[])V.Clone(), Normals = (double[])N.Clone(), Uvs = uv, Tris = tris, BoneIdx = bi, BoneW = bw,
                };
                var c2 = col;
                if (k2 == "cutout" && kind == "head") c2 = AlphaImg(texDir, tex);
                if (c2 != null) sm.Images["color"] = c2;
                if (nrm != null) sm.Images["normal"] = nrm;
                model.Meshes.Add(sm);
                if (k2 is "head" or "headonly" or "body") skinCol.Add((sm, k2));
            }
        }
        // the body's skin tinted to the head's (RDR2 shares a pale hand / torso texture the game tints)
        var hd = skinCol.FirstOrDefault(x => x.Kind is "head" or "headonly");
        foreach (var (sm, k) in skinCol.Where(x => x.Kind == "body"))
            if (hd.M != null && hd.M.Images.TryGetValue("color", out var hc) && sm.Images.TryGetValue("color", out var bc) && cfg["match_skin"]?.GetValue<bool>() != false)
            {
                var a = Median(hc); var b = Median(bc);
                double[] f = [Math.Clamp(a.R / b.R, 0.4, 2.5), Math.Clamp(a.G / b.G, 0.4, 2.5), Math.Clamp(a.B / b.B, 0.4, 2.5)];
                var o = new Img(bc.W, bc.H, 4);
                for (int i = 0; i < bc.W * bc.H; i++)
                {
                    for (int c = 0; c < 3; c++) o.Px[4 * i + c] = (byte)Math.Clamp(bc.Px[4 * i + c] * f[c], 0, 255);
                    o.Px[4 * i + 3] = bc.Px[4 * i + 3];
                }
                sm.Images["color"] = o;
                log($"FBX: body skin tinted to the head's (x{f[0]:F2} {f[1]:F2} {f[2]:F2})");
            }
        return new AewBridge(model, work, rig == "rdr1" ? Rdr1Jaw : RageJaw, rig == "rdr1" ? Rdr1Upper : RageUpper, "FBX", log);
    }

    /// <summary>
    /// hats (kind "hat"): RDR2 keeps the hair full in its rest pose and lifts the hat over it (the game squashes the
    /// hair when one is worn). The hat lowered until it rests on the skull (HatGapCm above it), the hair under the hat
    /// pressed down below the hat's inside (HairUnderCm under it).
    /// </summary>
    const double Cell = 1.0, HatGapCm = 0.6, HairUnderCm = 0.4;
    static void SeatHats(SourceFbx.Scene sc, JsonObject cm, double eyeY, Action<string> log)
    {
        string Kind(SourceFbx.Mesh m) => cm[m.Name] is JsonArray e ? e[0]!.GetValue<string>() : "";
        var hats = sc.Meshes.Where(m => Kind(m) == "hat").ToList();
        if (hats.Count == 0) return;
        (int, int) C(double x, double z) => ((int)Math.Floor(x / Cell), (int)Math.Floor(z / Cell));
        // the skull's top per cell (the head's own surface above the eyes)
        var skull = new Dictionary<(int, int), double>();
        foreach (var m in sc.Meshes.Where(m => Kind(m) is "head" or "headonly"))
            for (int i = 0; i < m.Count; i++)
            {
                double y = m.V[3 * i + 1];
                if (y < eyeY) continue;
                var k = C(m.V[3 * i], m.V[3 * i + 2]);
                skull[k] = Math.Max(skull.GetValueOrDefault(k, double.NegativeInfinity), y);
            }
        if (skull.Count == 0) return;
        // the drop: down in 2 mm steps while no more than 1% of the skull above the eyes would come through the hat
        //  (a hat sits snug: its band squeezes the head's sides a little, as a worn one does)
        var skullPts = new List<(double X, double Y, double Z)>();
        foreach (var m in sc.Meshes.Where(m => Kind(m) is "head" or "headonly"))
            for (int i = 0; i < m.Count; i++) if (m.V[3 * i + 1] >= eyeY) skullPts.Add((m.V[3 * i], m.V[3 * i + 1], m.V[3 * i + 2]));
        var hatLow0 = new Dictionary<(int, int), double>();
        foreach (var h in hats)
            for (int i = 0; i < h.Count; i++)
            {
                var k = C(h.V[3 * i], h.V[3 * i + 2]);
                hatLow0[k] = Math.Min(hatLow0.GetValueOrDefault(k, double.PositiveInfinity), h.V[3 * i + 1]);
            }
        int Through(double d) => skullPts.Count(p => hatLow0.TryGetValue(C(p.X, p.Z), out double l) && p.Y + HatGapCm > l - d);
        int allow = Math.Max(1, skullPts.Count / 100), start = Through(0);
        double drop = 0;
        while (drop < 15 && Through(drop + 0.2) <= Math.Max(allow, start)) drop += 0.2;
        if (drop <= 0) return;
        foreach (var h in hats) for (int i = 0; i < h.Count; i++) h.V[3 * i + 1] -= drop;
        log($"FBX: hat lowered {drop:F1} cm onto the head ({Through(drop)} of {skullPts.Count} skull points above the eyes touch it)");
        // the hat's inside per cell (its lowest surface), widened a cell so hair at the band's edge goes under too
        var low = new Dictionary<(int, int), double>();
        foreach (var h in hats)
            for (int i = 0; i < h.Count; i++)
            {
                var (cx, cz) = C(h.V[3 * i], h.V[3 * i + 2]);
                low[(cx, cz)] = Math.Min(low.GetValueOrDefault((cx, cz), double.PositiveInfinity), h.V[3 * i + 1]);
            }
        int pressed = 0;
        foreach (var m in sc.Meshes.Where(m => Kind(m) is "hair"))
            for (int i = 0; i < m.Count; i++)
            {
                var (cx, cz) = C(m.V[3 * i], m.V[3 * i + 2]);
                double lim = double.PositiveInfinity;
                for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++) if (low.TryGetValue((cx + dx, cz + dz), out double l)) lim = Math.Min(lim, l);
                if (double.IsPositiveInfinity(lim)) continue;
                // (keep the hair above the skull where the hat's inside is lower than that)
                double floorY = skull.TryGetValue((cx, cz), out double s) ? s + 0.1 : double.NegativeInfinity;
                double target = Math.Max(lim - HairUnderCm, floorY);
                if (m.V[3 * i + 1] > target) { m.V[3 * i + 1] = target; pressed++; }
            }
        if (pressed > 0) log($"FBX: {pressed} hair points pressed under the hat");
    }

    static readonly Dictionary<string, Img> alphaCache = [];
    static Img AlphaImg(string dir, string tex)
    {
        string p = FindFile(dir, tex)!;
        if (!alphaCache.TryGetValue(p, out var im)) alphaCache[p] = im = ImageIO.Load(p).ToRgba();
        return im;
    }
    static int Alpha(string dir, string tex, int x, int y) { var im = AlphaImg(dir, tex); return im.Px[4 * (y * im.W + x) + 3]; }
}
