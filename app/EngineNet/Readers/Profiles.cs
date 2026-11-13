using System.Buffers.Binary;
using PortIT.Engine.Formats;

namespace PortIT.Engine.Readers;

/// <summary>
/// The stock superstars' profiles (pac/ch/chEtc.pac CHAR/PRO, a PACH of BPE records by superstar id). A profile's first
/// 0x1C0 bytes are its move-set: 224 little-endian move ids, one per slot (the Mod Maker tools' moveset_port.py N11). A
/// mod takes another superstar's moves with moves.txt: "0xOFF=id" for each slot that differs from its base's, and
/// manifest "moves=moves.txt" (Mod Maker manifest docs).
/// </summary>
public static class Profiles
{
    public const int MoveSlots = 0x1C0 / 2;

    public static Dictionary<int, byte[]> Read(string game)
    {
        string p = Path.Combine(game, "pac", "ch", "chEtc.pac");
        if (!File.Exists(p)) throw new FileNotFoundException("chEtc.pac is not in the game's pac/ch folder", p);
        var a = Epac.Read(File.ReadAllBytes(p));
        var pro = a.Groups.Where(g => g.TypeText == "CHAR").SelectMany(g => g.Entries).FirstOrDefault(e => e.NameText.Trim() == "PRO")
                  ?? throw new InvalidDataException("chEtc.pac has no CHAR/PRO table");
        return Pach.Read(Bpe.Unpack(pro.Data)).ToDictionary(e => (int)e.Id, e => Bpe.Unpack(e.Data));
    }

    public static ushort[] Moves(byte[] profile)
    {
        if (profile.Length < 2 * MoveSlots) throw new InvalidDataException("a profile shorter than its move-set");
        var o = new ushort[MoveSlots];
        for (int i = 0; i < MoveSlots; i++) o[i] = BinaryPrimitives.ReadUInt16LittleEndian(profile.AsSpan(2 * i));
        return o;
    }

    /// <summary>moves.txt lines giving superstar `from`'s move-set to a mod on base `bas` (only the slots that differ)</summary>
    public static List<string> MovesTxt(Dictionary<int, byte[]> pros, int bas, int from)
    {
        if (!pros.TryGetValue(bas, out var b)) throw new ArgumentException($"no profile for superstar {bas}");
        if (!pros.TryGetValue(from, out var f)) throw new ArgumentException($"no profile for superstar {from}");
        var mb = Moves(b); var mf = Moves(f);
        var o = new List<string>();
        for (int i = 0; i < MoveSlots; i++) if (mf[i] != mb[i]) o.Add($"0x{2 * i:X3}={mf[i]}");
        return o;
    }
}
