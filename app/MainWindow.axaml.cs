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
using PortIT.Core;

namespace PortIT;

public partial class MainWindow : Window
{
    public sealed record WrestlerItem(string Id, string Name, string Badge, string Detail, bool Female, string[] Attires);
    /// <summary>a mesh of the chosen attire; Keep: ticked = ported (clothes / gear / hair can be unticked: left out of
    /// the port, the skin under them kept)</summary>
    public sealed class MeshItem : System.ComponentModel.INotifyPropertyChanged
    {
        public MeshItem(string name, string kind, string detail, bool canRemove, string? baseOf = null)
        {
            Name = name; Kind = kind; Detail = detail; CanRemove = canRemove; Base = baseOf; Label = MeshNames.Friendly(name);
        }
        public string Name { get; }
        /// <summary>the name in plain words (Left knee pad)</summary>
        public string Label { get; }
        public string Kind { get; }
        public string Detail { get; }
        public bool CanRemove { get; }
        /// <summary>an alternate look of another mesh (M_Hair_Entrance of M_Hair): ticked, it is used instead of that one</summary>
        public string? Base { get; }
        public string TickTip => Base != null ? $"An alternate look of {MeshNames.Friendly(Base)}: tick to use it instead"
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

    static readonly string SettingsPath = Path.Combine(Core.Engine.WorkRoot, "settings.json");
    readonly List<WrestlerItem> all = new();
    readonly ObservableCollection<WrestlerItem> shown = new();
    ObservableCollection<MeshItem> meshes => ring.Meshes;
    WrestlerItem? chosen;
    string? lastMod;
    int lastPages;
    CancellationTokenSource? cts;
    bool filling;
    readonly string python = LoadSetting("python") ?? Core.Engine.DefaultPython;

    public MainWindow()
    {
        InitializeComponent();
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
        GameBox.Text = LoadSetting("game") ?? "";
        BudgetBox.SelectedIndex = int.TryParse(LoadSetting("budget"), out int b) ? b : DetectBudget(GameBox.Text);
        GameBox.LostFocus += (_, _) => BudgetBox.SelectedIndex = DetectBudget(GameBox.Text);
        OutBox.Text = LoadSetting("out") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PortIT");
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
        OpenButton.Click += (_, _) => { if (lastMod != null) System.Diagnostics.Process.Start("explorer.exe", lastMod); };
        void Open3D()
        {
            if (lastMod == null || !File.Exists(Path.Combine(lastMod, "ch.pac"))) return;
            new ViewerWindow(lastMod, chosen?.Name ?? Path.GetFileName(lastMod), entrance.BuiltPic != null, onEntrance).Show(this);
        }
        View3DButton.Click += (_, _) => Open3D();
        // (people click the picture to turn it: a click on the built model's picture opens the 3D view too)
        PreviewImage.PointerPressed += (_, _) => { if (View3DButton.IsEnabled && PreviewTitle.Text?.Contains("pages") == true) Open3D(); };
        InstallButton.Click += async (_, _) => await Install();
        AttireButton.Click += async (_, _) => await InstallAttire();
        SourcePreviewButton.Click += async (_, _) => await SourcePreview();
        GameBrowse.Click += async (_, _) => { if (await PickFolder("Choose the SvR 2011 game folder") is string p) { GameBox.Text = p; SaveSetting("game", p); } };
        SourceBrowse.Click += async (_, _) => { if (await PickFolder(SrcAEW.IsChecked == true ? "Choose AEW's paks folder (…\\content\\paks)" : "Choose a 2K folder: a 2K26 install, a mods folder or one wrestler") is string p) { SourceBox.Text = p; } };
        OutBrowse.Click += async (_, _) => { if (await PickFolder("Choose where PortIT writes the mods") is string p) { OutBox.Text = p; SaveSetting("out", p); } };
        BudgetBox.SelectionChanged += (_, _) => { SaveSetting("budget", BudgetBox.SelectedIndex.ToString()); HdBox.IsEnabled = FullDetail; };
        HdBox.IsEnabled = FullDetail;
        BigModelButton.Click += async (_, _) => await BigModel();
        OpenModelButton.Click += async (_, _) => await OpenModel();
        Opened += async (_, _) => { if (Directory.Exists(SourceBox.Text)) await LoadList(); };
        InitOutfits();
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
    }

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
        string outDir = OutBox.Text?.Trim().Trim('"') ?? "";
        SaveSetting("out", outDir);
        var req = Request("port");
        req["game"] = game;
        req["out"] = outDir;
        req["female"] = FemaleChip.IsChecked == true;
        req["budget"] = (BudgetBox.SelectedItem as Budget)?.Pages ?? 190;
        req["hd_size"] = (HdBox.SelectedItem as HdSize)?.Width ?? 4096;
        req["face"] = FaceCheck.IsChecked == true;
        req["moving_hair"] = HairCheck.IsChecked == true;
        req["mod_id"] = (ModIdBox.Text ?? "").Trim();
        req["mod_name"] = (ModNameBox.Text ?? "").Trim();
        // (each outfit's unticked pieces left out, its ticked alternate looks used; the entrance outfit a second model)
        AddOutfits(req);
        ReportText.Text = "";
        await Work($"Porting {chosen.Name}…", async ct =>
        {
            var r = await Core.Engine.Run(python, req, line => Dispatcher.UIThread.Post(() => { ReportText.Text += line + "\n"; Status(line); }), ct);
            lastMod = r.GetProperty("out").GetString();
            ReportText.Text = string.Join("\n", r.GetProperty("report").EnumerateArray().Select(x => "•  " + x.GetString()));
            Ported(r);
            lastPages = r.GetProperty("pages").GetInt32();
            OpenButton.IsEnabled = InstallButton.IsEnabled = AttireButton.IsEnabled = View3DButton.IsEnabled = true;
            Status($"Done: {lastMod}");
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
        });
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
