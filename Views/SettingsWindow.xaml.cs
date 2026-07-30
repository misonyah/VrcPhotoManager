using System.Windows;
using Microsoft.Win32;
using VrcPhotoManager.Data;
using VrcPhotoManager.Services;

namespace VrcPhotoManager.Views;

public partial class SettingsWindow : Window
{
    private readonly PhotoRepository _repo;
    private readonly ModelDownloadService _downloader = new();
    private CancellationTokenSource? _downloadCts;

    public SettingsWindow(PhotoRepository repo)
    {
        InitializeComponent();
        _repo = repo;
        ModelDirTextBox.Text = _repo.GetStringSetting(SettingsKeys.WdModelDir) ?? "";
        ClipModelDirTextBox.Text = _repo.GetStringSetting(SettingsKeys.ClipModelDir) ?? "";
        AutoCopyUrlCheckBox.IsChecked = _repo.GetBoolSetting(SettingsKeys.AutoCopyVrcdnUrlOnHover);
    }

    private void BrowseModelDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select WD14 model folder" };
        if (dialog.ShowDialog() == true)
        {
            ModelDirTextBox.Text = dialog.FolderName;
        }
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        string targetDir = ModelDirTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            MessageBox.Show(this, "Enter or browse to a model folder first (it will be created if it doesn't exist).",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DownloadButton.IsEnabled = false;
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => DownloadStatusText.Text = msg);
        try
        {
            await _downloader.DownloadWdTaggerModelAsync(targetDir, progress, _downloadCts.Token);
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            DownloadStatusText.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            DownloadButton.IsEnabled = true;
        }
    }

    private void BrowseClipModelDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select CLIP model folder" };
        if (dialog.ShowDialog() == true)
        {
            ClipModelDirTextBox.Text = dialog.FolderName;
        }
    }

    private async void DownloadClipModel_Click(object sender, RoutedEventArgs e)
    {
        string targetDir = ClipModelDirTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            MessageBox.Show(this, "Enter or browse to a model folder first (it will be created if it doesn't exist).",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DownloadClipButton.IsEnabled = false;
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => DownloadClipStatusText.Text = msg);
        try
        {
            await _downloader.DownloadClipModelAsync(targetDir, progress, _downloadCts.Token);
        }
        catch (OperationCanceledException)
        {
            DownloadClipStatusText.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            DownloadClipStatusText.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            DownloadClipButton.IsEnabled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ModelDirTextBox.Text))
        {
            _repo.SetStringSetting(SettingsKeys.WdModelDir, ModelDirTextBox.Text.Trim());
        }
        if (!string.IsNullOrWhiteSpace(ClipModelDirTextBox.Text))
        {
            _repo.SetStringSetting(SettingsKeys.ClipModelDir, ClipModelDirTextBox.Text.Trim());
        }
        _repo.SetBoolSetting(SettingsKeys.AutoCopyVrcdnUrlOnHover, AutoCopyUrlCheckBox.IsChecked == true);
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        DialogResult = false;
        Close();
    }
}
