using System.Text.RegularExpressions;
using PortIT.Engine.Port;

namespace PortIT.Engine.Readers;

/// <summary>
/// portit/source_2k.py (find_wrestler, load_mcd, load): a 2K mod folder (2K20 - 2K26 .mcd / .mdl, or an unpacked
/// 2K19 - 2K22 pack) -&gt; its base model's and one attire's meshes, each with its textures (the face paint and an
/// attire's own base textures laid on, as the port does) - for the inspect list and the source preview.
/// </summary>
public static class Source2k
{
    static readonly Dictionary<int, string> ShaderKind = new()
    {
        [0x12] = "eye", [0x13] = "caruncle", [0x14] = "lash", [0x15] = "gear", [0x16] = "hair", [0x17] = "head", [0x18] = "skin",
        [0x19] = "skin", [0x1A] = "teeth", [0x1B] = "hair", [0x1C] = "hair", [0x1D] = "hair",
    };

    public static bool IsBaseModel(string name) => name.ToLowerInvariant() is "basemodel.mcd" or "basemodel.mdl";

    /// <summary>-&gt; (base model path, [attire model paths]); hair_&lt;n&gt;.mdl (a 2K19-era hair slot) is no attire</summary>
    public static (string? Base, List<string> Attires) FindWrestler(string folder)
    {
        var all = GlobModels(folder);     // (glob('**/*') in its order, both extensions)
        var bas = all.Where(p => IsBaseModel(Path.GetFileName(p))).ToList();
        var att = all.Where(p => !bas.Contains(p) && !p.ToLowerInvariant().Contains(Path.DirectorySeparatorChar + "textures" + Path.DirectorySeparatorChar)
                                 && !Path.GetFileName(p).ToLowerInvariant().StartsWith("hair_"))
                     .OrderBy(p => p, StringComparer.Ordinal).ToList();
        return (bas.Count > 0 ? bas[0] : null, att);
    }

    static List<string> GlobModels(string folder)
    {
        var o = new List<string>();
        if (!Directory.Exists(folder)) return o;
        var dirs = new List<string> { folder };
        void Walk(string d)
        {
            foreach (var s in Directory.EnumerateDirectories(d))
                if (!Path.GetFileName(s).StartsWith('.')) { dirs.Add(s); Walk(s); }
        }
        Walk(folder);
        foreach (var d in dirs)
            foreach (var f in Directory.EnumerateFiles(d))
                if ((f.EndsWith(".mcd", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                    && !Path.GetFileName(f).StartsWith('.')) o.Add(f);
        return o;
    }

    public static (List<SourceAew.Bone> Bones, List<SourceAew.SourceMesh> Meshes) LoadMcd(string path, Source2kTextures tex)
    {
        var m = Mcd.Load(path);
        string mdir = Path.GetDirectoryName(path)!;
        var objects = m.Objects.Select(o => Regex.Replace(o.Name, "(:skinned)?$", "", RegexOptions.IgnoreCase)).ToList();
        var jmtls = new Dictionary<string, string>();
        var jorder = new List<string>();
        string mats = Path.Combine(mdir, "Materials");
        if (Directory.Exists(mats))
            foreach (var p in Directory.EnumerateFiles(mats, "*.jmtl"))
            {
                string k = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                if (!jmtls.ContainsKey(k)) jorder.Add(k);
                jmtls[k] = p;
            }
        var meshes = new List<SourceAew.SourceMesh>();
        for (int k = 0; k < m.Meshes.Count; k++)
        {
            var mb = m.Meshes[k];
            string key = Regex.Replace(mb.Material.ToLowerInvariant(), "^m_", "");
            string Un(string s) => Regex.Replace(s, "_alpha$", "");
            var cand = jorder.Where(s => Un(s) == key).ToList();
            if (cand.Count == 0) cand = jorder.Where(s => s.StartsWith(key) || key.EndsWith(Un(s))).ToList();
            if (cand.Count == 0) cand = jorder.Where(s => key.Contains(Un(s))).ToList();
            string jstem = cand.Count > 0 ? cand.OrderBy(s => s.Length).First() : (string.IsNullOrEmpty(mb.Object) ? key : mb.Object).ToLowerInvariant();
            string obj = objects.FirstOrDefault(o => o.ToLowerInvariant() == jstem) ?? jstem;
            string stem = Un(jstem);
            string jm = jmtls.TryGetValue(jstem, out var jp) ? jp : Path.Combine(mdir, "Materials", jstem + ".jmtl");
            int? shader = File.Exists(jm) ? Source2kTextures.JmtlInfo(jm).Shader : null;
            string mat = mb.Material.ToLowerInvariant();
            string kind = shader != null && ShaderKind.TryGetValue(shader.Value, out var sk) ? sk
                : mat.Contains("eye") && !mat.Contains("lash") ? "eye" : mat.Contains("lash") ? "lash" : mat.Contains("hair") ? "hair"
                : mat == "m_head" ? "head" : mat == "m_body" ? "skin" : "gear";
            bool alphaObj = obj.ToLowerInvariant().Split(':')[0].EndsWith("_alpha");
            var pos = m.StreamOf(k, "POSITION")!; var nrm = m.StreamOf(k, "NORMAL")!;
            int n = pos.N;
            var me = new SourceAew.SourceMesh
            {
                Name = stem, Material = mb.Material, Kind = kind, Cutout = alphaObj || kind is "hair" or "lash", SourcePath = path, Index = k,
                Positions = Cols(pos, 3), Normals = Cols(nrm, 3), Tris = m.Lods.Count > 0 ? m.Lods[0][k].Triangles : [],
                MaxInf = mb.Influences, BoneIdx = mb.SkinBones.Select(x => (long)x).ToArray(), BoneW = mb.SkinWeights,
                Textures = tex.ForMaterial(jm, stem, mdir, key),
            };
            var uv = mb.Streams.FirstOrDefault(s => s.Sem == "TEXCOORD");
            me.Uvs = uv != null ? Cols(uv, 2) : new double[2 * mb.Vertices];
            var col = m.StreamOf(k, "COLOR");
            if (col != null) { me.Colors = col.V.Select(x => x / 255.0).ToArray(); me.ColorC = col.C; }
            meshes.Add(me);
        }
        var names = meshes.Select(x => x.Material.ToLowerInvariant()).ToHashSet();
        foreach (var me in meshes)
        {
            string n = me.Material.ToLowerInvariant();
            me.Variant = names.Any(o => n != o && n.StartsWith(o + "_"));
            if (n.StartsWith("s_") && names.Contains("m_" + n[2..])) me.Variant = true;
            if (n.Contains("eyeshadow") || new[] { "m_lambert", "m_polysurface", "lambert", "polysurface" }.Any(n.StartsWith)) me.Variant = true;
        }
        var bones = m.Bones.Select(b => new SourceAew.Bone
        {
            Name = b.Name, Parent = b.Parent,
            World = Enumerable.Range(0, 16).Select(i => b.World[i / 4, i % 4]).ToArray(),
        }).ToList();
        return (bones, meshes);
    }

    static double[] Cols(Mcd.Stream s, int k)
    {
        int n = s.N;
        var o = new double[n * k];
        for (int i = 0; i < n; i++) for (int c = 0; c < k; c++) o[k * i + c] = s.V[s.C * i + c];
        return o;
    }

    /// <summary>-&gt; the wrestler: base model meshes + one attire's (the first when none is named), the face paint and the
    /// attire's own base textures swapped in (previews\_facepaint)</summary>
    public static SourceAew.Model Load(string folder, string? attire = null, string? previewRoot = null)
    {
        var (bas, attires) = FindWrestler(folder);
        if (bas == null) throw new InvalidDataException($"No BaseModel/baseModel.mcd (or .mdl) under {folder}");
        var tex = new Source2kTextures(folder);
        var (bones, meshes) = LoadMcd(bas, tex);
        string? att = null;
        if (attires.Count > 0)
        {
            att = attire ?? attires[0];
            var (_, am) = LoadMcd(att, tex);
            meshes.AddRange(am);
        }
        if (att != null)
        {
            List<string> first;
            try
            {
                string root = previewRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortIT", "previews");
                string outDir = Path.Combine(root, "_facepaint", Regex.Replace(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), @"[^A-Za-z0-9_.-]+", "_"));
                first = Port2k.FacePaint(folder, Path.GetRelativePath(folder, Path.GetDirectoryName(att)!), outDir, _ => { }, normals: false);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { first = []; }
            if (first.Count > 0)
            {
                string? Swap(string pathOrStem)
                {
                    string stem = Path.GetFileNameWithoutExtension(pathOrStem).ToLowerInvariant();
                    foreach (var d in first)
                        foreach (var f in Directory.EnumerateFileSystemEntries(d))
                            if (Path.GetFileNameWithoutExtension(f).ToLowerInvariant() == stem) return f;
                    return null;
                }
                foreach (var me in meshes)
                {
                    var tx = new Dictionary<string, string>(me.Textures);
                    foreach (var (role, p) in tx.ToList()) tx[role] = Swap(p) ?? p;
                    if (!tx.ContainsKey("color") && Swap(me.Name + "_color") is string sc) tx["color"] = sc;
                    me.Textures = tx;
                }
            }
        }
        return new SourceAew.Model
        {
            Game = "2k26", Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), Bones = bones, Meshes = meshes, Attire = att, Attires = attires,
            Female = folder.ToLowerInvariant().Contains("female") || Mcd.LooksFemale(bas),
        };
    }
}
