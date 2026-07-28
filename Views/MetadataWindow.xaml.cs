using System.Windows;
using VrcdnManager.ViewModels;

namespace VrcdnManager.Views;

public partial class MetadataWindow : Window
{
    public MetadataWindow(PhotoViewModel photo)
    {
        InitializeComponent();
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
            $"Author:      {m.AuthorDisplayName ?? "(no VRCX metadata)"}",
            $"World:       {m.WorldName ?? "-"}",
            $"Players:     {m.PlayerNames ?? "-"}",
            "",
            $"Upload status: {m.RemoteStatus}",
            $"Remote URL:    {m.RemoteUrl ?? "-"}",
            $"Uploaded at:   {m.UploadedAt ?? "-"}",
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
