using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SkiaSharp;

namespace PortIT;

/// <summary>
/// "Suggest an idea": an idea for PortIT, with an optional example picture, sent privately to SUL's
/// #portit-suggestions Discord channel through the same relay as bug reports (Core.Relay - its address is not in the
/// source, and the webhook lives on the relay). Free: Discord takes the picture as an attachment. The picture is
/// shrunk to at most 1600 px and re-encoded here, so a phone photo or a 4K screenshot always fits, and what is sent
/// is only ever a picture PortIT itself encoded. (The user's request 2026-11-19.)
/// </summary>
public sealed class SuggestDialog : Window
{
    const string RelayKey = "portit-report-v1";
    const int MaxSide = 1600;

    readonly TextBox idea = new()
    {
        Watermark = "Your idea - e.g. \"a dark/light theme switch\", \"port many wrestlers at once\", \"show the hair in the preview\"",
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 130, MaxLength = 1900,
    };
    readonly TextBox contact = new() { Watermark = "Optional - your Discord name, if SUL may ask you about it" };
    readonly Button pick = new() { Content = "Add a picture…" };
    readonly Button drop = new() { Content = "Remove picture", IsVisible = false };
    readonly Image thumb = new() { MaxHeight = 140, HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
    readonly TextBlock picInfo = new() { Classes = { "faint" }, VerticalAlignment = VerticalAlignment.Center };
    readonly Button send = new() { Content = "Send", Classes = { "accent" }, MinWidth = 100 };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    byte[]? picture;

    public SuggestDialog()
    {
        Title = "Suggest an idea";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var cancel = new Button { Content = "Cancel", MinWidth = 100 };
        cancel.Click += (_, _) => Close();
        send.Click += async (_, _) => await Send();
        pick.Click += async (_, _) => await Pick();
        drop.Click += (_, _) => SetPicture(null, null);

        Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Suggest an idea", Classes = { "h" } },
                new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "faint" },
                                Text = "Something PortIT should do, or do better? Your idea goes privately to SUL, the maker of PortIT. " +
                                       "A picture helps - a sketch, a screenshot, an example from another tool." },
                idea,
                contact,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { pick, drop, picInfo } },
                thumb,
                new TextBlock { Classes = { "faint" }, TextWrapping = TextWrapping.Wrap,
                                Text = "Sent with it: PortIT " + ReportDialog.AppVersion + ". For a bug, use Report a problem… instead - it needs the model." },
                status,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, send } },
            },
        };
    }

    async Task Pick()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "A picture to show your idea",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Pictures") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif" } } },
        });
        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        try
        {
            var (bytes, w, h) = await Task.Run(() => Shrink(path));
            SetPicture(bytes, $"{Path.GetFileName(path)}  ·  {w} x {h}  ·  {bytes.Length / 1024} KB");
        }
        catch (Exception) { Say("That file could not be read as a picture.", true); }
    }

    /// <summary>a picture at most MaxSide on its long side: PNG when small enough (sharp text in screenshots), else JPEG</summary>
    static (byte[] Bytes, int W, int H) Shrink(string path)
    {
        using var src = SKBitmap.Decode(path) ?? throw new InvalidDataException("not a picture");
        double k = Math.Min(1.0, (double)MaxSide / Math.Max(src.Width, src.Height));
        int w = Math.Max(1, (int)Math.Round(src.Width * k)), h = Math.Max(1, (int)Math.Round(src.Height * k));
        using var bmp = k < 1.0 ? src.Resize(new SKImageInfo(w, h), SKFilterQuality.High) : src.Copy();
        using var img = SKImage.FromBitmap(bmp);
        using (var png = img.Encode(SKEncodedImageFormat.Png, 100))
            if (png.Size <= 2_500_000) return (png.ToArray(), w, h);
        using var jpg = img.Encode(SKEncodedImageFormat.Jpeg, 85);
        return (jpg.ToArray(), w, h);
    }

    void SetPicture(byte[]? bytes, string? info)
    {
        picture = bytes;
        drop.IsVisible = thumb.IsVisible = bytes != null;
        picInfo.Text = info ?? "";
        pick.Content = bytes == null ? "Add a picture…" : "Change picture…";
        if (bytes != null) { using var ms = new MemoryStream(bytes); thumb.Source = new Bitmap(ms); }
        else thumb.Source = null;
    }

    async Task Send()
    {
        string text = (idea.Text ?? "").Trim();
        if (text.Length < 10) { Say("Write your idea in a few words.", true); return; }
        if (Core.Relay.Address is not string relay) { Say(Core.Relay.NotInThisBuild, true); return; }
        send.IsEnabled = false;
        Say(picture != null ? "Sending (with the picture)…" : "Sending…", false);
        var body = new System.Collections.Generic.Dictionary<string, string>
        {
            ["kind"] = "suggestion", ["idea"] = text, ["version"] = ReportDialog.AppVersion, ["contact"] = (contact.Text ?? "").Trim(),
        };
        if (picture != null) body["image"] = Convert.ToBase64String(picture);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var msg = new HttpRequestMessage(HttpMethod.Post, relay)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            msg.Headers.Add("X-PortIT-Key", RelayKey);
            using var res = await http.SendAsync(msg);
            string answer = await res.Content.ReadAsStringAsync();
            string said = "";
            try { said = JsonDocument.Parse(answer).RootElement.GetProperty("message").GetString() ?? ""; } catch (JsonException) { }
            if (res.IsSuccessStatusCode)
            {
                Say((said.Length > 0 ? said : "Suggestion sent.") + " You can close this window.", false);
                return;
            }
            Say(said.Length > 0 ? said : $"It was not sent (error {(int)res.StatusCode}). Try again later.", true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Say("Could not reach the server. Check your internet connection and try again.", true);
        }
        send.IsEnabled = true;
    }

    void Say(string text, bool bad)
    {
        status.Text = text;
        status.Foreground = bad ? new SolidColorBrush(Color.Parse("#FFD27A")) : Brushes.LightGreen;
    }
}
