using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using VrcPhotoManager.Data;
using VrcPhotoManager.Models;
using VrcPhotoManager.Services;
using VrcPhotoManager.ViewModels;

namespace VrcPhotoManager.Views;

public partial class MetadataWindow : Window
{
    public MetadataWindow(PhotoViewModel photo, PhotoRepository repo)
    {
        InitializeComponent();
        DialogWindowBehavior.HideMinimizeAndMaximizeButtons(this);
        DialogWindowBehavior.CloseOnDeactivated(this);
        DialogWindowBehavior.OpenNearCursor(this);
        PreviewKeyDown += MetadataWindow_PreviewKeyDown;
        var m = photo.Model;

        var doc = new FlowDocument { FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12 };
        var p = new Paragraph();

        p.Inlines.Add(new Run($"File:        {m.FileName}\n"));
        p.Inlines.Add(new Run($"Path:        {m.LocalPath}\n"));
        p.Inlines.Add(new Run($"Size:        {m.FileSize / 1024.0:N0} KB\n"));
        p.Inlines.Add(new Run($"Dimensions:  {(m.Width is int w && m.Height is int h ? $"{w}x{h}" : "not scanned")}\n"));
        p.Inlines.Add(new Run($"File hash:   {m.FileHash ?? "not computed"}\n\n"));
        p.Inlines.Add(new Run($"Rating:      {m.Rating ?? "(unclassified)"}\n\n"));

        p.Inlines.Add(new Run("Author:      "));
        p.Inlines.Add(VrcLink(m.AuthorId, m.AuthorDisplayName ?? "(no VRCX metadata)", "https://vrchat.com/home/user/{0}"));
        p.Inlines.Add(new Run("\n"));

        p.Inlines.Add(new Run("World:       "));
        p.Inlines.Add(VrcLink(m.WorldId, m.WorldName ?? "-", "https://vrchat.com/home/world/{0}"));
        if (m.WorldNameInferred) p.Inlines.Add(new Run(" (inferred from gamelog)"));
        p.Inlines.Add(new Run("\n"));

        p.Inlines.Add(new Run("Players:\n"));
        var players = repo.GetPlayersForPhoto(m.Id);
        if (players.Count == 0)
        {
            p.Inlines.Add(new Run("  -\n"));
        }
        else
        {
            foreach (var player in players)
            {
                p.Inlines.Add(new Run("  "));
                p.Inlines.Add(VrcLink(player.UserId, player.DisplayName, "https://vrchat.com/home/user/{0}"));
                p.Inlines.Add(new Run("\n"));
            }
        }
        p.Inlines.Add(new Run("\n"));

        AppendTraveledTogether(p, m, repo);

        p.Inlines.Add(new Run($"Upload status: {m.RemoteStatus}\n"));
        p.Inlines.Add(new Run("Remote URL:    "));
        p.Inlines.Add(m.RemoteUrl is string remoteUrl ? DirectLink(remoteUrl) : new Run("-"));
        p.Inlines.Add(new Run("\n"));
        p.Inlines.Add(new Run($"Uploaded at:   {m.UploadedAt ?? "-"}\n"));
        p.Inlines.Add(new Run($"Uploaded as:   {m.UploadCropMode ?? "-"}"));

        doc.Blocks.Add(p);
        MetadataText.Document = doc;
    }

    /// <summary>Best-effort "who did I travel here with" via GamelogCorrelationService's paired
    /// departure/arrival correlation (see its FindTraveledTogether doc comment for the actual
    /// matching rules) - degrades silently (adds nothing to the document) if VRCX isn't
    /// available, this photo's capture time can't be parsed from its filename, or the gamelog
    /// has no preceding visit to compare against. This is a nice-to-have inference, not a
    /// correctness-critical field like the rest of this window, so it never surfaces as an
    /// error - just absent.</summary>
    private static void AppendTraveledTogether(Paragraph p, Photo m, PhotoRepository repo)
    {
        if (GamelogCorrelationService.TryParseCaptureTime(m.LocalPath) is not DateTime captureTime) return;
        using var gamelog = GamelogCorrelationService.TryCreate(out _);
        if (gamelog is null) return;

        double windowSeconds = repo.GetDoubleSetting(SettingsKeys.PortalHopWindowSeconds, 90);
        var traveled = gamelog.FindTraveledTogether(captureTime, TimeSpan.FromSeconds(windowSeconds));
        if (traveled is not { Count: > 0 }) return;

        p.Inlines.Add(new Run("Traveled together:\n"));
        foreach (var (userId, displayName) in traveled)
        {
            p.Inlines.Add(new Run("  "));
            p.Inlines.Add(VrcLink(userId, displayName, "https://vrchat.com/home/user/{0}"));
            p.Inlines.Add(new Run("\n"));
        }
        p.Inlines.Add(new Run("\n"));
    }

    /// <summary>A clickable VRChat website link when id is available, plain text otherwise -
    /// never a broken/empty link. urlTemplate takes exactly one {0} placeholder for the id.</summary>
    private static Inline VrcLink(string? id, string? displayText, string urlTemplate)
    {
        if (id is null) return new Run(displayText ?? "-");
        return DirectLink(string.Format(urlTemplate, id), displayText ?? id);
    }

    /// <summary>A clickable link to an already-complete URL (as opposed to VrcLink, which builds
    /// the URL from a template + id) - used for the VRCDN remote URL, which has no id/template
    /// split.</summary>
    private static Hyperlink DirectLink(string url, string? displayText = null)
    {
        var link = new Hyperlink(new Run(displayText ?? url)) { NavigateUri = new Uri(url) };
        link.Click += (_, _) => Process.Start(new ProcessStartInfo(link.NavigateUri.ToString()) { UseShellExecute = true });
        return link;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Escape closes the window outright - same rationale/precedent as
    /// TagFacesWindow_PreviewKeyDown, and nothing else in this read-only window needs Escape
    /// for anything else.</summary>
    private void MetadataWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
