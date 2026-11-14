using System.Buffers.Binary;
using System.Text.Json.Nodes;
using PortIT.Engine.Formats;

namespace PortIT.Engine.Port;

/// <summary>
/// Real-world height (cm / feet+inches) and weight (kg / lbs) for a superstar mod.
///
/// What the game actually does (read out of the game's own files, 2026-11-14):
///  - every superstar's record (pac/ch/chEtc.pac CHAR/DAT) carries a body scale where 4096 = 1.00, and
///    1 game unit = 10 cm. The scale is the ONLY thing that sets how tall a superstar stands: all 90 stock
///    skeletons are the same size (floor to the head bone = 17.14 units for Rey Mysterio and Big Show alike;
///    only Hornswoggle's model is really smaller). Stock scales run 2845 (0.69) to 4764 (1.16).
///  - a mod has no record of its own: it inherits base=(a stock superstar)'s, so the body type silently sets
///    the mod's size. height= in the manifest multiplies that scale - the game logs it as
///    "superstar mods: id N height x1.05 (3729 -&gt; 3915)". The manifest range is 0.80 - 1.25.
///  - so: in-game height = the model's own height x base scale / 4096 x height=.
///    Turned round, that gives the height= needed for a height the user types.
///  - there is NO weight anywhere in the format: no weight= key exists in the game (every manifest key in
///    svr2011.exe was read), and the game never prints a height or a weight on screen. What it has is a
///    weight CLASS byte in the record, which the mod also inherits from base=. A weight in kg can therefore
///    only be honoured by choosing a body type in the matching class - hence WeightClassOf / ClassName.
/// </summary>
public static class Sizing
{
    public const double MinMult = 0.80, MaxMult = 1.25;
    public const int Unit = 4096;              // the record's scale for 1.00
    public const double CmPerUnit = 10.0;      // 1 game unit = 10 cm

    // ---------------------------------------------------------------- the model's own height

    /// <summary>
    /// The built model's real height in cm, measured from the soles to the top of the head with the HAIR LEFT OUT
    /// (a wrestler's height is to the skull, and a big hair style would otherwise make 175 cm mean hair-tip
    /// height: Rhea Ripley measures 19.10 units with her hair and 18.89 without). null when model.json has no
    /// usable geometry.
    /// </summary>
    public static double? ModelHeightCm(string modelJson)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(modelJson)) as JsonObject;
            if (root?["attires"] is not JsonArray attires || attires.Count == 0) return null;
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (var at in attires)
            {
                if (at?["meshes"] is not JsonArray meshes) continue;
                foreach (var m in meshes)
                {
                    if (m is not JsonObject mo) continue;
                    string kind = mo["kind"]?.GetValue<string>() ?? "";
                    string name = mo["name"]?.GetValue<string>() ?? "";
                    if (kind.Equals("hair", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("hair", StringComparison.OrdinalIgnoreCase)) continue;
                    if (mo["positions"] is not JsonArray ps) continue;
                    foreach (var p in ps)
                    {
                        if (p is not JsonArray xyz || xyz.Count < 2) continue;
                        double y = xyz[1]!.GetValue<double>();
                        if (y < lo) lo = y;
                        if (y > hi) hi = y;
                    }
                }
                break;   // (attire 1: every attire of one mod is the same body)
            }
            if (double.IsInfinity(lo) || hi <= lo) return null;
            return (hi - lo) * CmPerUnit;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- the base superstar's record

    /// <summary>The body scale and weight class of every stock superstar: id -&gt; (scale, weight class).</summary>
    public static Dictionary<int, (int Scale, int WeightClass)> BaseSizes(string game)
    {
        var o = new Dictionary<int, (int, int)>();
        string chetc = Path.Combine(game, "pac", "ch", "chEtc.pac");
        Epac.Archive arc;
        try { arc = Epac.Read(File.ReadAllBytes(chetc)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { return o; }
        foreach (var g in arc.Groups)
        {
            if (System.Text.Encoding.Latin1.GetString(g.Type) != "CHAR") continue;
            foreach (var e in g.Entries)
            {
                if (!e.NameText.StartsWith("DAT")) continue;
                try
                {
                    foreach (var x in Pach.Read(e.Data))
                    {
                        var r = Bpe.Unpack(x.Data);
                        // The record's layout, anchored on the name (PortIT reads it at +38): the id sits 2 bytes
                        // before the name, the scale 6 before, a second copy of the scale 4 before. Checking the id
                        // against the record's own number proves the anchor on this game's files instead of
                        // trusting a hardcoded offset.
                        const int nameAt = 38;
                        if (r.Length < nameAt + 32) continue;
                        int id = BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(nameAt - 2, 2));
                        if (id != x.Id) continue;
                        int scale = BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(nameAt - 6, 2));
                        if (scale < 1000 || scale > 9000) continue;
                        int wclass = r.Length > 213 ? r[213] : 0;
                        o[(int)x.Id] = (scale, wclass);
                    }
                }
                catch (Exception) { }
            }
        }
        return o;
    }

    // ---------------------------------------------------------------- the conversion

    /// <summary>
    /// The height= a mod needs so a model of modelCm stands targetCm tall in the game, and what it really gets.
    /// Mult is already clamped to the manifest's 0.80 - 1.25 and rounded to the 2 decimals the manifest holds, so
    /// GotCm is what the game will actually draw. InRange is false when the body type cannot reach the height.
    /// </summary>
    public static (double Mult, double GotCm, bool InRange) HeightMult(double targetCm, double modelCm, int baseScale)
    {
        if (modelCm <= 0 || baseScale <= 0) return (1.0, modelCm, false);
        double own = modelCm * baseScale / Unit;            // the height with height= left out
        double want = targetCm / own;
        double mult = Math.Round(Math.Clamp(want, MinMult, MaxMult), 2);
        return (mult, own * mult, Math.Abs(want - mult) <= 0.005);
    }

    /// <summary>The shortest and tallest a body type can make this model, in cm.</summary>
    public static (double Lo, double Hi) Reach(double modelCm, int baseScale)
    {
        double own = modelCm * baseScale / Unit;
        return (own * MinMult, own * MaxMult);
    }

    // ---------------------------------------------------------------- feet / inches, kg / lbs

    public static double FeetInchesToCm(double feet, double inches) => (feet * 12 + inches) * 2.54;

    /// <summary>cm -&gt; (feet, inches) rounded to the nearest inch.</summary>
    public static (int Feet, int Inches) CmToFeetInches(double cm)
    {
        int total = (int)Math.Round(cm / 2.54);
        return (total / 12, total % 12);
    }

    /// <summary>"175 cm (5'9")" - the height both ways round, for the log and the report.</summary>
    public static string Pretty(double cm)
    {
        var (ft, inch) = CmToFeetInches(cm);
        return string.Format("{0:F0} cm ({1}'{2}\")", cm, ft, inch);
    }

    public static double LbsToKg(double lbs) => lbs * 0.45359237;
    public static double KgToLbs(double kg) => kg / 0.45359237;

    /// <summary>
    /// The game's weight class for a weight. The classes are the record's own (read off the stock roster): 1 every
    /// diva, 2 the normal men (Jericho, Rey Mysterio, CM Punk, Kofi Kingston), 3 the heavyweights (The Rock, Stone
    /// Cold, Triple H, Undertaker, Kane, Edge, Batista), 4 the super heavyweights - only Big Show (441 lb) and Mark
    /// Henry (412 lb) are in it. The thresholds sit between those groups' real weights.
    /// </summary>
    public static int WeightClassOf(double kg, bool female)
    {
        if (female) return 1;
        if (kg >= 180) return 4;      // ~400 lb and over: Big Show, Mark Henry
        if (kg >= 113) return 3;      // ~250 lb and over: the heavyweights
        return 2;
    }

    public static string ClassName(int wclass) => wclass switch
    {
        1 => "Diva",
        2 => "normal",
        3 => "heavyweight",
        4 => "super heavyweight",
        _ => "unset",
    };
}
