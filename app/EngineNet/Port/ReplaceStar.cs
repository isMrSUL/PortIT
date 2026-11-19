using System.Text;
using System.Text.Json.Nodes;
using PortIT.Engine.Formats;

namespace PortIT.Engine.Port;

/// <summary>
/// Replace a superstar the game already has with a ported model - "update John Cena's model".
///
/// This is NOT an attire mod. An attire mod adds or swaps ONE of a superstar's attires and lives in
/// Mods\Attires. Here the game's own character pac (pac\ch\chNNN.pac) is rewritten, so the superstar
/// looks new in every attire, every menu and every entrance. His name, stats, moves, entrance and
/// menu pictures are untouched - he is still John Cena, with a better model.
///
/// How it works. A character pac is an EPK8 holding EMD entries named "&lt;6-digit id&gt;&lt;attire&gt;&lt;kind&gt;":
/// John Cena (139) ships 00013902 (the ring model), 00013904 (his entrance model) and 00013912 (his
/// second attire). PortIT names the entries it builds after the BASE superstar, so porting with
/// base=139 already produces 00013902 - the exact name the stock pac uses. So the swap is by name:
/// every stock entry stays unless we built one with the same name, and anything we built that the
/// stock pac does not have is added. His second attire therefore survives a replacement.
///
/// Big models are fine here. From game 2.1.6 the model loader takes its memory from the fallback
/// heap whenever the 768 KB per-wrestler window is full (port/src/model_mem.cpp, "allocator 128 full
/// - the block from the fallback memory"); that hook is on the allocator, not on "is this a mod", so
/// a replaced stock superstar gets it too.
/// </summary>
public static class ReplaceStar
{
    /// <summary>pac\ch\chNNN.pac for a stock superstar</summary>
    public static string StockPac(string game, long star) => Path.Combine(game, "pac", "ch", $"ch{star}.pac");

    /// <summary>The id an EMD entry name belongs to: "00013902" -&gt; 139, or null when it is not one.</summary>
    public static long? IdOf(string name)
    {
        if (name.Length < 8 || !name.All(char.IsAsciiDigit)) return null;
        return long.TryParse(name[..6], out long id) ? id : null;
    }

    /// <summary>
    /// Our model, with any part of the game's own that we do not build put back.
    ///
    /// A character model is a PACH of numbered parts: 0 the body, 0x2710 the hair, 0x4E20 the EYELASHES,
    /// 0x3C the hair settings, 0x3D its capsules, and the rest. A PortIT port builds its own, and it
    /// builds no eyelashes - so replacing a superstar wholesale threw his lashes away. That is not only
    /// a look: every port is posed to a stock superstar's model and reads the lashes out of it, so after
    /// replacing (say) Big Show - PortIT's own default body type - the next port died on a bare
    /// "KeyNotFoundException: 20000" with nothing to say why (found 2026-11-17).
    ///
    /// Keeping only the ids we have none of is safe: the model's own body, textures and shapes all win,
    /// and the leftovers are the pieces the game expects to find and we never touch.
    /// </summary>
    public static byte[] KeepTheirParts(byte[] ours, byte[] theirs, List<string>? report = null)
    {
        List<Formats.Entry> mine, stock;
        try
        {
            mine = Formats.Pach.Read(Formats.Bpe.Unpack(ours));
            stock = Formats.Pach.Read(Formats.Bpe.Unpack(theirs));
        }
        catch (Exception) { return ours; }       // (not a model entry: left exactly as it was)
        var have = mine.Select(e => e.Id).ToHashSet();
        var add = stock.Where(e => !have.Contains(e.Id)).ToList();
        if (add.Count == 0) return ours;
        report?.AddRange(add.Select(e => NameOfPart(e.Id)));
        var all = mine.Concat(add).OrderBy(e => e.Id).ToList();
        var built = Formats.Pach.Write(all);
        return Formats.Bpe.IsBpe(ours) ? Formats.Bpe.Encode(built, true) : built;
    }

    static string NameOfPart(uint id) => id switch
    {
        0x4E20 => "eyelashes (0x4E20)",
        0x2710 => "hair (0x2710)",
        0x3C => "hair settings (0x3C)",
        0x3D => "hair capsules (0x3D)",
        _ => $"0x{id:X}",
    };

    public static JsonObject Run(JsonObject req, Func<string, string, string, JsonObject> copyPack, Action<string> log)
    {
        string S(string k, string d = "") => req[k] is JsonNode n
            ? (n.GetValueKind() == System.Text.Json.JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString()) : d;
        string src = S("mod_dir"), game = S("game");
        if (!long.TryParse(S("star"), out long star) || star < 100 || star > 321)
            throw new ArgumentException("star: the number of a superstar the game has (100 - 321)");

        string ourPac = Path.Combine(src, "ch.pac");
        if (!File.Exists(ourPac)) throw new ArgumentException("the port has no ch.pac - port first");
        string stockPath = StockPac(game, star);
        if (!File.Exists(stockPath))
            throw new ArgumentException($"ch{star}.pac is not in the game's pac\\ch folder: superstar {star} has no model to replace");

        var warnings = new JsonArray();
        var ours = Epk8.Read(File.ReadAllBytes(ourPac));
        var stock = Epk8.Read(File.ReadAllBytes(stockPath));

        // real entries only: the index is padded with blank names (the count field is 4x the entries)
        static List<NamedEntry> Real(Epk8.Archive a) =>
            a.Groups.SelectMany(g => g.Entries).Where(e => e.NameText.Trim('\0', ' ').Length > 0 && e.Data.Length > 0).ToList();

        var mine = Real(ours);
        var theirs = Real(stock);
        if (mine.Count == 0) throw new InvalidDataException("the ported ch.pac has no model entries");
        if (theirs.Count == 0) throw new InvalidDataException($"ch{star}.pac has no model entries - is it really a character pac?");

        // Our entries are named after the BASE the port was built on. Rename them to the superstar being
        // replaced - exactly what the game does for superstar mods ("its EMD entries are renamed to the
        // slot's id at load"), so a model does not care which id it sits under. That is what lets any port
        // replace any superstar, not only the one it was posed on.
        long builtFor = mine.Select(e => IdOf(e.NameText)).FirstOrDefault(x => x != null) ?? star;
        var byName = new Dictionary<string, NamedEntry>();
        foreach (var e in mine)
        {
            string n = e.NameText;
            if (IdOf(n) is null) { byName[n] = e; continue; }
            string renamed = $"{star:D6}{n[6..8]}";
            byName[renamed] = renamed == n ? e : new NamedEntry(Encoding.Latin1.GetBytes(renamed), e.Data);
        }
        if (builtFor != star)
        {
            log($"the port was built on superstar {builtFor}: its model entries are renamed for {star}");
            warnings.Add($"this model was posed on superstar {builtFor}, not {star}. It still loads - the entries are " +
                         $"renamed - but superstar {star}'s own size and build are used, so check they do not come out " +
                         $"too big or too small. Porting again with the body type set to {star} is the exact fit.");
        }
        var outEntries = new List<(byte[] Name, byte[] Data)>();
        var replaced = new List<string>();
        var kept = new List<string>();
        var lost = new List<string>();
        foreach (var e in theirs)
        {
            if (byName.TryGetValue(e.NameText, out var ourE))
            {
                outEntries.Add((ourE.Name, KeepTheirParts(ourE.Data, e.Data, lost)));
                replaced.Add(e.NameText);
                byName.Remove(e.NameText);
            }
            else
            {
                outEntries.Add((e.Name, e.Data));
                kept.Add(e.NameText);
            }
        }
        var added = byName.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        foreach (var n in added) outEntries.Add((byName[n].Name, byName[n].Data));

        if (replaced.Count == 0)
            throw new InvalidDataException(
                $"none of the ported model's entries ({string.Join(", ", mine.Select(e => e.NameText))}) match " +
                $"ch{star}.pac's ({string.Join(", ", theirs.Select(e => e.NameText))}) - nothing would change.");

        log($"replacing superstar {star}: {replaced.Count} model(s) swapped ({string.Join(", ", replaced)})" +
            (kept.Count > 0 ? $", {kept.Count} of the game's kept ({string.Join(", ", kept)})" : "") +
            (added.Count > 0 ? $", {added.Count} added ({string.Join(", ", added)})" : ""));
        if (kept.Count > 0)
            warnings.Add($"superstar {star} has other models the port did not cover ({string.Join(", ", kept)}): " +
                         "they are the game's originals still, so those attires look as they did.");
        if (lost.Count > 0)
            log("parts of the game's own model kept because the port has none: " + string.Join(", ", lost));

        byte[] built = Epk8.Write(File.ReadAllBytes(stockPath), outEntries);

        // back up the game's own pac first - this is the only copy of it
        string bakDir = Path.Combine(game, "Mods", "_portit_backup");
        Directory.CreateDirectory(bakDir);
        string bak = Path.Combine(bakDir, $"pac_ch{star}_{DateTime.Now:yyyyMMdd_HHmmss}.pac");
        File.Copy(stockPath, bak);
        File.WriteAllBytes(stockPath, built);
        log($"the game's ch{star}.pac is replaced ({new FileInfo(stockPath).Length / 1024} KB); the original is kept at {bak}");

        var o = new JsonObject
        {
            ["star"] = star,
            ["pac"] = stockPath,
            ["backup"] = bak,
            ["replaced"] = new JsonArray(replaced.Select(x => (JsonNode)x).ToArray()),
            ["kept"] = new JsonArray(kept.Select(x => (JsonNode)x).ToArray()),
            ["added"] = new JsonArray(added.Select(x => (JsonNode)x).ToArray()),
            ["warnings"] = warnings,
        };
        foreach (var (k, v) in copyPack(src, game, $"star{star}")) o[k] = v?.DeepClone();
        return o;
    }

    /// <summary>Put a replaced superstar back to the game's own model (the newest backup).</summary>
    public static JsonObject Restore(JsonObject req, Action<string> log)
    {
        string game = req["game"]!.GetValue<string>();
        long star = long.Parse(req["star"]!.ToJsonString().Trim('"'));
        string bakDir = Path.Combine(game, "Mods", "_portit_backup");
        var bak = Directory.Exists(bakDir)
            ? Directory.EnumerateFiles(bakDir, $"pac_ch{star}_*.pac").OrderBy(x => x, StringComparer.Ordinal).LastOrDefault()
            : null;
        if (bak == null) throw new ArgumentException($"no backup of superstar {star}'s model in {bakDir} - nothing to put back");
        string stockPath = StockPac(game, star);
        File.Copy(bak, stockPath, true);
        log($"superstar {star}: the game's own model is back (from {Path.GetFileName(bak)})");
        return new JsonObject { ["star"] = star, ["pac"] = stockPath, ["from"] = bak };
    }
}
