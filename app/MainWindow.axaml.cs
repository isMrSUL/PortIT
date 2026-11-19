using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PortIT.Engine.Port;
using PortIT.Core;

namespace PortIT;

public partial class MainWindow : Window
{
    public sealed record WrestlerItem(string Id, string Name, string Badge, string Detail, bool Female, string[] Attires);
    /// <summary>a mesh of the chosen attire; Keep: ticked = ported (clothes / gear / hair can be unticked: left out of
    /// the port, the skin under them kept)</summary>
    public sealed class MeshItem : System.ComponentModel.INotifyPropertyChanged
    {
        public MeshItem(string name, string kind, string detail, bool canRemove, string? baseOf = null, string? why = null)
        {
            Name = name; Kind = kind; Detail = detail; CanRemove = canRemove; Base = baseOf; Why = why; Label = MeshNames.Friendly(name);
        }
        public string Name { get; }
        /// <summary>the name in plain words (Left knee pad)</summary>
        public string Label { get; }
        public string Kind { get; }
        public string Detail { get; }
        public bool CanRemove { get; }
        /// <summary>an alternate look of another mesh (M_Hair_Entrance of M_Hair): ticked, it is used instead of that one</summary>
        public string? Base { get; }
        /// <summary>why this one has no tick box (a greyed box that does nothing looked like a broken Meshes list -
        /// several rows are all called "Hair" and only one of them can come off, 2026-11-18)</summary>
        public string? Why { get; }
        /// <summary>the small grey line under the name: the real mesh name, and the reason when it cannot come off</summary>
        public string SubText => Why == null ? Name : Name + "   -   " + Why;
        public string TickTip => Why != null ? Why
                               : Base != null ? $"An alternate look of {MeshNames.Friendly(Base)}: tick to use it instead"
                                              : "Untick to leave this piece out of this outfit (the skin under it stays)";
        bool keep = true;
        public bool Keep
        {
            get => keep;
            set { if (keep == value) return; keep = value; PropertyChanged?.Invoke(this, new(nameof(Keep))); KeepChanged?.Invoke(this); }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public Action<MeshItem>? KeepChanged;
    }
    public sealed record HdSize(string Label, int Width)
    {
        public override string ToString() => Label;
    }

    bool FullDetail => ((BudgetBox.SelectedItem as Budget)?.Pages ?? 0) >= 1500;

    public sealed record Budget(string Label, int Pages)
    {
        public override string ToString() => Label;
    }

    /// <summary>a body type / fighting style: the stock superstar underneath the port (moves, entrance, stats, size) - whose
    /// own model is also the body the port is posed to. Every port was Big Show's (125) until 1.3.1: "everyone looks like
    /// a giant", user reports 2026-11-13</summary>
    public sealed record Style(string Label, int Base)
    {
        public override string ToString() => Label;
    }

    const int MaleStyle = 0, FemaleStyle = 10;
    static readonly Style[] BodyStyles =
    [
        new("Heavyweight / All-rounder (John Cena)", 139),
        new("Heavyweight / Powerhouse (Batista)", 160),
        new("Heavyweight / Monster (Kane)", 107),
        new("Heavyweight / Brawler (Stone Cold)", 101),
        new("Heavyweight / Showman (The Rock)", 100),
        new("Super heavyweight / Giant (Big Show)", 125),
        new("Light heavyweight / Technician (Chris Jericho)", 104),
        new("Light heavyweight / Technician (Bret Hart)", 177),
        new("Cruiserweight / High-flyer (Rey Mysterio)", 123),
        new("Cruiserweight / High-flyer (Evan Bourne)", 265),
        new("Diva / All-rounder (Michelle McCool)", 182),
        new("Diva / Powerhouse (Beth Phoenix)", 224),
        new("Diva / High-flyer (Kelly Kelly)", 164),
        .. new (int, string)[]
        {
            (297, "Alicia Fox"), (177, "Bret Hart"), (294, "Brie Bella"), (137, "Chavo Guerrero"), (276, "Christian"), (131, "CM Punk"),
            (205, "Cody Rhodes"), (281, "David Hart Smith"), (275, "Dolph Ziggler"), (278, "Drew McIntyre"), (110, "Edge"), (292, "Eve"),
            (268, "Ezekiel Jackson"), (158, "Finlay"), (293, "Gail Kim"), (277, "Goldust"), (195, "Hornswoggle"), (274, "Hurricane"),
            (267, "Jack Swagger"), (132, "Jake Roberts"), (153, "Jerry Lawler"), (122, "Jimmy Snuka"), (175, "John Morrison"), (165, "JTG"),
            (176, "Kofi Kingston"), (321, "Luke Gallows"), (179, "Mark Henry"), (291, "Maryse"), (112, "Matt Hardy"), (174, "Melina"),
            (143, "Mickie James"), (263, "Mike Knox"), (115, "Mr. McMahon"), (135, "MVP"), (290, "Natalya"), (295, "Nikki Bella"),
            (271, "Primo"), (272, "R-Truth"), (161, "Randy Orton"), (305, "Ricky Steamboat"), (306, "Rob Van Dam"), (201, "Santino Marella"),
            (178, "Shad Gaspard"), (145, "Shawn Michaels"), (279, "Sheamus"), (208, "Shelton Benjamin"), (261, "Ted DiBiase"), (169, "Terry Funk"),
            (218, "The Miz"), (296, "Tiffany"), (102, "Triple H"), (282, "Tyson Kidd"), (103, "Undertaker"), (284, "Vance Archer"),
            (262, "Vladimir Kozlov"), (117, "William Regal"), (283, "Yoshi Tatsu"), (219, "Zack Ryder"),
        }.Select(x => new Style($"Like {x.Item2}", x.Item1)),
    ];

    static readonly string SettingsPath = Path.Combine(Core.Engine.WorkRoot, "settings.json");
    readonly List<WrestlerItem> all = new();
    readonly ObservableCollection<WrestlerItem> shown = new();
    ObservableCollection<MeshItem> meshes => ring.Meshes;
    WrestlerItem? chosen;
    string? lastMod;
    /// <summary>the "send this one file" zip the last port made (model + HD textures) - the Share button opens it</summary>
    string? lastShare;
    int lastPages;
    CancellationTokenSource? cts;
    bool filling;
    readonly string python = LoadSetting("python") ?? Core.Engine.DefaultPython;

    /// <summary>which wrestler the screenshot hooks pick: PORTIT_SHOT_WHO (part of a name), else the first male one -
    /// the user guide's pictures show a male wrestler (the user's wish, 2026-11-19)</summary>
    int ShotPick()
    {
        var items = WrestlerList.Items.Cast<object>().OfType<WrestlerItem>().ToList();
        string? who = Environment.GetEnvironmentVariable("PORTIT_SHOT_WHO");
        int i = string.IsNullOrEmpty(who) ? -1 : items.FindIndex(w => w.Name.Contains(who, StringComparison.OrdinalIgnoreCase));
        if (i < 0) i = items.FindIndex(w => !w.Female);
        return Math.Max(0, i);
    }

    public MainWindow()
    {
        InitializeComponent();
        // PORTIT_SHOT=<file.png>: draw the window to a picture once it is up, then close. For looking at a layout
        // change without having to sit in front of it (2026-11-18, developers only - nothing happens without the
        // environment variable).
        // PORTIT_SHOTENT=<file.png>: pick the first wrestler, then change the Entrance outfit box and photograph the
        // main window - the check that the picture follows the box on its own, with no "Update preview" (2026-11-18).
        if (Environment.GetEnvironmentVariable("PORTIT_SHOTENT") is { Length: > 0 } shotEnt)
            Opened += async (_, _) =>
            {
                try
                {
                    // (for the guide's picture: a source folder with no user name in it, and the whole list loaded
                    //  from it - the app otherwise reopens whatever folder the developer used last)
                    if (Environment.GetEnvironmentVariable("PORTIT_SHOT_SOURCE") is { Length: > 0 } shotSrc)
                    {
                        await Task.Delay(1500);
                        SourceBox.Text = shotSrc;
                        await LoadList();
                        for (int i = 0; i < 120 && WrestlerList.ItemCount < 2; i++) await Task.Delay(500);
                    }
                    for (int i = 0; i < 60 && WrestlerList.ItemCount == 0; i++) await Task.Delay(500);
                    WrestlerList.SelectedIndex = ShotPick();
                    for (int i = 0; i < 80 && chosen == null; i++) await Task.Delay(500);
                    await Task.Delay(6000);
                    // (for the user guide's picture: neutral folders instead of the developer's own - setting the text
                    //  saves nothing, only Browse and a port write the settings)
                    if (Environment.GetEnvironmentVariable("PORTIT_SHOT_PATHS") == "1")
                    {
                        GameBox.Text = @"D:\Games\SvR2011";
                        OutBox.Text = @"D:\PortIT Mods";
                    }
                    var before = PreviewImage.Source;
                    if (EntranceBox.ItemCount > 1) EntranceBox.SelectedIndex = 1;      // "this attire with all its gear"
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < 120 && ReferenceEquals(PreviewImage.Source, before); i++) await Task.Delay(100);
                    bool changed = !ReferenceEquals(PreviewImage.Source, before);
                    await Task.Delay(800);
                    using (var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)Width, (int)Height), new Avalonia.Vector(96, 96)))
                    {
                        rtb.Render(this);
                        using var fs = File.Create(shotEnt);
                        rtb.Save(fs);
                    }
                    File.WriteAllText(shotEnt + ".info.txt",
                        (chosen?.Name ?? "?") + ": the picture " + (changed ? "redrew by itself after " + sw.ElapsedMilliseconds + " ms" : "DID NOT redraw"));
                }
                catch (Exception e) { File.WriteAllText(shotEnt + ".error.txt", e.ToString()); }
                Close();
            };
        // PORTIT_SHOT3D=<file.png>: pick the first wrestler, open the 3D view and draw THAT window to a picture.
        // Added because the 3D view came up empty and a build-only check could never have caught it (2026-11-18).
        if (Environment.GetEnvironmentVariable("PORTIT_SHOT3D") is { Length: > 0 } shot3d)
            Opened += async (_, _) =>
            {
                try
                {
                    for (int i = 0; i < 60 && WrestlerList.ItemCount == 0; i++) await Task.Delay(500);
                    if (WrestlerList.ItemCount == 0) { File.WriteAllText(shot3d + ".error.txt", "no wrestlers loaded"); Close(); return; }
                    WrestlerList.SelectedIndex = ShotPick();
                    for (int i = 0; i < 80 && chosen == null; i++) await Task.Delay(500);
                    await Task.Delay(3000);
                    Open3D();
                    await Task.Delay(1000);
                    var vw = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                             ?.Windows.OfType<ViewerWindow>().FirstOrDefault();
                    if (vw == null) { File.WriteAllText(shot3d + ".error.txt", "the 3D window did not open"); Close(); return; }
                    await Task.Delay(12000);
                    using (var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)vw.Width, (int)vw.Height), new Avalonia.Vector(96, 96)))
                    {
                        rtb.Render(vw);
                        using var fs = File.Create(shot3d);
                        rtb.Save(fs);
                    }
                    File.WriteAllText(shot3d + ".info.txt", "wrestler: " + (chosen?.Name ?? "?"));
                }
                catch (Exception e) { File.WriteAllText(shot3d + ".error.txt", e.ToString()); }
                Close();
            };
        if (Environment.GetEnvironmentVariable("PORTIT_SHOT") is { Length: > 0 } shotPath)
            Opened += async (_, _) =>
            {
                await Task.Delay(1500);
                try
                {
                    var size = new Avalonia.PixelSize((int)Width, (int)Height);
                    using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Avalonia.Vector(96, 96));
                    rtb.Render(this);
                    using (var fs = File.Create(shotPath)) rtb.Save(fs);
                }
                catch (Exception e) { File.WriteAllText(shotPath + ".error.txt", e.ToString()); }
                Close();
            };
        WrestlerList.ItemsSource = shown;
        MeshList.ItemsSource = meshes;
        BudgetBox.ItemsSource = new[]
        {
            new Budget("Classic window - 190 pages (every game version)", 190),
            new Budget("Bigger window - 500 pages (2048 KB builds)", 500),
            new Budget("Full detail + HD texture pack (game 2.1.6+)", 1500),
        };
        // (the HD pack's size - people's packs reached ~1 GB at 8192: 4K is the default now, 2026-11-10)
        HdBox.ItemsSource = new[]
        {
            new HdSize("8K - 8192 x 4096 (sharpest, ~90 MB a wrestler)", 8192),
            new HdSize("4K - 4096 x 2048 (~22 MB a wrestler)", 4096),
            new HdSize("2K - 2048 x 1024 (~6 MB a wrestler)", 2048),
        };
        HdBox.SelectedIndex = int.TryParse(LoadSetting("hdsize"), out int hd) && hd is >= 0 and <= 2 ? hd : 1;
        HdBox.SelectionChanged += (_, _) => SaveSetting("hdsize", HdBox.SelectedIndex.ToString());
        StyleBox.ItemsSource = BodyStyles;
        StyleBox.SelectedIndex = MaleStyle;
        MovesBox.ItemsSource = MoveSets;
        MovesBox.SelectedIndex = 0;
        PoseBox.ItemsSource = Poses;
        PoseBox.SelectedIndex = 0;
        SweatBox.ItemsSource = Sweats;
        SweatBox.SelectedIndex = 0;
        HairBox.ItemsSource = Hairs;
        HairBox.SelectedIndex = int.TryParse(LoadSetting("hair"), out int hv) && hv >= 0 && hv < Hairs.Length ? hv : 0;
        HairBox.SelectionChanged += (_, _) => SaveSetting("hair", HairBox.SelectedIndex.ToString());
        GameBox.Text = LoadSetting("game") ?? "";
        BudgetBox.SelectedIndex = int.TryParse(LoadSetting("budget"), out int b) ? b : DetectBudget(GameBox.Text);
        GameBox.LostFocus += (_, _) => BudgetBox.SelectedIndex = DetectBudget(GameBox.Text);
        OutBox.Text = LoadSetting("out") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PortIT");
        SetUpSizes();
        SetSource(LoadSetting("source") == "aew");

        Src2K.Click += (_, _) => SetSource(false);
        SrcAEW.Click += (_, _) => SetSource(true);
        MaleChip.Click += (_, _) => SetFemale(false);
        FemaleChip.Click += (_, _) => SetFemale(true);
        SearchBox.TextChanged += (_, _) => Filter();
        LoadButton.Click += async (_, _) => await LoadList();
        WrestlerList.SelectionChanged += async (_, _) => await Choose(WrestlerList.SelectedItem as WrestlerItem);
        // (not while Choose fills the box: it reads the meshes itself - two reads at once cancelled one, whose
        //  'engine stopped' then showed over the good result)
        AttireBox.SelectionChanged += async (_, _) => { if (!filling && chosen != null && AttireBox.SelectedItem is string) await Inspect(); };
        PortButton.Click += async (_, _) => await Port();
        PortAllButton.Click += async (_, _) => await PortAllAttires();
        ShareButton.Click += (_, _) => ShareLast();
        OpenButton.Click += (_, _) => { if (lastMod != null) System.Diagnostics.Process.Start("explorer.exe", lastMod); };
        // The 3D view works BEFORE porting too (2026-11-18): with no port yet it opens the 2K / mod model itself, and
        // once a port exists the window's "2K model" button switches between the two.
        View3DButton.Click += (_, _) => Open3D();
        // (people click the picture to turn it: a click on the picture opens the 3D view)
        PreviewImage.PointerPressed += (_, _) => { if (View3DButton.IsEnabled) Open3D(); };
        InstallButton.Click += async (_, _) => await Install();
        AttireButton.Click += async (_, _) => await InstallAttire();
        ReplaceButton.Click += async (_, _) => await ReplaceInGame();
        SourcePreviewButton.Click += async (_, _) => await SourcePreview();
        // click a row and that piece lights up in orange, so you can see which one a name is (2026-11-18)
        MeshList.SelectionChanged += (_, _) => { if (MeshList.SelectedItem is MeshItem mi) _ = SourcePreview(mi.Name); };
        GameBrowse.Click += async (_, _) => { if (await PickFolder("Choose the SvR 2011 game folder") is string p) { GameBox.Text = p; SaveSetting("game", p); } };
        SourceBrowse.Click += async (_, _) => { if (await PickFolder(SrcAEW.IsChecked == true ? "Choose AEW's paks folder (…\\content\\paks)" : "Choose a 2K folder: a 2K26 install, a mods folder or one wrestler") is string p) { SourceBox.Text = p; } };
        OutBrowse.Click += async (_, _) => { if (await PickFolder("Choose where PortIT writes the mods") is string p) { OutBox.Text = p; SaveSetting("out", p); } };
        BudgetBox.SelectionChanged += (_, _) => { SaveSetting("budget", BudgetBox.SelectedIndex.ToString()); HdBox.IsEnabled = FullDetail; };
        HdBox.IsEnabled = FullDetail;
        BigModelButton.Click += async (_, _) => await BigModel();
        // (bug reports with the model link, to SUL's private Discord channel - 1.3.2)
        ReportButton.Click += async (_, _) => await new ReportDialog(chosen?.Name ?? "",
                                                                     Source == "aew" ? "AEW Fight Forever" : "WWE 2K",
                                                                     ReportDialog.CollectLog(Core.Engine.LastLog, ReportText.Text ?? "")).ShowDialog(this);
        OpenModelButton.Click += async (_, _) => await OpenModel();
        DiscordButton.Click += (_, _) => Community.Open(Community.Discord);
        SuggestButton.Click += async (_, _) => await new SuggestDialog().ShowDialog(this);
        Opened += async (_, _) =>
        {
            FitToScreen();
            // (the Discord server: said once, on the first start after updating - the user's request 2026-11-13)
            if (!SkipAnnounce && LoadSetting("discord_told") == null)
            {
                SaveSetting("discord_told", DateTime.Now.ToString("yyyy-MM-dd"));
                await Community.Announce(this);
            }
            _ = CheckForUpdate();          // (in the background: the list loads meanwhile)
            if (Directory.Exists(SourceBox.Text)) await LoadList();
        };
        InitOutfits();
    }

    /// <summary>(screenshots: Render.Window) don't open the one-time Discord notice over the shot</summary>
    internal static bool SkipAnnounce;

    /// <summary>never open bigger than the screen: on a 1366x768 laptop PortIT opened 1500x920, so the Port button sat
    /// below the bottom edge and could not be reached (user report 2026-11-14)</summary>
    void FitToScreen()
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen == null) return;
            double scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
            double maxW = screen.WorkingArea.Width / scale, maxH = screen.WorkingArea.Height / scale;
            if (Width > maxW) Width = Math.Max(MinWidth, maxW);
            if (Height > maxH) Height = Math.Max(MinHeight, maxH);
            // (centre it again: a window shrunk after CenterScreen sits off to one side)
            Position = new Avalonia.PixelPoint(
                screen.WorkingArea.X + (int)Math.Max(0, (screen.WorkingArea.Width - Width * scale) / 2),
                screen.WorkingArea.Y + (int)Math.Max(0, (screen.WorkingArea.Height - Height * scale) / 2));
        }
        catch (Exception) { }
    }

    /// <summary>(screenshots: Render.Window) pick the first listed wrestler whose name contains `name`</summary>
    internal void ShotPick(string name)
    {
        var it = shown.FirstOrDefault(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (it != null) WrestlerList.SelectedItem = it;
    }

    // ------------------------------------------------------------------ source
    void SetSource(bool aew)
    {
        Src2K.IsChecked = !aew;
        SrcAEW.IsChecked = aew;
        SourceBox.Watermark = aew ? "AEW's paks folder (…\\CUSA34641\\elite\\content\\paks)" : "Folder with the 2K wrestlers (2K19 - 2K26)";
        SourceBox.Text = LoadSetting(aew ? "aewPath" : "2kPath") ?? "";
        SaveSetting("source", aew ? "aew" : "2k26");
        all.Clear();
        shown.Clear();
        CountText.Text = "nothing loaded";
        ResetOutfits();
    }

    string Source => SrcAEW.IsChecked == true ? "aew" : "2k26";
    string SourceLabel => Source == "aew" ? "AEW" : "2K";

    void SetFemale(bool female)
    {
        MaleChip.IsChecked = !female;
        FemaleChip.IsChecked = female;
        // (the body type follows: a woman on a man's style got a man's moves and body, and the other way round)
        bool styleFemale = StyleBox.SelectedItem is Style s && (s.Label.StartsWith("Diva") || FemaleBases.Contains(s.Base));
        if (styleFemale != female) StyleBox.SelectedIndex = female ? FemaleStyle : MaleStyle;
        Hints();   // (the weight class depends on this: every diva is class 1)
    }

    // ---------------------------------------------------------------- height and weight in real units
    //
    // SvR 2011 keeps no height and no weight of its own. A superstar's record holds a body scale (4096 = 1.00,
    // 1 game unit = 10 cm) and a weight CLASS, and a mod inherits both from its body type. height= in the
    // manifest multiplies the scale, so a height in cm can be honoured exactly - the engine works the
    // multiplier out after the build, when the model's real size is known (EngineNet/Port/Sizing.cs). A weight
    // cannot be written at all; it is only checked against the body type's class. These boxes therefore take
    // what a person knows - cm or feet and inches, kg or lbs - and the user request of 2026-11-14 was exactly
    // that: the old 0.80 - 1.25 box was too hard to understand.

    double heightCm = 180, weightKg = 100;
    bool unitSync;                                        // (filling one box from the other must not loop back)
    string? sizesFor; Dictionary<int, (int Scale, int WeightClass)>? sizes;

    void SetUpSizes()
    {
        HeightUnitBox.ItemsSource = new[] { "cm", "feet + in" };
        HeightUnitBox.SelectedIndex = LoadSetting("height_unit") == "ft" ? 1 : 0;
        WeightUnitBox.ItemsSource = new[] { "kg", "lbs" };
        WeightUnitBox.SelectedIndex = LoadSetting("weight_unit") == "lb" ? 1 : 0;
        if (double.TryParse(LoadSetting("height_cm"), System.Globalization.CultureInfo.InvariantCulture, out double h) && h is >= 120 and <= 250) heightCm = h;
        if (double.TryParse(LoadSetting("weight_kg"), System.Globalization.CultureInfo.InvariantCulture, out double w) && w is >= 30 and <= 300) weightKg = w;
        ShowSizes();
        HeightUnitBox.SelectionChanged += (_, _) => { SaveSetting("height_unit", HeightUnitBox.SelectedIndex == 1 ? "ft" : "cm"); ShowSizes(); };
        WeightUnitBox.SelectionChanged += (_, _) => { SaveSetting("weight_unit", WeightUnitBox.SelectedIndex == 1 ? "lb" : "kg"); ShowSizes(); };
        HeightCmBox.ValueChanged += (_, _) => ReadSizes();
        HeightFtBox.ValueChanged += (_, _) => ReadSizes();
        HeightInBox.ValueChanged += (_, _) => ReadSizes();
        WeightBox.ValueChanged += (_, _) => ReadSizes();
        StyleBox.SelectionChanged += (_, _) => Hints();
    }

    /// <summary>the boxes -&gt; heightCm / weightKg (what the user just typed)</summary>
    void ReadSizes()
    {
        if (unitSync) return;
        heightCm = HeightUnitBox.SelectedIndex == 1
            ? Sizing.FeetInchesToCm((double)(HeightFtBox.Value ?? 5m), (double)(HeightInBox.Value ?? 0m))
            : (double)(HeightCmBox.Value ?? 180m);
        weightKg = WeightUnitBox.SelectedIndex == 1 ? Sizing.LbsToKg((double)(WeightBox.Value ?? 220m)) : (double)(WeightBox.Value ?? 100m);
        SaveSetting("height_cm", heightCm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        SaveSetting("weight_kg", weightKg.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        Hints();
    }

    /// <summary>heightCm / weightKg -&gt; the boxes, in whichever unit is chosen</summary>
    void ShowSizes()
    {
        bool ft = HeightUnitBox.SelectedIndex == 1;
        unitSync = true;
        var (f, i) = Sizing.CmToFeetInches(heightCm);
        HeightFtBox.Value = f;
        HeightInBox.Value = i;
        HeightCmBox.Value = (decimal)Math.Round(heightCm);
        WeightBox.Value = (decimal)Math.Round(WeightUnitBox.SelectedIndex == 1 ? Sizing.KgToLbs(weightKg) : weightKg);
        unitSync = false;
        HeightCmBox.IsVisible = !ft;
        HeightFtPanel.IsVisible = ft;
        Hints();
    }

    /// <summary>The stock superstars' scales and weight classes out of the chosen game, read once per game folder.</summary>
    Dictionary<int, (int Scale, int WeightClass)> Sizes()
    {
        string g = GameBox.Text?.Trim().Trim('"') ?? "";
        if (sizes == null || sizesFor != g)
        {
            sizesFor = g;
            try { sizes = Sizing.BaseSizes(g); } catch (Exception) { sizes = []; }
        }
        return sizes;
    }

    /// <summary>the small lines under the boxes: the same height the other way round, and what the game does with a weight</summary>
    void Hints()
    {
        if (HeightHint == null || WeightHint == null) return;
        var (f, i) = Sizing.CmToFeetInches(heightCm);
        HeightHint.Text = HeightUnitBox.SelectedIndex == 1 ? $"= {Math.Round(heightCm):0} cm" : $"= {f}'{i}\"";
        int baseId = (StyleBox.SelectedItem as Style)?.Base ?? 139;
        bool female = FemaleChip.IsChecked == true;
        int want = Sizing.WeightClassOf(weightKg, female);
        string lbs = $"{Math.Round(Sizing.KgToLbs(weightKg)):0} lb";
        string kg = $"{Math.Round(weightKg):0} kg";
        string said = WeightUnitBox.SelectedIndex == 1 ? kg : lbs;
        // the game has no weight number: say so once, here, instead of letting people think it was written
        WeightHint.Text = Sizes().TryGetValue(baseId, out var b) && b.WeightClass != want
            ? $"= {said} - {Sizing.ClassName(want)}, but this body type is {Sizing.ClassName(b.WeightClass)}"
            : $"= {said} - {Sizing.ClassName(want)} class";
    }

    /// <summary>the move-set: the body type's own, or a stock superstar's (moves.txt over the base's; user request 2026-11-13)</summary>
    public sealed record MoveSet(string Label, int From)
    {
        public override string ToString() => Label;
    }

    static readonly MoveSet[] MoveSets =
    [
        new("Same as the body type", -1),
        .. BodyStyles.Select(s => (s.Base, Name: s.Label.Contains('(') ? s.Label[(s.Label.LastIndexOf('(') + 1)..^1] : s.Label["Like ".Length..]))
                 .GroupBy(x => x.Base).Select(g => g.First()).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                 .Select(x => new MoveSet($"{x.Name}'s move-set", x.Base)),
    ];

    /// <summary>the select / VS screen pose: the body type's own, or a superstar's (the mod's pose=, a superstar id
    /// below 512 - Mod Maker manifest docs). User request 2026-11-16.</summary>
    public sealed record PoseChoice(string Label, int Id)
    {
        public override string ToString() => Label;
    }

    static readonly PoseChoice[] Poses =
    [
        new("The body type's", 0),
        .. BodyStyles.Select(s => (s.Base, Name: s.Label.Contains('(') ? s.Label[(s.Label.LastIndexOf('(') + 1)..^1] : s.Label["Like ".Length..]))
                 .GroupBy(x => x.Base).Select(g => g.First()).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                 .Select(x => new PoseChoice($"{x.Name}'s pose", x.Base)),
    ];

    /// <summary>
    /// How wet the skin looks. The game's skin shader multiplies its sweat level (which rises during a match) by a
    /// sweat mask, and that mask is the alpha of the skin texture: stock SvR 2011 is a flat 77. PortIT's old
    /// fallback was 47 - drier than the game's own. (user request 2026-11-16)
    /// </summary>
    public sealed record SweatChoice(string Label, int? Level, double Shine)
    {
        public override string ToString() => Label;
    }

    /// <summary>
    /// How much the hair swings, from the settings the game's own superstars carry. Measured off all 445 chain
    /// settings lines in the 43 haired stock characters, so every level stays inside the range the game itself uses
    /// (Build.HairChains.Line). (user request 2026-11-16: "lets make a big update to moving hair")
    /// </summary>
    public sealed record HairChoice(string Label, string? Feel)
    {
        public override string ToString() => Label;
    }

    static readonly HairChoice[] Hairs =
    [
        new("Off - the hair stays still", null),
        new("Like the game", "natural"),
        new("Gentle - barely moves", "gentle"),
        new("Loose - swings more", "loose"),
    ];

    static readonly SweatChoice[] Sweats =
    [
        new("Like the game", 77, 0.33),
        new("Dry - no shine", 0, 0.0),
        new("Extra oily", 140, 0.62),
        new("As the model has it", null, 0.20),
    ];

    static readonly HashSet<int> FemaleBases = [297, 224, 294, 292, 293, 164, 291, 174, 182, 143, 290, 295, 296];

    /// <summary>one model file (people's request 2026-11-10: open just the wrestler they want, no game needed)</summary>
    async Task OpenModel()
    {
        // (a model folder: one wrestler's mod folder - BaseModel + its attire folder(s), e.g.
        //  1298_Headshrinker_Fatu_WM9_2K25 - or a folder holding such folders / chNNNNN.pac packs. Loaded at once and,
        //  with one wrestler in it, chosen at once so the preview shows; the user's request 2026-11-10)
        string? p = await PickFolder("Open a model folder (a wrestler's folder with BaseModel, or a folder of 2K mods / .pac packs)");
        if (p == null) return;
        SetSource(false);
        SourceBox.Text = p;
        SearchBox.Text = "";
        await LoadList();
        if (shown.Count == 1) WrestlerList.SelectedItem = shown[0];
    }

    /// <summary>the big-model DLL: what it is, whether it is in the game folder and was used, install / remove</summary>
    async Task BigModel()
    {
        string game = GameBox.Text?.Trim().Trim('"') ?? "";
        if (!File.Exists(Path.Combine(game, "svr2011.exe"))) { Status("Choose the SvR 2011 game folder first (the one with svr2011.exe)."); return; }
        JsonElement st = default;
        await Work("Checking the big-model DLL…", async ct => st = await Core.Engine.Run(python, new() { ["command"] = "bigmodel", ["game"] = game, ["action"] = "status" }, null, ct));
        if (st.ValueKind != JsonValueKind.Object) return;
        string state = st.GetProperty("installed").GetString() ?? "none";
        string text =
            "PortIT's big-model DLL (xinput1_4.dll beside svr2011.exe) gives every wrestler 4096 KB of model memory instead " +
            "of the game's 768 KB. It is needed for \"Full detail + HD texture pack\" ports: without it they are cut down to " +
            "fit, and a full-detail model can crash the match. Game 2.1.6 or newer.\n\n" +
            "In this game folder: " + state switch
            {
                "portit" => "installed.",
                "other" => "another xinput1_4.dll is there (another mod). Installing keeps it as xinput1_4.dll.before_portit.",
                _ => "not installed.",
            } +
            (st.TryGetProperty("note", out var n) && n.GetString() is { Length: > 0 } note ? "\nLast game start: " + note + "." : "") +
            (st.TryGetProperty("version", out var v) && v.GetString() is { Length: > 0 } ver ? "\nGame: " + ver + "." : "") +
            "\n\nClose the game before installing or removing.";
        string? pick = await Choose3(text, state == "portit" ? "Reinstall" : "Install", state == "portit" ? "Remove" : null);
        if (pick == null) return;
        await Work(pick == "Remove" ? "Removing the big-model DLL…" : "Installing the big-model DLL…", async ct =>
        {
            var r = await Core.Engine.Run(python, new() { ["command"] = "bigmodel", ["game"] = game, ["action"] = pick == "Remove" ? "remove" : "install" }, null, ct);
            Status(pick == "Remove" ? "Big-model DLL removed." :
                   "Big-model DLL installed. Start the game once: then PortIT's full-detail ports use the bigger model memory.");
        });
    }

    async Task<string?> Choose3(string text, string main, string? second)
    {
        var a = new Button { Content = main, Classes = { "accent" } };
        var close = new Button { Content = "Close" };
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        row.Children.Add(close);
        Button? b = null;
        if (second != null) { b = new Button { Content = second }; row.Children.Add(b); }
        row.Children.Add(a);
        var dlg = new Window
        {
            Title = "PortIT - big-model DLL", Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20), Spacing = 16,
                Children = { new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, row },
            },
        };
        string? result = null;
        a.Click += (_, _) => { result = main; dlg.Close(); };
        if (b != null) b.Click += (_, _) => { result = second; dlg.Close(); };
        close.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        return result;
    }

    async Task LoadList()
    {
        string path = SourceBox.Text?.Trim().Trim('"') ?? "";
        if (!Directory.Exists(path) && !(File.Exists(path) && Source == "2k26")) { Status("Choose a source folder or a model file first."); return; }
        SaveSetting(Source == "aew" ? "aewPath" : "2kPath", path);
        await Work("Reading the wrestlers…", async ct =>
        {
            var r = await Core.Engine.Run(python, new() { ["command"] = "list", ["source"] = Source, ["path"] = path }, null, ct);
            all.Clear();
            foreach (var w in r.GetProperty("wrestlers").EnumerateArray())
            {
                bool female = w.TryGetProperty("female", out var f) && f.GetBoolean();
                string[] attires = w.TryGetProperty("attires", out var a) ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>();
                string detail = Source == "aew" ? $"attire {w.GetProperty("attire").GetString()}   ·   {w.GetProperty("id").GetString()}"
                                                : $"{attires.Length} attire(s)   ·   {(w.TryGetProperty("group", out var g) ? g.GetString() : "")}";
                all.Add(new(w.GetProperty("id").GetString()!, w.GetProperty("name").GetString()!, female ? "F" : "M", detail, female, attires));
            }
            Filter();
            Status($"{all.Count} wrestler attires found.");
        });
    }

    void Filter()
    {
        string q = SearchBox.Text?.Trim().ToLowerInvariant() ?? "";
        shown.Clear();
        foreach (var w in all.Where(w => q == "" || w.Name.ToLowerInvariant().Contains(q) || w.Id.ToLowerInvariant().Contains(q)))
            shown.Add(w);
        CountText.Text = all.Count == 0 ? "nothing loaded" : $"{shown.Count} of {all.Count}";
    }

    async Task Choose(WrestlerItem? w)
    {
        chosen = w;
        PortButton.IsEnabled = w != null;
        // "Port every attire" only means anything when there IS more than one (2026-11-18)
        PortAllButton.IsEnabled = w != null && w.Attires.Length > 1;
        if (w == null) return;
        ChosenText.Text = $"{w.Name}   ({(w.Female ? "female" : "male")})";
        SetFemale(w.Female);
        string id = System.Text.RegularExpressions.Regex.Replace(w.Name.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        ModIdBox.Text = id;
        ModNameBox.Text = w.Name;
        filling = true;
        try
        {
            AttireBox.ItemsSource = w.Attires.Length > 0 ? w.Attires : new[] { "(the one attire)" };
            AttireBox.SelectedIndex = 0;
            FillEntrance(w);
        }
        finally { filling = false; }
        await Inspect();
        // Man or woman, once the model itself has been read.
        //
        // The list only knows for a 2K19 - 2K22 pack it has unpacked before, and unpacking 100 packs to
        // draw a list would take a minute, so until then every one of them is listed as a man - Alexa
        // Bliss included (2026-11-17). Reading the wrestler tells us properly: the women's body has
        // 12,699 points. This is the moment the chip, the fighting style and the weight class are set,
        // and it is only done here, when the wrestler is picked, so a chip you set yourself stands.
        if (ringInspect is System.Text.Json.JsonElement ri && ri.TryGetProperty("female", out var fe)
            && fe.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
            && fe.GetBoolean() != (FemaleChip.IsChecked == true))
        {
            SetFemale(fe.GetBoolean());
            ChosenText.Text = $"{w.Name}   ({(fe.GetBoolean() ? "female" : "male")})";
        }
        // (screenshots only: PORTIT_SHOT_ENTRANCE=<n> picks entrance option n once the wrestler is read)
        if (int.TryParse(Environment.GetEnvironmentVariable("PORTIT_SHOT_ENTRANCE"), out int shotEntrance) && shotEntrance < EntranceBox.ItemCount)
            EntranceBox.SelectedIndex = shotEntrance;
    }

    Dictionary<string, object?> Request(string command)
    {
        var w = chosen!;
        var req = new Dictionary<string, object?>
        {
            ["command"] = command, ["source"] = Source, ["path"] = SourceBox.Text?.Trim().Trim('"') ?? "", ["id"] = w.Id,
        };
        if (Source == "2k26" && AttireBox.SelectedItem is string a && w.Attires.Contains(a)) req["attire"] = a;
        return req;
    }

    /// <summary>everything the "port" command needs, from the settings on the right. Shared by one port and by
    /// "Port all attires" (2026-11-18), so the two can never drift apart.</summary>
    Dictionary<string, object?> PortRequest(string game)
    {
        string outDir = OutBox.Text?.Trim().Trim('"') ?? "";
        SaveSetting("out", outDir);
        var req = Request("port");
        req["game"] = game;
        req["out"] = outDir;
        req["female"] = FemaleChip.IsChecked == true;
        req["base"] = (StyleBox.SelectedItem as Style)?.Base ?? 139;
        // real height and weight: the engine turns the height into the manifest's height= once it knows the
        // model's own size, and checks the weight against the body type's class (the game stores no weight)
        req["height_cm"] = heightCm;
        req["weight_kg"] = weightKg;
        if (MovesBox.SelectedItem is MoveSet ms && ms.From > 0) req["moves_from"] = ms.From;
        if (PoseBox.SelectedItem is PoseChoice pc && pc.Id > 0) req["pose"] = pc.Id;
        if (SweatBox.SelectedItem is SweatChoice sc && sc.Level is int lv) req["sweat"] = lv;
        req["budget"] = (BudgetBox.SelectedItem as Budget)?.Pages ?? 190;
        req["hd_size"] = (HdBox.SelectedItem as HdSize)?.Width ?? 4096;
        req["face"] = FaceCheck.IsChecked == true;
        var hair = HairBox.SelectedItem as HairChoice ?? Hairs[0];
        req["moving_hair"] = hair.Feel != null;
        if (hair.Feel != null) req["hair_feel"] = hair.Feel;
        req["mod_id"] = (ModIdBox.Text ?? "").Trim();
        req["mod_name"] = (ModNameBox.Text ?? "").Trim();
        // (each outfit's unticked pieces left out, its ticked alternate looks used; the entrance outfit a second model)
        AddOutfits(req);
        return req;
    }

    // ------------------------------------------------------------------ port
    async Task Port()
    {
        if (chosen == null) return;
        string game = GameBox.Text?.Trim().Trim('"') ?? "";
        if (!File.Exists(Path.Combine(game, "pac", "ch", "ch104.pac")))
        {
            Status("The SvR game folder must have pac\\ch (the stock models are the templates).");
            return;
        }
        SaveSetting("game", game);
        // the big-model DLL: is PortIT's in the game folder, and did the game use it (bigmodel.log)? Without it a "Full
        // detail" port is cut down to the normal 768 KB - Marina Shafir's hair went from 1090 cards to ~250 (GitHub issue 1,
        // 2026-11-12): the old check only looked for a file named xinput1_4.dll
        string dllState = "none"; bool dllActive = false;
        try
        {
            var st = await Core.Engine.Run(python, new() { ["command"] = "bigmodel", ["game"] = game, ["action"] = "status" }, null, CancellationToken.None);
            dllState = st.GetProperty("installed").GetString() ?? "none";
            dllActive = st.TryGetProperty("active", out var ac) && ac.GetBoolean();
        }
        catch (Exception) { dllState = File.Exists(Path.Combine(game, "xinput1_4.dll")) ? "portit" : "none"; dllActive = true; }   // (no answer: the old file check)
        const string smaller = "Port it smaller";
        if (FullDetail && dllState != "portit")
        {
            // (people's request 2026-11-10: tell them the DLL is needed for the better model and textures, and put it in)
            string? pick = await Choose3("\"Full detail + HD texture pack\" needs PortIT's big-model DLL in the game folder " +
                                         "(xinput1_4.dll): it gives each wrestler 4096 KB of model memory instead of 768 KB.\n\n" +
                                         (dllState == "other" ? "The xinput1_4.dll in your game folder is from another mod, not PortIT's. Installing keeps it as xinput1_4.dll.before_portit.\n\n" : "It is not installed.\n\n") +
                                         "Without it this port is cut down to the game's normal size: thinner hair, rougher clothes and body.\n\n" +
                                         "Install it now? (Close the game first. Start the game once after installing, then port again for the full model.)",
                                         "Install", smaller);
            if (pick == "Install")
            {
                await Work("Installing the big-model DLL…", async ct =>
                {
                    await Core.Engine.Run(python, new() { ["command"] = "bigmodel", ["game"] = game, ["action"] = "install" }, null, ct);
                    Status("Big-model DLL installed. Start the game once, then port again for the full-detail model.");
                });
                return;
            }
            if (pick != smaller) return;
        }
        else if (FullDetail && !dllActive)
        {
            string? pick = await Choose3("PortIT's big-model DLL is installed, but the game has not used it yet: there is no bigmodel.log " +
                                         "in the game folder, or it shows the normal 768 KB.\n\n" +
                                         "Start the game once (to the main menu), close it, then port again for the full model.\n\n" +
                                         "If you port now, the model is cut down to the game's normal size: thinner hair, rougher clothes and body.",
                                         smaller, null);
            if (pick != smaller) return;
        }
        bool remind = !FullDetail && dllState != "portit";
        var req = PortRequest(game);
        ReportText.Text = "";

        await Work($"Porting {chosen.Name}…", async ct =>
        {
            var r = await Core.Engine.Run(python, req, line => Dispatcher.UIThread.Post(() => { ReportText.Text += line + "\n"; Status(line); }), ct);
            lastMod = r.GetProperty("out").GetString();
            ReportText.Text = string.Join("\n", r.GetProperty("report").EnumerateArray().Select(x => "•  " + x.GetString()));
            Ported(r);
            lastPages = r.GetProperty("pages").GetInt32();
            OpenButton.IsEnabled = InstallButton.IsEnabled = AttireButton.IsEnabled = View3DButton.IsEnabled = ReplaceButton.IsEnabled = true;
            Status($"Done: {lastMod}");
            lastShare = r.TryGetProperty("share_zip", out var szp) && szp.ValueKind == JsonValueKind.String ? szp.GetString() : null;
            ShareButton.IsEnabled = lastMod != null;
            if (r.TryGetProperty("share_zip", out var sz) && sz.ValueKind == JsonValueKind.String)
            {
                // (people shared the .svrmod and forgot the HD textures: one zip with both - the user's request 2026-11-12)
                string share = Path.GetFileName(sz.GetString()!);
                ReportText.Text += $"\n\nTO SHARE THIS WRESTLER: send \"{share}\" (next to the mod). It has the model AND its HD textures - " +
                                   "the .svrmod alone has no HD textures.";
                Status($"Done  ·  to share it, send \"{share}\" (model + HD textures)");
            }
            if (remind)
            {
                // (the user's request 2026-11-12: remind people the full model quality needs the DLL)
                const string tip = "Tip: for the full model quality (all the hair, smooth clothes, HD textures), pick \"Full detail + " +
                                   "HD texture pack\" and install PortIT's big-model DLL (the Big-model DLL button), then start the game once.";
                ReportText.Text += "\n\n" + tip;
                Status($"Done: {lastMod}  ·  {tip}");
            }
            // (a warning sits in the middle of a long report and people miss it - say it in the status line too)
            if (r.GetProperty("report").EnumerateArray().Select(x => x.GetString() ?? "")
                 .FirstOrDefault(l => l.StartsWith("warning:", StringComparison.OrdinalIgnoreCase)) is { } warn)
                Status(warn);
        });
    }

    // ------------------------------------------------------------------ every attire in one go
    /// <summary>Port EVERY attire this wrestler has, one after the other, with the settings as they are - each into its
    /// own mod folder named after the attire. Without this it is the same long job repeated by hand for each attire
    /// (the user's request 2026-11-18). The engine is untouched: it is the same port command, run once per attire.</summary>
    async Task PortAllAttires()
    {
        if (chosen == null) return;
        var attires = chosen.Attires.ToList();
        if (attires.Count < 2) { Status("This wrestler has only one attire - use Port."); return; }
        string game = GameBox.Text?.Trim().Trim('"') ?? "";
        if (!File.Exists(Path.Combine(game, "pac", "ch", "ch104.pac")))
        {
            Status(@"The SvR game folder must have pac\ch (the stock models are the templates).");
            return;
        }
        SaveSetting("game", game);
        if (!await Confirm($"Port all {attires.Count} attires of {chosen.Name}, one after the other?\n\n" +
                           "Each one becomes its own mod folder, named after its attire. The settings on the right are used for every one.\n\n" +
                           "This takes about as long as porting once for each attire.")) return;
        string baseId = (ModIdBox.Text ?? "").Trim(), baseName = (ModNameBox.Text ?? "").Trim();
        var done = new List<string>();
        var failed = new List<string>();
        ReportText.Text = "";
        await Work($"Porting {attires.Count} attires of {chosen.Name}…", async ct =>
        {
            for (int i = 0; i < attires.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string att = attires[i];
                string tag = System.Text.RegularExpressions.Regex.Replace(att.Replace('/', '_'), "[^A-Za-z0-9]+", "_").Trim('_').ToLowerInvariant();
                var req = PortRequest(game);
                req["attire"] = att;
                req["mod_id"] = (baseId.Length > 0 ? baseId : "mod") + "_" + tag;
                req["mod_name"] = (baseName.Length > 0 ? baseName : chosen.Name) + " (" + att + ")";
                string what = $"attire {i + 1} of {attires.Count}: {att}";
                Status("Porting " + what + "…");
                try
                {
                    var r = await Core.Engine.Run(python, req, line => Dispatcher.UIThread.Post(() => Status(what + " - " + line)), ct);
                    lastMod = r.GetProperty("out").GetString();
                    lastPages = r.GetProperty("pages").GetInt32();
                    Ported(r);
                    lastShare = r.TryGetProperty("share_zip", out var sz2) && sz2.ValueKind == JsonValueKind.String ? sz2.GetString() : null;
                    done.Add(att + "  ->  " + lastMod);
                    ReportText.Text += $"•  {att}: ported ({lastPages} pages)\n";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    failed.Add(att + ": " + e.Message.Split('\n')[0]);
                    ReportText.Text += $"•  {att}: FAILED - {e.Message.Split('\n')[0]}\n";
                }
            }
            if (done.Count > 0)
                OpenButton.IsEnabled = InstallButton.IsEnabled = AttireButton.IsEnabled = View3DButton.IsEnabled = ReplaceButton.IsEnabled = ShareButton.IsEnabled = true;
            ReportText.Text = $"{done.Count} of {attires.Count} attires ported.\n\n" + ReportText.Text +
                              (failed.Count > 0 ? "\nThe ones that failed are listed above. Port those on their own to see why." : "") +
                              "\n\nEach attire is its own mod folder. To put several on ONE superstar, use \"Install as attire…\" for each of them.";
            Status(failed.Count == 0 ? $"Done: all {done.Count} attires ported." : $"Done: {done.Count} ported, {failed.Count} failed.");
        });
    }

    // ------------------------------------------------------------------ share
    /// <summary>The one file to send someone: the zip with the model AND its HD textures, which the port already makes.
    /// It used to be a sentence in the report that people missed (2026-11-18).</summary>
    void ShareLast()
    {
        if (lastShare != null && File.Exists(lastShare))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + lastShare + "\"") { UseShellExecute = true });
            Status("Send this one file: " + Path.GetFileName(lastShare) + "  (the model AND its HD textures)");
            return;
        }
        if (lastMod != null)
        {
            System.Diagnostics.Process.Start("explorer.exe", lastMod);
            Status("This port has no share zip (it was made without the HD texture pack) - send the .svrmod from this folder.");
            return;
        }
        Status("Port a wrestler first.");
    }

    void Open3D()
    {
        bool hasPort = lastMod != null && File.Exists(Path.Combine(lastMod, "ch.pac"));
        // The request is built HERE, on the window's own thread. It reads the boxes and the ticks, and Avalonia
        // refuses to be read from another thread - doing it inside the function below (which the viewer runs on a
        // background thread) threw, and the 3D view came up empty with the reason hidden in its bottom line
        // (2026-11-18).
        Func<Viewer3D.Scene>? source = null;
        if (chosen != null)
        {
            var req = Request("source_preview");
            var o = Current;
            if (o == entrance && o.Attire != null) req["attire"] = o.Attire;
            req["leave_out"] = LeaveOut(o);
            req["use"] = Use(o);
            source = () => Core.Engine.SourceScene(req, CancellationToken.None).GetAwaiter().GetResult();
        }
        if (!hasPort && source == null) return;
        new ViewerWindow(hasPort ? lastMod : null, chosen?.Name ?? (lastMod != null ? Path.GetFileName(lastMod) : "model"),
                         entrance.BuiltPic != null, onEntrance,
                         (SweatBox.SelectedItem as SweatChoice)?.Shine ?? 0, source).Show(this);
    }

    /// <summary>A newer PortIT on GitHub? Then show what is new and ask (UpdateDialog). Only an installed copy -
    /// never a developer's build - and never during the screenshot runs. No internet, GitHub down, a timeout: says
    /// nothing at all. "Skip this version" is remembered (2026-11-19).</summary>
    async Task CheckForUpdate()
    {
        string root = AppContext.BaseDirectory;
        bool test = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PORTIT_UPDATE_FEED"));
        if (SkipAnnounce || (!PortIT.Engine.Update.Updater.IsInstalledCopy(root) && !test)) return;
        PortIT.Engine.Update.Updater.CleanUp(root);           // (the files the last update could not delete while running)
        PortIT.Engine.Update.Updater.Release? rel;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            rel = await PortIT.Engine.Update.Updater.CheckAsync(typeof(App).Assembly.GetName().Version ?? new Version(0, 0), cts.Token);
        }
        catch (Exception) { return; }
        if (rel == null || LoadSetting("skip_version") == rel.Tag) return;
        var dlg = new UpdateDialog(rel);
        await dlg.ShowDialog(this);
        if (dlg.Choice == "skip") SaveSetting("skip_version", rel.Tag);
    }

    void ShowPreview(string? path, string title)
    {
        if (path == null || !File.Exists(path)) return;
        using var s = File.OpenRead(path);
        PreviewImage.Source = new Bitmap(s);
        PreviewEmpty.IsVisible = false;
        PreviewTitle.Text = title;
    }

    async Task Install()
    {
        if (lastMod == null) return;
        string game = GameBox.Text?.Trim().Trim('"') ?? "";
        string id = Path.GetFileName(lastMod);
        string dest = Path.Combine(game, "Mods", "Superstars", id);
        bool ok = await Confirm($"Copy this mod into the game?\n\n{dest}\n\n" +
                                (Directory.Exists(dest) ? "A mod with this id is there: it is moved to Mods\\_portit_backup first." : "Nothing there is replaced."));
        if (!ok) return;
        await Work("Installing…", async ct =>
        {
            var r = await Core.Engine.Run(python, new() { ["command"] = "install", ["mod_dir"] = lastMod, ["game"] = game, ["mod_id"] = id }, null, ct);
            string msg = "Installed: " + r.GetProperty("installed").GetString();
            if (r.TryGetProperty("pack", out var pk))
            {
                msg += "  ·  HD textures: " + pk.GetString();
                if (r.TryGetProperty("pack_turned_on", out var pt) && pt.GetBoolean())
                    msg += "  (PortIT turned on the texture pack \"PortIT HD\" for you)";
                else if (r.TryGetProperty("pack_enabled", out var pe) && !pe.GetBoolean())
                    msg += "  (turn on the texture pack \"PortIT HD\" in the launcher's Texture packs list)";
            }
            Status(msg);
        });
    }

    /// <summary>
    /// Put the port over a superstar the game already has, in the game's OWN pac\ch\chNNN.pac - "update John Cena's
    /// model". Not an attire mod: he looks new in every attire and every menu, and keeps his name, stats, moves and
    /// entrance. The superstar is the body type the port was built on, because PortIT names a model's entries after
    /// its base, which is what makes the swap possible. (user request 2026-11-16)
    /// </summary>
    async Task ReplaceInGame()
    {
        if (lastMod == null) return;
        string game = GameBox.Text?.Trim().Trim('"') ?? "";
        int builtOn = (StyleBox.SelectedItem as Style)?.Base ?? 139;
        string builtOnName = (StyleBox.SelectedItem as Style)?.Label ?? ("superstar " + builtOn);
        List<StarSlots>? stars = null;
        await Work("Reading the game's superstars…", async ct =>
            stars = StarSlots.From(await Core.Engine.Run(python, new() { ["command"] = "stars", ["game"] = game }, null, ct)));
        if (stars == null || stars.Count == 0) { Status("No superstars found in the game folder above."); return; }

        var chosen2 = await new ReplaceDialog(stars, builtOn, builtOnName).ShowDialog<StarSlots?>(this);
        if (chosen2 == null) return;
        int star = chosen2.Id;
        string who = chosen2.Name;
        if (!File.Exists(Path.Combine(game, "pac", "ch", $"ch{star}.pac")))
        { Status($"ch{star}.pac is not in the game's pac\\ch folder - {who} has no model to replace."); return; }

        await Work("Replacing…", async ct =>
        {
            var r = await Core.Engine.Run(python, new() { ["command"] = "replace_star", ["mod_dir"] = lastMod, ["game"] = game, ["star"] = star }, null, ct);
            var swapped = r.GetProperty("replaced").EnumerateArray().Select(x => x.GetString()).ToList();
            string msg = $"{who}: {swapped.Count} model(s) replaced in the game";
            if (r.TryGetProperty("kept", out var kept) && kept.GetArrayLength() > 0)
                msg += $", {kept.GetArrayLength()} of the game's own kept";
            msg += "  ·  the original is in Mods\\_portit_backup";
            if (r.TryGetProperty("pack", out var pk)) msg += "  ·  HD textures: " + pk.GetString();
            foreach (var w in r.GetProperty("warnings").EnumerateArray()) msg += "\n" + w.GetString();
            Status(msg);
        });
    }

    /// <summary>the port as an attire of a superstar the game has (Mods\Attires: manifest + attire.pac) - the Mod Maker's
    /// Attires page: pick the superstar and the attire (new / replace), its CHANGE ATTIRE name, entrance, author</summary>
    async Task InstallAttire()
    {
        if (lastMod == null) return;
        string game = GameBox.Text?.Trim().Trim('"') ?? "";
        List<StarSlots>? stars = null;
        await Work("Reading the game's superstars…", async ct =>
            stars = StarSlots.From(await Core.Engine.Run(python, new() { ["command"] = "stars", ["game"] = game }, null, ct)));
        if (stars == null || stars.Count == 0) { Status("No superstars found in the game folder above."); return; }
        string guess = Path.GetFileName(lastMod).Replace('_', ' ').ToUpperInvariant();
        var choice = await new AttireDialog(stars, guess, lastPages).ShowDialog<AttireChoice?>(this);
        if (choice == null) return;
        await Work("Installing the attire…", async ct =>
        {
            var r = await Core.Engine.Run(python, new()
            {
                ["command"] = "install_attire", ["mod_dir"] = lastMod, ["game"] = game, ["star"] = choice.Star.Id, ["star_name"] = choice.Star.Name,
                ["slot"] = choice.Slot, ["name"] = choice.Name, ["entrance"] = choice.Entrance, ["author"] = choice.Author, ["version"] = choice.Version,
            }, null, ct);
            string msg = $"Installed as an attire of {choice.Star.Name}: " + r.GetProperty("installed").GetString() + "  ·  on CHANGE ATTIRE from the next game start";
            if (r.TryGetProperty("backup", out var bk)) msg += "  ·  the older copy: " + bk.GetString();
            if (r.TryGetProperty("warnings", out var wn))     // (the engine's own rule checks: an attire another mod uses)
                foreach (var w in wn.EnumerateArray()) msg += "  ·  NOTE: " + w.GetString();
            if (r.TryGetProperty("pack", out var pk))
            {
                msg += "  ·  HD textures: " + pk.GetString();
                if (r.TryGetProperty("pack_turned_on", out var pt) && pt.GetBoolean())
                    msg += "  (PortIT turned on the texture pack \"PortIT HD\" for you)";
                else if (r.TryGetProperty("pack_enabled", out var pe) && !pe.GetBoolean())
                    msg += "  (turn on the texture pack \"PortIT HD\" in the launcher's Texture packs list)";
            }
            Status(msg);
        });
    }

    /// <summary>the model memory the game has: port 2.1.6+ writes "big models: N pages a wrestler" in its log (the
    /// full-detail route + texture pack); else the DLL's bigmodel.log names the vertex window ("768 -> 2048 KB");
    /// neither = the classic 768 KB window (190 pages)</summary>
    static int DetectBudget(string? game)
    {
        try
        {
            string logs = Path.Combine(game ?? "", "logs");
            if (Directory.Exists(logs))
            {
                var last = new DirectoryInfo(logs).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (last != null)
                {
                    using var fs = new FileStream(last.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var rd = new StreamReader(fs);
                    var m1 = System.Text.RegularExpressions.Regex.Match(rd.ReadToEnd(), @"big models: (\d+) pages a wrestler");
                    if (m1.Success && int.Parse(m1.Groups[1].Value) >= 1500) return 2;
                }
            }
            string log = Path.Combine(game ?? "", "bigmodel.log");
            if (!File.Exists(log)) return 0;
            var m = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(log), @"vertex windows[^:]*: *\d+ *-> *(\d+) *KB");
            if (m.Count == 0) return 0;
            int kb = int.Parse(m[^1].Groups[1].Value);
            return kb >= 2000 ? 1 : 0;
        }
        catch { return 0; }
    }

    // ------------------------------------------------------------------ helpers
    async Task Work(string what, Func<CancellationToken, Task> job)
    {
        cts?.Cancel();
        var mine = cts = new CancellationTokenSource();
        Busy.IsVisible = true;
        Status(what);
        try { await job(mine.Token); }
        catch (OperationCanceledException) { }
        catch (Exception) when (mine.IsCancellationRequested) { }   // (replaced by a newer job: not its error)
        catch (Exception e) { Status("Error: " + e.Message.Split('\n')[0]); ReportText.Text = e.Message; }
        finally { if (ReferenceEquals(cts, mine)) Busy.IsVisible = false; }
    }

    void Status(string s) => StatusText.Text = s;

    async Task<string?> PickFolder(string title)
    {
        var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return r.Count > 0 ? r[0].TryGetLocalPath() : null;
    }

    async Task<bool> Confirm(string text)
    {
        var yes = new Button { Content = "Yes, copy it", Classes = { "accent" } };
        var no = new Button { Content = "Cancel" };
        var dlg = new Window
        {
            Title = "PortIT", Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20), Spacing = 16,
                Children = { new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                             new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { no, yes } } },
            },
        };
        bool result = false;
        yes.Click += (_, _) => { result = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        return result;
    }

    internal static string? LoadSetting(string key)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsPath))?.GetValueOrDefault(key); }
        catch { return null; }
    }

    internal static void SaveSetting(string key, string value)
    {
        try
        {
            Dictionary<string, string> d;
            try { d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsPath)) ?? new(); } catch { d = new(); }
            d[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(d));
        }
        catch { }
    }
}
