using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PortIT;

/// <summary>a superstar an attire can go to (engine command "stars")</summary>
public sealed record StarSlots(int Id, string Name, int[] Attires, bool Gear, Dictionary<int, string> Mods)
{
    public override string ToString() => Name;

    public static List<StarSlots> From(JsonElement r) => r.GetProperty("stars").EnumerateArray().Select(s => new StarSlots(
        s.GetProperty("id").GetInt32(), s.GetProperty("name").GetString() ?? "",
        s.GetProperty("attires").EnumerateArray().Select(a => a.GetInt32()).ToArray(), s.GetProperty("gear").GetBoolean(),
        s.GetProperty("mods").EnumerateObject().ToDictionary(p => int.Parse(p.Name), p => p.Value.GetString() ?? ""))).ToList();

    /// <summary>the lowest attire neither the superstar's own nor asked for by a mod (0: none; none for entrance gear)</summary>
    public int NextFree() => Gear ? 0 : Enumerable.Range(1, 4).FirstOrDefault(a => !Attires.Contains(a) && !Mods.ContainsKey(a));
}

public sealed record AttireChoice(StarSlots Star, string Slot, string Name, string Entrance, string Author, string Version);

/// <summary>"Install as attire": the port as an attire of a superstar the game has - the Mod Maker's Attires page
/// (port/modmaker/attire_page.cpp) with the same rules: a new attire takes the next free one (4 at most), a superstar
/// with entrance gear (Cena, Punk, Undertaker, HBK, Morrison) can only have their own attires replaced, an attire an
/// installed attire mod asks for clashes. The name shows on CHANGE ATTIRE; the superstar's pictures stay.</summary>
public sealed class AttireDialog : Window
{
    readonly ComboBox star = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly StackPanel slots = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
    readonly TextBlock now = new() { Classes = { "faint" }, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock info = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBox name = new() { Watermark = "e.g. 2003 GEAR", MaxLength = 31 };
    readonly TextBox entrance = new() { Watermark = "the superstar's (or an entrance number)" };
    readonly TextBox author = new();
    readonly TextBox version = new() { Text = "1.0" };
    readonly Button install = new() { Content = "Install", Classes = { "accent" }, MinWidth = 100 };
    string slot = "new";

    public AttireDialog(List<StarSlots> stars, string defaultName, int pages)
    {
        Title = "Install as an attire";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        star.ItemsSource = stars;
        name.Text = defaultName.Length > 31 ? defaultName[..31] : defaultName;
        var cancel = new Button { Content = "Cancel", MinWidth = 100 };
        cancel.Click += (_, _) => Close(null);
        install.Click += (_, _) =>
        {
            if (star.SelectedItem is StarSlots s)
                Close(new AttireChoice(s, slot, (name.Text ?? "").Trim(), (entrance.Text ?? "").Trim(), (author.Text ?? "").Trim(),
                                       string.IsNullOrWhiteSpace(version.Text) ? "1.0" : version.Text.Trim()));
        };
        star.SelectionChanged += (_, _) =>
        {
            if (star.SelectedItem is StarSlots s) slot = s.Gear ? (s.Attires.Contains(1) ? "1" : "new") : "new";
            Refresh();
        };
        Grid Row(string label, Control c)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*") };
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Classes = { "label" } });
            Grid.SetColumn(c, 1);
            g.Children.Add(c);
            return g;
        }
        Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Install as an attire", Classes = { "h" } },
                new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "faint" },
                                Text = "The ported model becomes an attire of a superstar the game has: a new attire, or one replacing an attire. " +
                                       "It shows on CHANGE ATTIRE; the superstar's pictures stay." +
                                       (pages > 0 ? $"  (This port: {pages} memory pages - an attire has the same budget as a superstar.)" : "") },
                Row("Superstar", star),
                now,
                slots,
                info,
                Row("Name on CHANGE ATTIRE", name),
                Row("Entrance (optional)", entrance),
                Row("Author (optional)", author),
                Row("Version (optional)", version),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, install } },
            },
        };
        star.SelectedIndex = stars.Count > 0 ? 0 : -1;
        Refresh();
    }

    /// <summary>(screenshots: Render.Attire) the superstar whose name is `name`</summary>
    internal void ShotPick(string name)
    {
        foreach (var o in star.ItemsSource!)
            if (o is StarSlots s && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { star.SelectedItem = s; break; }
    }

    void Refresh()
    {
        slots.Children.Clear();
        if (star.SelectedItem is not StarSlots s)
        {
            install.IsEnabled = false;
            info.Text = "";
            now.Text = "";
            return;
        }
        int next = s.NextFree();
        now.Text = $"Attires the game has: {(s.Attires.Length > 0 ? string.Join(", ", s.Attires) : "none")} (of 4)" +
                   (s.Mods.Count > 0 ? "  ·  attire mods: " + string.Join(", ", s.Mods.Select(m => $"{m.Key} \"{m.Value}\"")) : "");
        void Radio(string text, string value, bool enabled, string tip)
        {
            var rb = new RadioButton { Content = text, GroupName = "attire_slot", IsChecked = slot == value, IsEnabled = enabled };
            ToolTip.SetTip(rb, tip);
            rb.IsCheckedChanged += (_, _) => { if (rb.IsChecked == true) { slot = value; Refresh(); } };
            slots.Children.Add(rb);
        }
        Radio("A new attire", "new", !s.Gear && next > 0, "The next free attire (4 at most).");
        for (int a = 1; a <= 4; ++a)
        {
            bool exists = s.Attires.Contains(a), modded = s.Mods.ContainsKey(a);
            if (s.Gear && !exists) continue;           // (entrance gear: their own attires only)
            Radio((exists ? "Replace " : "Attire ") + a + (modded ? " (a mod's)" : exists ? "" : " (free?)"), a.ToString(), true,
                  modded ? $"An installed attire mod, \"{s.Mods[a]}\", asks for attire {a}." : exists ? $"The game's own attire {a}: this replaces it." : $"Attire {a} is free.");
        }
        // what will happen (the Mod Maker's messages)
        var warn = new List<string>();
        string ok;
        if (slot == "new")
        {
            if (s.Gear) { ok = ""; warn.Add($"{s.Name} wears entrance gear: new attires for them don't load in the game yet - replace attire {(s.Attires.Length > 1 ? "1-" + s.Attires.Max() : "1")} instead."); }
            else if (next == 0) { ok = ""; warn.Add("No attire is free (4 at most): replace one instead."); }
            else ok = $"It becomes attire {next} (the next free one).";
        }
        else
        {
            int a = int.Parse(slot);
            ok = s.Attires.Contains(a) ? $"Replaces the game's attire {a}." : $"The superstar has no attire {a} now: this adds it as attire {a}.";
            if (s.Gear)     // (why "A new attire" is off for them - users asked themselves)
                ok += $"\n{s.Name} wears entrance gear (a coat / hat / shirt for the entrance): a new attire never loads for them, so only their own attires can be replaced.";
            if (s.Mods.TryGetValue(a, out var other))
                warn.Add($"Attire {a} is already used by \"{other}\" (installed): of two mods on one attire the first folder (A-Z) keeps it" +
                         (s.Gear ? " - switch one of them off." : next > 0 ? $"; attire {next} is free." : "."));
        }
        info.Text = string.Join("\n", new[] { ok }.Concat(warn).Where(t => t.Length > 0));
        info.Foreground = warn.Count > 0 ? new SolidColorBrush(Color.Parse("#FFD27A")) : Brushes.LightGreen;
        install.IsEnabled = ok.Length > 0;
    }
}
