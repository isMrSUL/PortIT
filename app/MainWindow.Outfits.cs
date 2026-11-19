using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using PortIT.Core;

namespace PortIT;

/// <summary>
/// The ring attire and the entrance outfit (people's request 2026-11-11: SvR 2011 has both, the Mod Maker makes both):
/// each outfit has its own meshes (ticked = worn), its own alternate looks (2K's M_Hair_Entrance, M_HoodUp...) and its
/// own picture, before and after porting; the switch over the preview shows one or the other. The port sends the ring's
/// choice as leave_out / use and the entrance outfit's as 'entrance' (EMD kind 4 in ch.pac).
/// </summary>
public partial class MainWindow
{
    public sealed record EntranceChoice(string Label, bool On, string? Attire)
    {
        public override string ToString() => Label;
    }

    sealed class Outfit
    {
        public string? Attire;                  // (the entrance outfit's other attire; null: the ring attire's)
        public readonly ObservableCollection<MeshItem> Meshes = new();
        public string? SourcePic, BuiltPic, BuiltTitle;
        public int Verts;
    }

    readonly Outfit ring = new(), entrance = new();
    bool onEntrance, swapping;
    JsonElement? ringInspect;
    readonly HashSet<string> autoOff = new();     // (ring pieces PortIT took off because the entrance outfit wears them)

    Outfit Current => onEntrance ? entrance : ring;
    EntranceChoice? EntranceMode => EntranceBox.SelectedItem as EntranceChoice;
    bool HasEntrance => EntranceMode?.On == true;
    string OutfitName => !HasEntrance ? $"{SourceLabel} model" : onEntrance ? "entrance outfit" : "ring attire";

    void InitOutfits()
    {
        OutfitRing.Click += async (_, _) => await ShowOutfit(false);
        OutfitEntrance.Click += async (_, _) => await ShowOutfit(true);
        EntranceBox.SelectionChanged += async (_, _) => { if (!filling && chosen != null) await EntranceChanged(); };
        SyncOutfitUi();
    }

    void ResetOutfits()
    {
        foreach (var o in new[] { ring, entrance }) { o.Meshes.Clear(); o.SourcePic = o.BuiltPic = o.BuiltTitle = null; o.Attire = null; o.Verts = 0; }
        autoOff.Clear();
        ForgetScene();
        ringInspect = null;
        onEntrance = false;
        SyncOutfitUi();
    }

    void FillEntrance(WrestlerItem w)
    {
        var items = new List<EntranceChoice>
        {
            new("None - walks out in the ring attire", false, null),
            new("This attire with all its gear (choose below what comes off for the ring)", true, null),
        };
        if (Source == "2k26")
            foreach (var a in w.Attires) items.Add(new($"Another attire: {a}", true, a));
        EntranceBox.ItemsSource = items;
        EntranceBox.SelectedIndex = 0;
        ResetOutfits();
    }

    // ------------------------------------------------------------------ the meshes of an outfit
    void Fill(Outfit o, JsonElement r, bool entranceDefaults)
    {
        o.Meshes.Clear();
        var all = r.GetProperty("meshes").EnumerateArray().ToList();
        // (alternates only of pieces that can come off - clothes, gear, hair: M_Head_Caruncle is no other head)
        var plain = all.Where(m => !m.GetProperty("variant").GetBoolean() && m.GetProperty("kind").GetString() is "gear" or "hair")
                       .Select(m => m.GetProperty("material").GetString()!).ToList();
        int verts = 0;
        foreach (var m in all)
        {
            string mat = m.GetProperty("material").GetString()!;
            string kind = m.GetProperty("kind").GetString()!;
            bool variant = m.GetProperty("variant").GetBoolean();
            int v = m.GetProperty("verts").GetInt32();
            verts += v;
            string tex = string.Join(", ", m.GetProperty("textures").EnumerateArray().Select(t => t.GetString()));
            // (an alternate look - M_Hair_Entrance of M_Hair: can be used instead of its base; the cloth-physics copies
            //  S_..., lambert / eyeshadow leftovers stay out)
            string? baseOf = variant && mat.StartsWith("M_", StringComparison.OrdinalIgnoreCase)
                ? plain.Where(b => mat.StartsWith(b + "_", StringComparison.OrdinalIgnoreCase)).OrderByDescending(b => b.Length).FirstOrDefault() : null;
            bool canRemove = baseOf != null || (!variant && kind is "gear" or "hair");
            string label = baseOf != null ? kind + " · alternate" : variant ? kind + " · left out" : kind;
            // (a greyed tick box that does nothing when clicked reads as "the Meshes section is broken" - and with
            //  M_Hair, S_Hair, M_Hair_ENT and S_Hair_ENT the list shows FOUR rows called Hair, only one of which comes
            //  off. So every row that has no tick box now says why, right under its name. 2026-11-18)
            string? why = canRemove ? null
                : variant ? "the game does not use this copy - it is already left out"
                : kind is "head" or "skin" ? "the body itself - it cannot be left out"
                : kind == "eye" ? "the eyes - they cannot be left out"
                : kind == "lash" ? "the eyelashes - they cannot be left out"
                : "part of the wrestler, not gear - it cannot be left out";
            o.Meshes.Add(new MeshItem(mat, label, $"{v,6:N0} verts   {tex}", canRemove, baseOf, why) { Keep = !variant });
        }
        o.Verts = verts;
        if (entranceDefaults)        // (2K's own entrance pieces: used, their everyday ones not)
            foreach (var alt in o.Meshes.Where(x => x.Base != null && MeshNames.IsEntranceVersion(x.Name)).ToList())
            {
                alt.Keep = true;
                foreach (var b in o.Meshes.Where(x => x.Name.Equals(alt.Base, StringComparison.OrdinalIgnoreCase))) b.Keep = false;
            }
        foreach (var it in o.Meshes) it.KeepChanged = x => { Swap(o, x); if (o == Current) QueuePreview(); };
    }

    int previewGen;

    /// <summary>Tick or untick a piece and the picture follows by itself - no "Update preview" click (2026-11-18).
    /// Ticking several pieces in a row only draws once: each tick cancels the one waiting.</summary>
    void QueuePreview()
    {
        if (chosen == null) return;
        int gen = ++previewGen;
        Dispatcher.UIThread.Post(async () =>
        {
            try { await Task.Delay(450); } catch (Exception) { return; }
            if (gen != previewGen || chosen == null) return;
            await SourcePreview();
        });
    }

    /// <summary>an alternate look and its base are worn one at a time: ticking one unticks the other(s)</summary>
    void Swap(Outfit o, MeshItem it)
    {
        if (o == ring && it.Keep) autoOff.Remove(it.Name);
        if (swapping || !it.Keep) return;
        swapping = true;
        try
        {
            string group = it.Base ?? it.Name;
            foreach (var x in o.Meshes)
                if (x != it && (x.Name.Equals(group, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Base, group, StringComparison.OrdinalIgnoreCase)))
                    x.Keep = false;
        }
        finally { swapping = false; }
    }

    static string[] LeaveOut(Outfit o) => o.Meshes.Where(m => m.CanRemove && !m.Keep).Select(m => m.Name).Distinct().ToArray();
    static string[] Use(Outfit o) => o.Meshes.Where(m => m.Base != null && m.Keep).Select(m => m.Name).Distinct().ToArray();

    void SyncOutfitUi()
    {
        OutfitBar.IsVisible = HasEntrance;
        if (!HasEntrance) onEntrance = false;
        OutfitRing.IsChecked = !onEntrance;
        OutfitEntrance.IsChecked = onEntrance;
        MeshList.ItemsSource = Current.Meshes;
        MeshHeader.Text = !HasEntrance ? "Meshes" : onEntrance ? "Meshes - entrance outfit" : "Meshes - ring attire";
        MeshCount.Text = Current.Meshes.Count == 0 ? "" : $"{Current.Meshes.Count} meshes, {Current.Verts:N0} vertices";
    }

    // ------------------------------------------------------------------ reading the wrestler, the entrance outfit
    async Task Inspect()
    {
        if (chosen == null) return;
        var w = chosen;
        await Work($"Reading {w.Name}…", async ct =>
        {
            var r = await Core.Engine.Run(python, Request("inspect"), null, ct);
            ringInspect = r;
            autoOff.Clear();
            Fill(ring, r, false);
            ring.BuiltPic = null;
            ring.SourcePic = r.TryGetProperty("preview", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString() : null;
            Status($"{w.Name}: {ring.Meshes.Count} meshes.");
            SourcePreviewButton.IsEnabled = true;
            View3DButton.IsEnabled = true;           // (the 2K model can be turned in 3D before porting, 2026-11-18)
            CopyPicButton.IsEnabled = SavePicButton.IsEnabled = true;
            if (HasEntrance) await PrepareEntrance(ct);
            SyncOutfitUi();
            ShowCurrent();
            WarmScene();
        });
    }

    /// <summary>the entrance outfit for the choice made: this attire (all its gear, 2K's entrance pieces used, the coats
    /// and hats taken off the ring attire) or another attire (read)</summary>
    async Task PrepareEntrance(CancellationToken ct)
    {
        foreach (var n in autoOff.ToList())
            foreach (var m in ring.Meshes.Where(x => x.Name == n)) m.Keep = true;
        autoOff.Clear();
        entrance.BuiltPic = null;
        var mode = EntranceMode;
        if (mode is not { On: true }) { entrance.Meshes.Clear(); onEntrance = false; return; }
        if (mode.Attire == null || mode.Attire == AttireBox.SelectedItem as string)
        {
            entrance.Attire = null;
            if (ringInspect is JsonElement r) Fill(entrance, r, true);
            entrance.SourcePic = null;
            var off = new List<string>();
            foreach (var m in ring.Meshes.Where(x => x.Base == null && x.CanRemove && x.Keep && MeshNames.LooksEntranceGear(x.Name)))
            {
                m.Keep = false;
                autoOff.Add(m.Name);
                off.Add(m.Label);
            }
            Status(off.Count > 0 ? $"Ring attire: {string.Join(", ", off)} taken off (worn only for the entrance) - tick it again to keep it on in the ring."
                                 : "Entrance outfit: this attire with all its gear. Untick in the ring attire what comes off for the match.");
        }
        else
        {
            entrance.Attire = mode.Attire;
            var req = Request("inspect");
            req["attire"] = mode.Attire;
            var r = await Core.Engine.Run(python, req, null, ct);
            Fill(entrance, r, true);
            entrance.SourcePic = r.TryGetProperty("preview", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString() : null;
        }
    }

    async Task EntranceChanged()
    {
        if (chosen == null) return;
        await Work("Setting up the entrance outfit…", async ct =>
        {
            await PrepareEntrance(ct);
            onEntrance = HasEntrance;
            SyncOutfitUi();
        });
        if (HasEntrance) await ShowOutfit(true);
        else ShowCurrent();
    }

    async Task ShowOutfit(bool entranceSide)
    {
        onEntrance = entranceSide && HasEntrance;
        SyncOutfitUi();
        // Always draw it again. It used to keep whatever picture that outfit had last, which is why changing the
        // entrance outfit, or switching between ring and entrance, left the old picture on screen until "Update
        // preview" was clicked. Drawing again is cheap now: the model is kept and only the ticks change (2026-11-18).
        if (Current.BuiltPic != null) ShowCurrent();
        else if (chosen != null) await SourcePreview();
        else ShowCurrent();
    }

    void ShowCurrent()
    {
        var o = Current;
        if (o.BuiltPic != null) ShowPreview(o.BuiltPic, o.BuiltTitle ?? "");
        else if (o.SourcePic != null && chosen != null) ShowPreview(o.SourcePic, $"{chosen.Name}  ·  {OutfitName} (before porting)");
    }

    // ------------------------------------------------------------------ pictures
    /// <summary>the model this picture is drawn from, kept between draws, and what it was built for</summary>
    PortIT.Engine.Port.Viewer3D.Scene? scene;
    string? sceneKey;

    /// <summary>Which model the kept scene belongs to. The ticks are NOT part of it: every piece, and every alternate
    /// look, is in the scene and simply hidden or shown - so ticking a box, switching outfit or choosing another
    /// entrance outfit costs a redraw (about a third of a second) instead of reading and decoding the whole model
    /// again (over two seconds). That is what made "Update preview" feel necessary.</summary>
    string SceneKey(Outfit o) => Source + "|" + (chosen?.Id ?? "") + "|" + (o == entrance && o.Attire != null ? o.Attire : AttireBox.SelectedItem as string ?? "");

    public void ForgetScene() { scene = null; sceneKey = null; }

    /// <summary>the model the share picture is drawn from: the built port when this outfit has been ported (with its HD
    /// textures), else the 2K model with the pieces as they are ticked now</summary>
    async Task<PortIT.Engine.Port.Viewer3D.Scene?> ShareSceneFor(Outfit o, CancellationToken ct)
    {
        bool ent = o == entrance;
        string? pac = lastMod != null ? System.IO.Path.Combine(lastMod, "ch.pac") : null;
        if (o.BuiltPic != null && pac != null && System.IO.File.Exists(pac))
        {
            string mod = lastMod!;
            return await Task.Run(() => PortIT.Engine.Port.Viewer3D.Load(pac, ent ? 1 : 0, PortIT.Engine.Port.Viewer3D.HdColor(mod, ent)), ct);
        }
        string key = SceneKey(o);
        if (scene == null || sceneKey != key)
        {
            var req = Request("source_preview");
            if (ent && o.Attire != null) req["attire"] = o.Attire;
            req["leave_out"] = Array.Empty<string>();
            req["use"] = o.Meshes.Where(m => m.Base != null).Select(m => m.Name).Distinct().ToArray();
            scene = await Core.Engine.SourceScene(req, ct);
            sceneKey = key;
        }
        var hide = LeaveOut(o).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in scene.Parts) { p.Hidden = hide.Contains(p.Name); p.Highlight = false; }
        PortIT.Engine.Port.SourceScene.Measure(scene);
        return scene;
    }

    /// <summary>"Copy picture" / "Save picture…": a clean picture made for sharing (people post their ports in
    /// Discord - the user's request 2026-11-19). Front, side and back, the name on top, no debug caption.</summary>
    async Task SharePicture(bool save)
    {
        if (chosen == null) { Status("Pick a wrestler first."); return; }
        var w = chosen;
        var o = Current;
        bool ported = o.BuiltPic != null;
        string title = (ModNameBox.Text ?? "").Trim();
        if (title.Length == 0) title = w.Name;
        string sub = ported ? (o == entrance ? "entrance outfit  ·  " : "") + "ported to SvR 2011" : OutfitName + "  ·  before porting";
        byte[]? png = null;
        await Work("Making the picture…", async ct =>
        {
            var s = await ShareSceneFor(o, ct);
            if (s == null) return;
            png = await Task.Run(() => PortIT.Engine.Port.Preview.ShareScene(s, null, title, sub), ct);
        });
        if (png == null) return;
        if (!save)
        {
            Status(Core.ClipImage.SetPng(png)
                ? "Picture copied - paste it in Discord (or anywhere) with Ctrl+V."
                : "Could not reach the clipboard (another program has it) - try again, or use Save picture….");
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save the picture",
            SuggestedFileName = System.Text.RegularExpressions.Regex.Replace(title, "[^A-Za-z0-9 _-]+", "").Trim() + (ported ? "" : " - before porting") + ".png",
            DefaultExtension = "png",
            FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("PNG picture") { Patterns = new[] { "*.png" } } },
        });
        string? path = file?.TryGetLocalPath();
        if (path == null) return;
        System.IO.File.WriteAllBytes(path, png);
        Status("Saved " + System.IO.Path.GetFileName(path));
    }

    /// <summary>Read the model in the background as soon as a wrestler is chosen, so the FIRST tick or outfit change is
    /// as quick as the ones after it. Nothing on screen changes; if it fails, the next draw simply reads it then.</summary>
    void WarmScene()
    {
        if (chosen == null) return;
        string key = SceneKey(ring);
        if (scene != null && sceneKey == key) return;
        var req = Request("source_preview");
        req["leave_out"] = Array.Empty<string>();
        req["use"] = ring.Meshes.Where(m => m.Base != null).Select(m => m.Name).Distinct().ToArray();
        _ = Task.Run(async () =>
        {
            try
            {
                var s = await Core.Engine.SourceScene(req, CancellationToken.None);
                Dispatcher.UIThread.Post(() => { if (scene == null && SceneKey(ring) == key) { scene = s; sceneKey = key; } });
            }
            catch (Exception) { }
        });
    }

    /// <summary>the outfit drawn again with its ticks - before porting; highlight: one mesh in orange ("Show")</summary>
    async Task SourcePreview(string? highlight = null)
    {
        if (chosen == null) return;
        var w = chosen;
        var o = Current;
        string key = SceneKey(o);
        // a piece left out is still drawn when it is the one being shown in orange
        var hide = LeaveOut(o).Where(n => highlight == null || !n.Equals(highlight, StringComparison.OrdinalIgnoreCase))
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await Work(highlight != null ? $"Showing {MeshNames.Friendly(highlight)}…" : $"Drawing {w.Name}…", async ct =>
        {
            if (scene == null || sceneKey != key)
            {
                var req = Request("source_preview");
                if (o == entrance && o.Attire != null) req["attire"] = o.Attire;
                req["leave_out"] = Array.Empty<string>();
                // every alternate look is built, so ticking one is only a change of what is hidden
                req["use"] = o.Meshes.Where(m => m.Base != null).Select(m => m.Name).Distinct().ToArray();
                scene = await Core.Engine.SourceScene(req, ct);
                sceneKey = key;
            }
            var s = scene;
            foreach (var p in s.Parts)
            {
                p.Hidden = hide.Contains(p.Name);
                p.Highlight = highlight != null && string.Equals(p.Name, highlight, StringComparison.OrdinalIgnoreCase);
            }
            // one file per outfit, written over each time. With a new name per draw the folder filled up with a
            // megabyte-sized picture for every tick (the picture is drawn again several times a minute now).
            // ShowPreview decodes the file and lets go of it, so writing over it is safe.
            string outPath = System.IO.Path.Combine(PortIT.Engine.Worker.Previews,
                                                    highlight != null ? "view_show.png" : o == entrance ? "view_entrance.png" : "view_ring.png");
            await Task.Run(() => PortIT.Engine.Port.Preview.DrawScene(s, outPath, ""), ct);
            ct.ThrowIfCancellationRequested();
            if (highlight == null) { o.SourcePic = outPath; o.BuiltPic = null; }
            ShowPreview(outPath, $"{w.Name}  ·  {OutfitName}" + (highlight != null ? $"  ·  {MeshNames.Friendly(highlight)} in orange" : "  (before porting, your edit)"));
            Status(highlight != null ? $"{MeshNames.Friendly(highlight)} ({highlight}) is shown in orange." : $"{w.Name}: picture updated.");
        });
    }

    void OnShowMesh(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string name) _ = SourcePreview(name);
    }

    // ------------------------------------------------------------------ the port
    void AddOutfits(Dictionary<string, object?> req)
    {
        req["leave_out"] = LeaveOut(ring);
        var use = Use(ring);
        if (use.Length > 0) req["use"] = use;
        if (!HasEntrance) return;
        var e = new Dictionary<string, object?> { ["leave_out"] = LeaveOut(entrance), ["use"] = Use(entrance) };
        if (entrance.Attire != null) e["attire"] = entrance.Attire;
        req["entrance"] = e;
    }

    void Ported(JsonElement r)
    {
        string name = chosen?.Name ?? "";
        ring.BuiltPic = r.GetProperty("preview").GetString();
        ring.BuiltTitle = $"{name}  ·  {(HasEntrance ? "ring attire" : "built")}  ·  {r.GetProperty("pages").GetInt32()} pages";
        if (r.TryGetProperty("entrance_preview", out var ep) && ep.ValueKind == JsonValueKind.String)
        {
            entrance.BuiltPic = ep.GetString();
            entrance.BuiltTitle = $"{name}  ·  entrance outfit  ·  {(r.TryGetProperty("entrance_pages", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0)} pages";
        }
        ShowCurrent();
    }
}
