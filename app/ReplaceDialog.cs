using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PortIT;

/// <summary>
/// "Replace this superstar in the game": pick which superstar the game already has gets this model.
///
/// Not an attire mod - the game's own pac\ch\chNNN.pac is rewritten, so he looks new in every attire and
/// every menu while keeping his name, stats, moves and entrance. Any port can replace any superstar: the
/// model's entries are renamed to the chosen superstar's id, which is what the game itself does for
/// superstar mods. Porting on that superstar's body type is still the exact fit for size, so the dialog
/// says so when they differ. (user request 2026-11-16)
/// </summary>
public sealed class ReplaceDialog : Window
{
    readonly ComboBox star = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 420 };
    readonly TextBlock note = new() { Classes = { "faint" }, TextWrapping = TextWrapping.Wrap };
    readonly Button go = new() { Content = "Replace", Classes = { "accent" }, MinWidth = 100 };

    public ReplaceDialog(List<StarSlots> stars, int builtOn, string builtOnName)
    {
        Title = "Replace a superstar in the game";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        star.ItemsSource = stars;
        int pick = stars.FindIndex(s => s.Id == builtOn);
        star.SelectedIndex = pick >= 0 ? pick : 0;

        void Refresh()
        {
            if (star.SelectedItem is not StarSlots s) return;
            string t = $"The game's {s.Name} gets this model. They keep their name, stats, moves and entrance, and look " +
                       "new in every attire and every menu.\n\n" +
                       "This changes the game's own file (pac\\ch\\ch" + s.Id + ".pac). PortIT copies the original into " +
                       "Mods\\_portit_backup first, so it can be put back. Close the game before you do this.";
            if (s.Id != builtOn)
                t += $"\n\nThis port was posed on {builtOnName}. It still works - PortIT renames the model's entries - " +
                     $"but {s.Name}'s own size is used, so check they do not come out too big or too small. " +
                     $"Porting again with the body type set to {s.Name} is the exact fit.";
            note.Text = t;
        }
        star.SelectionChanged += (_, _) => Refresh();
        Refresh();

        var cancel = new Button { Content = "Cancel", MinWidth = 100 };
        cancel.Click += (_, _) => Close(null);
        go.Click += (_, _) => Close(star.SelectedItem as StarSlots);

        Content = new Border
        {
            Padding = new Avalonia.Thickness(20),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Replace a superstar in the game", Classes = { "h" } },
                    new TextBlock
                    {
                        Classes = { "faint" }, TextWrapping = TextWrapping.Wrap,
                        Text = "Put this model over one of the game's own superstars. This is not a new attire: the " +
                               "superstar themselves gets the new model.",
                    },
                    new TextBlock { Text = "Superstar", Classes = { "label" } },
                    star,
                    note,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, go },
                    },
                },
            },
        };
    }
}
