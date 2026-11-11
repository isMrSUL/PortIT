using System.Text;
using PortIT.Engine.Formats;

namespace PortIT.Engine.Port;

/// <summary>
/// The entrance outfit (PortIT 2026-11-11, people's request: SvR 2011 has an entrance attire and a ring attire, the Mod
/// Maker makes both, PortIT made one): a second model of the wrestler - the whole attire with the coat / hat / robe the
/// ring model leaves out, or another attire - stored in the mod's ch.pac as EMD kind 4 beside the ring model (kind 2):
/// "%06d04" = attire 1's entrance outfit, as the game's own pacs (Undertaker, CM Punk, John Cena...) and the Mod Maker
/// write it. The game asks for it at the entrance and loads it instead of the ring model (a model without one walks out
/// in the ring attire), so it must fit the same per-wrestler memory as the ring model: it is built the same way.
/// </summary>
public static class Entrance
{
    static bool Real(NamedEntry e) => e.Name.Any(b => b != 0);

    /// <summary>the entrance build's model (its first EMD entry, kind 2) put into the ring pac as kind 4 of the same
    /// attire -&gt; the entry name written</summary>
    public static string AddTo(string ringPac, string entrancePac)
    {
        var ringBytes = File.ReadAllBytes(ringPac);
        var ring = Epk8.Read(ringBytes);
        var ent = Epk8.Read(File.ReadAllBytes(entrancePac));
        var rg = ring.Groups.First(g => Encoding.Latin1.GetString(g.Type) == "EMD ");
        var eg = ent.Groups.First(g => Encoding.Latin1.GetString(g.Type) == "EMD ");
        var entries = rg.Entries.Where(Real).Select(e => (e.Name, e.Data)).ToList();
        var model = entries.First(e => Encoding.Latin1.GetString(e.Name).Length == 8 && e.Name[7] == (byte)'2');
        var gear = eg.Entries.Where(Real).First(e => e.Name[7] == (byte)'2');
        string name = Encoding.Latin1.GetString(model.Name, 0, 7) + "4";
        entries.RemoveAll(e => Encoding.Latin1.GetString(e.Name) == name);
        int at = entries.FindIndex(e => ReferenceEquals(e.Name, model.Name)) + 1;
        entries.Insert(at, (Encoding.Latin1.GetBytes(name), gear.Data));
        File.WriteAllBytes(ringPac, Epk8.Write(ringBytes, entries));
        return name;
    }

    /// <summary>the EMD entry names of a pac (the real ones)</summary>
    public static List<string> Entries(string pac) =>
        Epk8.Read(File.ReadAllBytes(pac)).Groups.Where(g => Encoding.Latin1.GetString(g.Type) == "EMD ")
            .SelectMany(g => g.Entries.Where(Real)).Select(e => Encoding.Latin1.GetString(e.Name)).ToList();
}
