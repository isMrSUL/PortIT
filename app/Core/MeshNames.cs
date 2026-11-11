using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PortIT.Core;

/// <summary>2K / AEW mesh names in plain words for the mesh list (people's request 2026-11-11: "the names are hard"):
/// M_L_Kneepad -> Left knee pad, M_Hair_Entrance -> Hair (entrance), mt_Hands_R_01 -> Right hand wraps.</summary>
public static class MeshNames
{
    static readonly Dictionary<string, string> Words = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["Kneepad"] = "knee pad", ["Kneepads"] = "knee pad", ["Elbowpad"] = "elbow pad", ["Elbowpads"] = "elbow pad",
        ["Wrist"] = "wrist tape", ["Hands"] = "hand wraps", ["Tops"] = "top (shirt)", ["Shirt"] = "shirt", ["Bottoms"] = "pants",
        ["Underwear"] = "trunks", ["Tights"] = "tights", ["Shoes"] = "boots / shoes", ["Socks"] = "socks", ["Belt"] = "belt",
        ["Jacket"] = "jacket", ["JacketnoHood"] = "jacket without hood", ["HoodUp"] = "hood up", ["Cap"] = "cap", ["Hat"] = "hat",
        ["Mask"] = "mask", ["Masks"] = "mask", ["Glasses"] = "glasses", ["Pierce"] = "piercings", ["Combinations"] = "beard / facial hair",
        ["Hair"] = "hair", ["Eye"] = "eye", ["eyelash"] = "eyelashes", ["Eyelash"] = "eyelashes", ["Head"] = "head",
        ["Body"] = "body", ["MouthBag"] = "inside of the mouth", ["Mouth"] = "mouth (teeth, tongue)", ["Teeth"] = "teeth", ["Tongue"] = "tongue",
        ["Brassiere"] = "sports bra", ["Outfit"] = "outfit", ["Skin"] = "skin (head and body)", ["Robe"] = "robe", ["Coat"] = "coat",
        ["Cape"] = "cape", ["Gloves"] = "gloves", ["Glove"] = "glove", ["Necklace"] = "necklace", ["Towel"] = "towel", ["Vest"] = "vest",
    };

    /// <summary>a plain name for a mesh's material name</summary>
    public static string Friendly(string material)
    {
        string s = Regex.Replace(material, "^(M_|S_|mt_|mi_)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "^transB_", "", RegexOptions.IgnoreCase);
        int copy = 0;
        var m = Regex.Match(s, @"_0*(\d+)$");
        if (m.Success) { copy = int.Parse(m.Groups[1].Value); s = s[..m.Index]; }
        else if ((m = Regex.Match(s, @"(\d+)$")).Success && m.Index > 0) { copy = int.Parse(m.Groups[1].Value) + 1; s = s[..m.Index]; }
        var parts = s.Split('_', System.StringSplitOptions.RemoveEmptyEntries).ToList();
        var notes = new List<string>();
        string side = "";
        var words = new List<string>();
        for (int i = 0; i < parts.Count; i++)
        {
            string p = parts[i];
            if (p is "L" or "R" && (i == 0 || i == parts.Count - 1)) { side = p == "L" ? "left " : "right "; continue; }
            if (Regex.IsMatch(p, "^(ENT|Ent|Entrance)$", RegexOptions.IgnoreCase)) { notes.Add("entrance"); continue; }
            if (p.Equals("CS", System.StringComparison.OrdinalIgnoreCase)) { notes.Add("cutscene"); continue; }
            if (p.Equals("anim", System.StringComparison.OrdinalIgnoreCase)) { notes.Add("animated"); continue; }
            if (p.Equals("Sub", System.StringComparison.OrdinalIgnoreCase)) { notes.Add("extra"); continue; }
            if (p.Equals("withBelt", System.StringComparison.OrdinalIgnoreCase)) { words.Add("with belt"); continue; }
            if (p.Equals("HoodUp", System.StringComparison.OrdinalIgnoreCase) && words.Count > 0) { notes.Add("hood up"); continue; }
            if (p is "A" && i + 1 < parts.Count && parts[i + 1].Equals("Acce", System.StringComparison.OrdinalIgnoreCase)) { words.Add("arm band"); i++; continue; }
            if (p is "U" && i + 1 < parts.Count && parts[i + 1].Equals("Acce", System.StringComparison.OrdinalIgnoreCase)) { words.Add("upper-body accessory"); i++; continue; }
            if (p is "B" && i + 1 < parts.Count && parts[i + 1].Equals("Acce", System.StringComparison.OrdinalIgnoreCase)) { words.Add("body accessory"); i++; continue; }
            if (p is "H" && i + 1 < parts.Count && parts[i + 1].Equals("Other", System.StringComparison.OrdinalIgnoreCase)) { words.Add("head accessory"); i++; continue; }
            if (p is "F" && i + 1 < parts.Count && parts[i + 1].Equals("Hair", System.StringComparison.OrdinalIgnoreCase)) { words.Add("facial hair"); i++; continue; }
            if (p.Equals("Head", System.StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count && parts[i + 1].Equals("Caruncle", System.StringComparison.OrdinalIgnoreCase))
            { words.Add("eye corners"); i++; continue; }
            if (p.Equals("Ula", System.StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count && parts[i + 1].Equals("Fala", System.StringComparison.OrdinalIgnoreCase))
            { words.Add("Ula Fala (necklace)"); i++; continue; }
            if (p == "EYE") { words.Add("eyes"); continue; }
            words.Add(Words.TryGetValue(p, out var w) ? w : Regex.Replace(p, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant());
        }
        string name = side + string.Join(" ", words);
        if (name.Length == 0) name = material;
        name = char.ToUpperInvariant(name[0]) + name[1..];
        if (copy > 1) name += " " + copy;
        if (notes.Count > 0) name += " (" + string.Join(", ", notes) + ")";
        return name;
    }

    /// <summary>a piece worn for the entrance and taken off for the match, by its name: coats, jackets, robes, hats</summary>
    public static bool LooksEntranceGear(string material) =>
        Regex.IsMatch(material, "(jacket|coat|robe|hood|cape|cloak|poncho|hat|cap|towel)", RegexOptions.IgnoreCase)
        && !Regex.IsMatch(material, "(cap_|caruncle)", RegexOptions.IgnoreCase);

    /// <summary>2K's own entrance version of a piece (M_Hair_Entrance, M_Combinations_ENT)</summary>
    public static bool IsEntranceVersion(string material) => Regex.IsMatch(material, "_(ENT|Ent|Entrance)$", RegexOptions.IgnoreCase);
}
