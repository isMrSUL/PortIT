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
    public sealed class MeshItem
    {
        public MeshItem(string name, string kind, string detail, bool canRemove) { Name = name; Kind = kind; Detail = detail; CanRemove = canRemove; }
        public string Name { get; }
        public string Kind { get; }
        public string Detail { get; }
        public bool CanRemove { get; }
        public bool Keep { get; set; } = true;
    }
    public sealed record Budget(string Label, int Pages)
    {
        public override string ToString() => Label;
    }

    static readonly string SettingsPath = Path.Combine(Engine.WorkRoot, "settings.json");
    readonly List<WrestlerItem> all = new();
    readonly ObservableCollection<WrestlerItem> shown = new();
    readonly ObservableCollection<MeshItem> meshes = new();
    WrestlerItem? chosen;
    string? lastMod;
    int lastPages;
    CancellationTokenSource? cts;
    bool filling;
    readonly string python = LoadSetting("python") ?? Engine.DefaultPython;

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
        InstallButton.Click += async (_, _) => await Install();
        AttireButton.Click += async (_, _) => await InstallAttire();
        SourcePreviewButton.Click += async (_, _) => await SourcePreview();
        GameBrowse.Click += async (_, _) => { if (await PickFolder("Choose the SvR 2011 game folder") is string p) { GameBox.Text = p; SaveSetting("game", p); } };
        SourceBrowse.Click += async (_, _) => { if (await PickFolder(SrcAEW.IsChecked == true ? "Choose AEW's paks folder (…\\content\\paks)" : "Choose the folder with the 2K26 wrestlers") is string p) { SourceBox.Text = p; } };
        OutBrowse.Click += async (_, _) => { if (await PickFolder("Choose where PortIT writes the mods") is string p) { OutBox.Text = p; SaveSetting("out", p); } };
        BudgetBox.SelectionChanged += (_, _) => SaveSetting("budget", BudgetBox.SelectedIndex.ToString());
        Opened += async (_, _) => { if (Directory.Exists(SourceBox.Text)) await LoadList(); };
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
        SourceBox.Watermark = aew ? "AEW's paks folder (…\\CUSA34641\\elite\\content\\paks)" : "Folder with the 2K26 wrestlers";
        SourceBox.Text = LoadSetting(aew ? "aewPath" : "2kPath") ?? "";
        SaveSetting("source", aew ? "aew" : "2k26");
        all.Clear();
        shown.Clear();
        meshes.Clear();
        CountText.Text = "nothing loaded";
    }

    string Source => SrcAEW.IsChecked == true ? "aew" : "2k26";
    string SourceLabel => Source == "aew" ? "AEW" : "2K";

    void SetFemale(bool female)
    {
        MaleChip.IsChecked = !female;
        FemaleChip.IsChecked = female;
    }

    async Task LoadList()
    {
        string path = SourceBox.Text?.Trim().Trim('"') ?? "";
        if (!Directory.Exists(path)) { Status("Choose a source folder first."); return; }
        SaveSetting(Source == "aew" ? "aewPath" : "2kPath", path);
        await Work("Reading the wrestlers…", async ct =>
        {
            var r = await Engine.Run(python, new() { ["command"] = "list", ["source"] = Source, ["path"] = path }, null, ct);
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
        }
        finally { filling = false; }
        await Inspect();
    }

    async Task Inspect()
    {
        if (chosen == null) return;
        var w = chosen;
        await Work($"Reading {w.Name}…", async ct =>
        {
            var req = Request("inspect");
            var r = await Engine.Run(python, req, null, ct);
            meshes.Clear();
            int verts = 0;
            foreach (var m in r.GetProperty("meshes").EnumerateArray())
            {
                int v = m.GetProperty("verts").GetInt32();
                verts += v;
                bool variant = m.GetProperty("variant").GetBoolean();
                string tex = string.Join(", ", m.GetProperty("textures").EnumerateArray().Select(t => t.GetString()));
                string kind = m.GetProperty("kind").GetString()!;
                // (clothes, gear and hair can be left out; the body, head, eyes, lashes and mouth parts can't)
                bool canRemove = !variant && kind is "gear" or "hair";
                meshes.Add(new(m.GetProperty("material").GetString()!, kind + (variant ? " · left out" : ""),
                               $"{v,6:N0} verts   {tex}", canRemove && !variant));
            }
            MeshCount.Text = $"{meshes.Count} meshes, {verts:N0} vertices";
            Status($"{w.Name}: {meshes.Count} meshes.");
            // (the 2K model as it is, before porting: who it is, what it wears)
            if (r.TryGetProperty("preview", out var pv) && pv.ValueKind == JsonValueKind.String)
                ShowPreview(pv.GetString(), $"{w.Name}  ·  {SourceLabel} model (before porting)");
            SourcePreviewButton.IsEnabled = true;   // (2K26 and AEW both drawn before porting)
        });
    }

    /// <summary>the 2K model drawn again without the unticked meshes - how he looks with that edit, before porting</summary>
    async Task SourcePreview()
    {
        if (chosen == null) return;
        var w = chosen;
        await Work($"Drawing {w.Name}…", async ct =>
        {
            var req = Request("source_preview");
            req["leave_out"] = meshes.Where(m => m.CanRemove && !m.Keep).Select(m => m.Name).Distinct().ToArray();
            var r = await Engine.Run(python, req, null, ct);
            if (r.TryGetProperty("preview", out var pv) && pv.ValueKind == JsonValueKind.String)
                ShowPreview(pv.GetString(), $"{w.Name}  ·  {SourceLabel} model (before porting, your edit)");
            Status($"{w.Name}: preview updated.");
        });
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
        string outDir = OutBox.Text?.Trim().Trim('"') ?? "";
        SaveSetting("out", outDir);
        var req = Request("port");
        req["game"] = game;
        req["out"] = outDir;
        req["female"] = FemaleChip.IsChecked == true;
        req["budget"] = (BudgetBox.SelectedItem as Budget)?.Pages ?? 190;
        req["face"] = FaceCheck.IsChecked == true;
        req["mod_id"] = (ModIdBox.Text ?? "").Trim();
        req["mod_name"] = (ModNameBox.Text ?? "").Trim();
        // (the unticked clothes / gear: left out of the port)
        req["leave_out"] = meshes.Where(m => m.CanRemove && !m.Keep).Select(m => m.Name).Distinct().ToArray();
        ReportText.Text = "";
        await Work($"Porting {chosen.Name}…", async ct =>
        {
            var r = await Engine.Run(python, req, line => Dispatcher.UIThread.Post(() => { ReportText.Text += line + "\n"; Status(line); }), ct);
            lastMod = r.GetProperty("out").GetString();
            ReportText.Text = string.Join("\n", r.GetProperty("report").EnumerateArray().Select(x => "•  " + x.GetString()));
            ShowPreview(r.GetProperty("preview").GetString(), $"{chosen.Name}  ·  {r.GetProperty("pages").GetInt32()} pages");
            lastPages = r.GetProperty("pages").GetInt32();
            OpenButton.IsEnabled = InstallButton.IsEnabled = AttireButton.IsEnabled = true;
            Status($"Done: {lastMod}");
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
            var r = await Engine.Run(python, new() { ["command"] = "install", ["mod_dir"] = lastMod, ["game"] = game, ["mod_id"] = id }, null, ct);
            string msg = "Installed: " + r.GetProperty("installed").GetString();
            if (r.TryGetProperty("pack", out var pk))
            {
                msg += "  ·  HD textures: " + pk.GetString();
                if (r.TryGetProperty("pack_enabled", out var pe) && !pe.GetBoolean())
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
            stars = StarSlots.From(await Engine.Run(python, new() { ["command"] = "stars", ["game"] = game }, null, ct)));
        if (stars == null || stars.Count == 0) { Status("No superstars found in the game folder above."); return; }
        string guess = Path.GetFileName(lastMod).Replace('_', ' ').ToUpperInvariant();
        var choice = await new AttireDialog(stars, guess, lastPages).ShowDialog<AttireChoice?>(this);
        if (choice == null) return;
        await Work("Installing the attire…", async ct =>
        {
            var r = await Engine.Run(python, new()
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
                if (r.TryGetProperty("pack_enabled", out var pe) && !pe.GetBoolean())
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
