using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PortIT.Engine.Formats;

namespace PortIT.Engine.Port;

/// <summary>
/// portit/attires.py: a port as an attire mod of a superstar the game has (the Mod Maker's Attires page): the
/// superstars, their own attires (model pacs' EMD "%06d%d2"), entrance gear, attire mods already installed; the
/// install (Mods/Attires/&lt;id&gt;: manifest.txt + attire.pac) with the game's rules.
/// </summary>
public static class Attires
{
    /// <summary>the entry names of an EPAC / EPK8 pac from its header only: [(group, name)]</summary>
    static List<(string Group, string Name)> PacNames(string path)
    {
        var o = new List<(string, string)>();
        byte[] d;
        try
        {
            using var f = File.OpenRead(path);
            d = new byte[0x4000];
            int n = 0, r;
            while (n < d.Length && (r = f.Read(d, n, d.Length - n)) > 0) n += r;
            Array.Resize(ref d, n);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return o; }
        if (d.Length < 4) return o;
        string magic = Encoding.Latin1.GetString(d, 0, 4);
        if (magic is not ("EPK8" or "EPAC")) return o;
        int p = 0x800;
        while (p + 12 <= 0x4000 && p + 12 <= d.Length)
        {
            if (d[p] == 0 && d[p + 1] == 0 && d[p + 2] == 0 && d[p + 3] == 0) break;
            string typ = Encoding.Latin1.GetString(d, p, 4);
            if (magic == "EPK8")
            {
                int cnt = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 4));
                p += 12;
                for (int i = 0; i < cnt; i++) { o.Add((typ, Encoding.Latin1.GetString(d, p, Math.Min(8, Math.Max(0, d.Length - p))))); p += 16; }
            }
            else
            {
                int cnt = (int)(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 4)) & 0xFFFF) / 3;
                p += 12;
                for (int i = 0; i < cnt; i++) { o.Add((typ, Encoding.Latin1.GetString(d, p, Math.Min(4, Math.Max(0, d.Length - p))))); p += 12; }
            }
        }
        return o;
    }

    /// <summary>chEtc.pac CHAR / DAT* records: id -&gt; the superstar's full name</summary>
    static void CharNames(string chetc, Dictionary<long, string> names)
    {
        Epac.Archive arc;
        try { arc = Epac.Read(File.ReadAllBytes(chetc)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { return; }
        foreach (var g in arc.Groups)
        {
            if (Encoding.Latin1.GetString(g.Type) != "CHAR") continue;
            foreach (var e in g.Entries)
            {
                if (!e.NameText.StartsWith("DAT")) continue;
                try
                {
                    foreach (var x in Pach.Read(e.Data))
                    {
                        var r = Bpe.Unpack(x.Data);
                        if (r.Length >= 4 + 66)
                        {
                            int z = Array.IndexOf(r, (byte)0, 38, 32);
                            names[x.Id] = Encoding.Latin1.GetString(r, 38, (z < 0 ? 70 : z) - 38).Trim();
                        }
                    }
                }
                catch (Exception) { }
            }
        }
    }

    static string DlcRoot(string game) => Path.Combine(game, "UserData", "0000000000000000", "5451085D", "00000002");

    public static Dictionary<string, string> Manifest(string path)
    {
        var m = new Dictionary<string, string>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq >= 0) m[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return m;
    }

    static IEnumerable<string> SortedNames(string dir) => Directory.EnumerateFileSystemEntries(dir).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal);

    static SortedDictionary<int, string> ModAttires(string game, long sid)
    {
        string root = Path.Combine(game, "Mods", "Attires");
        var o = new SortedDictionary<int, string>();
        var seen = new Dictionary<int, string>();
        if (!Directory.Exists(root)) return o;
        foreach (var folder in SortedNames(root))
        {
            string d = Path.Combine(root, folder);
            if (!Directory.Exists(d) || File.Exists(Path.Combine(d, "disabled")) || Directory.Exists(Path.Combine(d, "disabled"))) continue;
            var m = Manifest(Path.Combine(d, "manifest.txt"));
            if (m.GetValueOrDefault("type") != "attire" || m.GetValueOrDefault("superstar") != sid.ToString()) continue;
            if (!int.TryParse(m.GetValueOrDefault("attire", ""), out int a) || m.GetValueOrDefault("attire", "").Trim() != m.GetValueOrDefault("attire", "")) continue;
            if (a >= 1 && a <= 4 && !o.ContainsKey(a)) o[a] = string.IsNullOrEmpty(m.GetValueOrDefault("name")) ? folder : m["name"];
        }
        return o;
    }

    public sealed record Star(long Id, string Name, bool Dlc, List<int> AttireList, bool Gear, SortedDictionary<int, string> Mods);

    public static List<Star> Stars(string game)
    {
        var names = new Dictionary<long, string>();
        CharNames(Path.Combine(game, "pac", "ch", "chEtc.pac"), names);
        var ids = new HashSet<long>();
        foreach (var (typ, name) in PacNames(Path.Combine(game, "pac", "DLC_HD.pac")))
            if (typ == "SSFA" && long.TryParse(name, out long i) && Regex.IsMatch(name, @"^\s*[+-]?\d+\s*$") && i >= 100 && i <= 321) ids.Add(i);
        var dlc = new Dictionary<long, List<string>>();
        var dlcNames = new Dictionary<long, string>();
        string root = DlcRoot(game);
        if (Directory.Exists(root))
            foreach (var pkg in SortedNames(root))
            {
                string chdir = Path.Combine(root, pkg, "pac", "ch");
                CharNames(Path.Combine(chdir, "chEtc.pac"), dlcNames);
                if (!Directory.Exists(chdir)) continue;
                foreach (var f in SortedNames(chdir))
                {
                    var m = Regex.Match(f, @"^ch(\d+)_dlc(\d+)\.pac$", RegexOptions.IgnoreCase);
                    if (m.Success && long.Parse(m.Groups[1].Value) is long n && n > 0 && n < 1000)
                    {
                        if (!dlc.TryGetValue(n, out var l)) dlc[n] = l = [];
                        l.Add(Path.Combine(chdir, f));
                    }
                }
            }
        var o = new List<Star>();
        foreach (long sid in ids.Union(dlc.Keys.Where(i => i < 100)).Order())
        {
            var own = new SortedSet<int>(); bool gear = false;
            var pacs = new List<string>();
            if (sid >= 100) pacs.Add(Path.Combine(game, "pac", "ch", $"ch{sid}.pac"));
            pacs.AddRange(dlc.GetValueOrDefault(sid, []));
            foreach (var pac in pacs)
                foreach (var (typ, n) in PacNames(pac))
                {
                    if (typ != "EMD " || n.Length != 8 || n[..6] != sid.ToString("D6") || !char.IsAsciiDigit(n[6])) continue;
                    if (n[7] == '2' && n[6] - '0' <= 3) own.Add(n[6] - '0' + 1);
                    if (n[6] == '0' && n[7] != '2') gear = true;
                }
            string? name = sid < 100 ? (dlcNames.GetValueOrDefault(sid) is { Length: > 0 } a1 ? a1 : names.GetValueOrDefault(sid))
                                     : (names.GetValueOrDefault(sid) is { Length: > 0 } b1 ? b1 : dlcNames.GetValueOrDefault(sid));
            if (string.IsNullOrEmpty(name)) name = $"Superstar {sid}";
            o.Add(new Star(sid, name + (sid < 100 ? " (DLC)" : ""), sid < 100, [.. own], gear, ModAttires(game, sid)));
        }
        return o.OrderBy(s => s.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList();
    }

    public static JsonArray StarsJson(string game) => new(Stars(game).Select(s => (JsonNode)new JsonObject
    {
        ["id"] = s.Id, ["name"] = s.Name, ["dlc"] = s.Dlc, ["attires"] = new JsonArray(s.AttireList.Select(a => (JsonNode)a).ToArray()),
        ["gear"] = s.Gear, ["mods"] = new JsonObject(s.Mods.Select(kv => KeyValuePair.Create(kv.Key.ToString(), (JsonNode?)kv.Value))),
    }).ToArray());

    static string? ModFolder(string game, long sid, int attire)
    {
        string root = Path.Combine(game, "Mods", "Attires");
        if (!Directory.Exists(root)) return null;
        foreach (var folder in SortedNames(root))
        {
            string d = Path.Combine(root, folder);
            if (!Directory.Exists(d) || File.Exists(Path.Combine(d, "disabled")) || Directory.Exists(Path.Combine(d, "disabled"))) continue;
            var m = Manifest(Path.Combine(d, "manifest.txt"));
            if (m.GetValueOrDefault("type") == "attire" && m.GetValueOrDefault("superstar") == sid.ToString() && m.GetValueOrDefault("attire") == attire.ToString()) return folder;
        }
        return null;
    }

    public static string ModId(string? name, string starName)
    {
        string s = ((string.IsNullOrEmpty(name) ? "attire" : name) + "_" + starName).ToLowerInvariant();
        s = Regex.Replace(s, "[^a-z0-9]+", "_").Trim('_');
        return s.Length > 0 ? s : "attire";
    }

    /// <summary>req: mod_dir, game, star, star_name, slot ('new' or 1-4), name, author, version, entrance. copyPack
    /// installs the HD texture pack.</summary>
    public static JsonObject Install(JsonObject req, Func<string, string, string, JsonObject> copyPack)
    {
        string S(string k, string d = "") => req[k] is JsonNode n ? (n.GetValueKind() == System.Text.Json.JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString()) : d;
        string src = S("mod_dir"), game = S("game");
        string pac = Path.Combine(src, "ch.pac");
        if (!File.Exists(pac)) throw new ArgumentException("the port has no ch.pac - port first");
        using (var f = File.OpenRead(pac)) { var b = new byte[4]; if (f.Read(b, 0, 4) < 4 || Encoding.Latin1.GetString(b) != "EPK8") throw new ArgumentException("ch.pac is not a character model pac"); }
        long sid = long.Parse(S("star"));
        string slot = string.IsNullOrEmpty(S("slot")) || req["slot"] == null ? "new" : S("slot");
        if (slot != "new" && !(slot.Length == 1 && slot[0] >= '1' && slot[0] <= '4')) throw new ArgumentException("attire must be 1-4 or new");
        string mid = ModId(S("name"), req["star_name"] != null ? S("star_name") : sid.ToString());
        string dest = Path.Combine(game, "Mods", "Attires", mid);
        var warnings = new JsonArray();
        var o = new JsonObject { ["id"] = mid, ["warnings"] = warnings };
        var star = Stars(game).FirstOrDefault(x => x.Id == sid) ?? throw new ArgumentException($"superstar {sid} is not in this game");
        var taken = star.Mods;
        if (slot == "new")
        {
            if (star.Gear)
                throw new ArgumentException($"{star.Name} wears entrance gear: new attires for them do not load in the game - replace attire {(star.AttireList.Count > 1 ? $"1-{star.AttireList.Count}" : "1")} instead");
            var ownOrMod = star.AttireList.ToHashSet();
            foreach (var (a, _) in taken) if (ModFolder(game, sid, a) != mid) ownOrMod.Add(a);
            if (Enumerable.Range(1, 4).All(ownOrMod.Contains)) throw new ArgumentException($"{star.Name} has no free attire (4 at most): replace one instead");
        }
        else
        {
            int a = slot[0] - '0';
            if (star.Gear && !star.AttireList.Contains(a))
                throw new ArgumentException($"{star.Name} wears entrance gear: only their own attires ({string.Join(", ", star.AttireList)}) can be replaced");
            if (taken.ContainsKey(a) && ModFolder(game, sid, a) != mid)
                warnings.Add($"attire {a} of {star.Name} is already taken by the attire mod \"{taken[a]}\": the launcher moves the newer one to the next free attire, or asks to switch one off");
        }
        if (Directory.Exists(dest) || File.Exists(dest))
        {
            string bak = Path.Combine(game, "Mods", "_portit_backup", "attire_" + mid + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
            Directory.Move(dest, bak);
            o["backup"] = bak;
        }
        Directory.CreateDirectory(dest);
        var lines = new List<string> { "type=attire", "id=" + mid, $"superstar={sid}", "attire=" + slot, "name=" + S("name"), "model=attire.pac",
                                       "author=" + S("author"), "version=" + (string.IsNullOrEmpty(S("version")) ? "1.0" : S("version")), "made_with=PortIT" };
        string ent = S("entrance").Trim();
        if (ent.Length > 0 && ent.All(char.IsAsciiDigit) && long.Parse(ent) > 0) lines.Add("entrance=" + ent);
        File.WriteAllText(Path.Combine(dest, "manifest.txt"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        File.Copy(pac, Path.Combine(dest, "attire.pac"));
        o["installed"] = dest;
        foreach (var (k, v) in copyPack(src, game, mid)) o[k] = v?.DeepClone();
        return o;
    }
}
