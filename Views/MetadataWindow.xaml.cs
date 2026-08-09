using System.Windows;
using VrcPhotoManager.ViewModels;

namespace VrcPhotoManager.Views;

public partial class MetadataWindow : Window
{
    public MetadataWindow(PhotoViewModel photo)
    {
        InitializeComponent();
        DialogWindowBehavior.HideMinimizeAndMaximizeButtons(this);
        DialogWindowBehavior.CloseOnDeactivated(this);
        DialogWindowBehavior.OpenNearCursor(this);
        var m = photo.Model;
        MetadataText.Text = string.Join("\n", new[]
        {
            $"File:        {m.FileName}",
            $"Path:        {m.LocalPath}",
            $"Size:        {m.FileSize / 1024.0:N0} KB",
            $"Dimensions:  {(m.Width is int w && m.Height is int h ? $"{w}x{h}" : "not scanned")}",
            $"File hash:   {m.FileHash ?? "not computed"}",
            "",
            $"Rating:      {m.Rating ?? "(unclassified)"}",
            "",
            $"Author:      {m.AuthorDisplayName ?? "(no VRCX metadata)"}{(m.AuthorId is null ? "" : $" ({m.AuthorId})")}",
            $"World:       {m.WorldName ?? "-"}{(m.WorldNameInferred ? " (inferred from gamelog)" : "")}",
            "Players:",
            m.PlayerNames ?? "  -",
            "",
            $"Upload status: {m.RemoteStatus}",
            $"Remote URL:    {m.RemoteUrl ?? "-"}",
            $"Uploaded at:   {m.UploadedAt ?? "-"}",
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
