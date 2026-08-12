using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using VrcPhotoManager.Data;
using VrcPhotoManager.Services;
using VrcPhotoManager.ViewModels;

namespace VrcPhotoManager.Views;

public partial class SettingsWindow : Window
{
    private readonly PhotoRepository _repo;
    private readonly ModelDownloadService _downloader = new();
    private CancellationTokenSource? _downloadCts;
    private bool _isDownloading;

    public SettingsWindow(PhotoRepository repo)
    {
        InitializeComponent();
        DialogWindowBehavior.HideMinimizeAndMaximizeButtons(this);
        // A download in progress must not be silently orphaned: the accidental "click the
        // main window while a download is running" case is blocked outright (stillOpenGuard
        // skips the close instead of letting it happen), while a deliberate close (X button,
        // Alt+F4, Cancel button) is still allowed through but cancels the download first via
        // the Closing handler below - either way nothing keeps running on the thread pool
        // with no UI left to show progress or cancel it.
        DialogWindowBehavior.CloseOnDeactivated(this, stillOpenGuard: () => _isDownloading);
        DialogWindowBehavior.OpenNearCursor(this);
        Closing += (_, _) => _downloadCts?.Cancel();
        _repo = repo;

        // WD14 alone has a legacy bundled-next-to-exe folder some installs already rely on
        // silently (no Settings value needed) - only suggest the new %LOCALAPPDATA% default
        // when that folder isn't present, so an existing working setup's textbox doesn't
        // start showing an unrelated path.
        string wdLegacyFolder = Path.Combine(AppContext.BaseDirectory, "wd14-model");
        string wdSuggestedDefault = Directory.Exists(wdLegacyFolder) ? "" : DefaultModelPaths.WdTagger;

        // A saved-but-empty setting (the user cleared the box and saved) is treated the same
        // as "never configured" - IsNullOrWhiteSpace, not a plain null check - so clearing a
        // path and saving restores the suggested default next time this window opens, rather
        // than leaving the box permanently blank with no way back to the default without
        // retyping it.
        string? savedWdDir = _repo.GetStringSetting(SettingsKeys.WdModelDir);
        ModelDirTextBox.Text = string.IsNullOrWhiteSpace(savedWdDir) ? wdSuggestedDefault : savedWdDir;

        string? savedClipDir = _repo.GetStringSetting(SettingsKeys.ClipModelDir);
        ClipModelDirTextBox.Text = string.IsNullOrWhiteSpace(savedClipDir) ? DefaultModelPaths.Clip : savedClipDir;

        string? savedAvatarDir = _repo.GetStringSetting(SettingsKeys.AvatarModelDir);
        AvatarModelDirTextBox.Text = string.IsNullOrWhiteSpace(savedAvatarDir) ? DefaultModelPaths.Avatar : savedAvatarDir;

        AutoCopyUrlCheckBox.IsChecked = _repo.GetBoolSetting(SettingsKeys.AutoCopyVrcdnUrlOnHover);
        HoverDelaySlider.Value = _repo.GetDoubleSetting(SettingsKeys.HoverPreviewDelaySeconds, 0.25);
        SkipResolvedPhotosCheckBox.IsChecked = _repo.GetBoolSetting(SettingsKeys.SkipResolvedPhotosOnFaceScan, true);

        DownloadStatusText.Text = GetModelStatusText(ModelDirTextBox.Text, "model.onnx", "selected_tags.csv");
        DownloadClipStatusText.Text = GetModelStatusText(ClipModelDirTextBox.Text, "model.onnx");
        DownloadAvatarStatusText.Text = GetModelStatusText(AvatarModelDirTextBox.Text, "model.onnx", "labels.txt");

        CropPresetsList.ItemsSource = MainViewModel.UploadCropPresets.Select(DescribeCropPreset).ToList();
    }

    /// <summary>Name + a human-readable ratio/example-resolution line for the read-only crop-
    /// presets reference panel - reads MainViewModel.UploadCropPresets directly (not a separate
    /// hardcoded list) so this can't silently drift out of sync with the real preset values.</summary>
    private static (string Name, string Detail) DescribeCropPreset(MainViewModel.UploadCropPreset preset)
    {
        if (preset.AspectRatio is not double ratio)
        {
            return (preset.Name, "Uploads at the photo's own resolution, uncropped.");
        }

        // Same cap ThumbnailService.PrepareForUploadAsync actually applies - the larger side
        // hits UploadMaxSide exactly, the other side follows the ratio.
        (int w, int h) = ratio >= 1
            ? (ThumbnailService.UploadMaxSide, (int)Math.Round(ThumbnailService.UploadMaxSide / ratio))
            : ((int)Math.Round(ThumbnailService.UploadMaxSide * ratio), ThumbnailService.UploadMaxSide);
        return (preset.Name, $"Ratio {ratio:0.###} - up to {w}x{h}.");
    }

    /// <summary>Reports what's already on disk for a model folder, so the window shows real
    /// state on open instead of staying blank until the user clicks Download. Uses the files'
    /// own last-write time rather than a separately tracked timestamp setting - immune to any
    /// bug in remembering to update a side-channel value, since it reads the one fact that
    /// actually matters (when these bytes were last written).</summary>
    private static string GetModelStatusText(string targetDir, params string[] requiredFiles)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            return "";
        }
        string[] paths = [.. requiredFiles.Select(f => Path.Combine(targetDir, f))];
        if (!paths.All(File.Exists))
        {
            return "Not downloaded yet.";
        }
        DateTime newest = paths.Select(File.GetLastWriteTime).Max();
        return $"Downloaded (updated {newest:yyyy-MM-dd HH:mm}).";
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
        _isDownloading = true;
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => DownloadStatusText.Text = msg);
        try
        {
            bool haveBothFiles = File.Exists(Path.Combine(targetDir, "model.onnx")) && File.Exists(Path.Combine(targetDir, "selected_tags.csv"));
            string? localEtag = _repo.GetStringSetting(SettingsKeys.WdModelEtag);

            DownloadStatusText.Text = "Checking for updates...";
            string? remoteEtag = await _downloader.GetRemoteWdTaggerModelETagAsync(_downloadCts.Token);

            if (haveBothFiles && remoteEtag is not null && remoteEtag == localEtag)
            {
                DownloadStatusText.Text = "Already up to date.";
                return;
            }

            await _downloader.DownloadWdTaggerModelAsync(targetDir, progress, _downloadCts.Token);
            if (remoteEtag is not null)
            {
                _repo.SetStringSetting(SettingsKeys.WdModelEtag, remoteEtag);
            }
            DownloadStatusText.Text += " Restart VRC Photo Manager to use it.";
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
            _isDownloading = false;
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
        _isDownloading = true;
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => DownloadClipStatusText.Text = msg);
        try
        {
            bool haveFile = File.Exists(Path.Combine(targetDir, "model.onnx"));
            string? localEtag = _repo.GetStringSetting(SettingsKeys.ClipModelEtag);

            DownloadClipStatusText.Text = "Checking for updates...";
            string? remoteEtag = await _downloader.GetRemoteClipModelETagAsync(_downloadCts.Token);

            if (haveFile && remoteEtag is not null && remoteEtag == localEtag)
            {
                DownloadClipStatusText.Text = "Already up to date.";
                return;
            }

            await _downloader.DownloadClipModelAsync(targetDir, progress, _downloadCts.Token);
            if (remoteEtag is not null)
            {
                _repo.SetStringSetting(SettingsKeys.ClipModelEtag, remoteEtag);
            }
            DownloadClipStatusText.Text += " Restart VRC Photo Manager to use it.";
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
            _isDownloading = false;
        }
    }

    private void BrowseAvatarModelDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select avatar classifier model folder" };
        if (dialog.ShowDialog() == true)
        {
            AvatarModelDirTextBox.Text = dialog.FolderName;
        }
    }

    private async void DownloadAvatarModel_Click(object sender, RoutedEventArgs e)
    {
        string targetDir = AvatarModelDirTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            MessageBox.Show(this, "Enter or browse to a model folder first (it will be created if it doesn't exist).",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DownloadAvatarButton.IsEnabled = false;
        _isDownloading = true;
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => DownloadAvatarStatusText.Text = msg);
        try
        {
            bool haveBothFiles = File.Exists(Path.Combine(targetDir, "model.onnx")) && File.Exists(Path.Combine(targetDir, "labels.txt"));
            string? localEtag = _repo.GetStringSetting(SettingsKeys.AvatarModelEtag);

            DownloadAvatarStatusText.Text = "Checking for updates...";
            string? remoteEtag = await _downloader.GetRemoteAvatarModelETagAsync(_downloadCts.Token);

            if (haveBothFiles && remoteEtag is not null && remoteEtag == localEtag)
            {
                DownloadAvatarStatusText.Text = "Already up to date.";
                return;
            }

            await _downloader.DownloadAvatarModelAsync(targetDir, progress, _downloadCts.Token);
            if (remoteEtag is not null)
            {
                _repo.SetStringSetting(SettingsKeys.AvatarModelEtag, remoteEtag);
            }
            DownloadAvatarStatusText.Text += " Restart VRC Photo Manager to use it.";
        }
        catch (OperationCanceledException)
        {
            DownloadAvatarStatusText.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            DownloadAvatarStatusText.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            DownloadAvatarButton.IsEnabled = true;
            _isDownloading = false;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Always save, including an empty box - that's how clearing a path and saving
        // actually clears the underlying setting instead of silently keeping the old value
        // (Directory.Exists("") is false, so an empty saved value already behaves as "not
        // configured" everywhere it's read, both here and in MainViewModel's resolvers).
        _repo.SetStringSetting(SettingsKeys.WdModelDir, ModelDirTextBox.Text.Trim());
        _repo.SetStringSetting(SettingsKeys.ClipModelDir, ClipModelDirTextBox.Text.Trim());
        _repo.SetStringSetting(SettingsKeys.AvatarModelDir, AvatarModelDirTextBox.Text.Trim());
        _repo.SetBoolSetting(SettingsKeys.AutoCopyVrcdnUrlOnHover, AutoCopyUrlCheckBox.IsChecked == true);
        _repo.SetDoubleSetting(SettingsKeys.HoverPreviewDelaySeconds, HoverDelaySlider.Value);
        _repo.SetBoolSetting(SettingsKeys.SkipResolvedPhotosOnFaceScan, SkipResolvedPhotosCheckBox.IsChecked == true);
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
