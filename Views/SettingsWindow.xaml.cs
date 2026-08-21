using System.Diagnostics;
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
    private readonly CredentialStore _credentials;
    private readonly ModelDownloadService _downloader = new();
    private CancellationTokenSource? _downloadCts;
    private bool _isDownloading;

    public SettingsWindow(PhotoRepository repo)
    {
        InitializeComponent();
        _credentials = new CredentialStore(repo);
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

        string? savedCcipDir = _repo.GetStringSetting(SettingsKeys.CcipModelDir);
        CcipModelDirTextBox.Text = string.IsNullOrWhiteSpace(savedCcipDir) ? DefaultModelPaths.Ccip : savedCcipDir;

        string? savedFaceDetectionDir = _repo.GetStringSetting(SettingsKeys.FaceDetectionModelDir);
        FaceDetectionModelDirTextBox.Text = string.IsNullOrWhiteSpace(savedFaceDetectionDir) ? DefaultModelPaths.FaceDetection : savedFaceDetectionDir;

        string? savedAvatarDir = _repo.GetStringSetting(SettingsKeys.AvatarModelDir);
        AvatarModelDirTextBox.Text = string.IsNullOrWhiteSpace(savedAvatarDir) ? DefaultModelPaths.Avatar : savedAvatarDir;

        AutoCopyUrlCheckBox.IsChecked = _repo.GetBoolSetting(SettingsKeys.AutoCopyVrcdnUrlOnHover);
        HoverDelaySlider.Value = _repo.GetDoubleSetting(SettingsKeys.HoverPreviewDelaySeconds, 0.25);
        SkipResolvedPhotosCheckBox.IsChecked = _repo.GetBoolSetting(SettingsKeys.SkipResolvedPhotosOnFaceScan, true);

        // GistTokenBox is deliberately left blank rather than showing the saved token - it's
        // only used to CHANGE the token (see SaveButton_Click, which leaves the stored token
        // untouched if this stays empty), same "don't echo back a secret" spirit as never
        // displaying the VRCDN cookie anywhere either.
        string? savedIndexFileNameBase = _repo.GetStringSetting(SettingsKeys.IndexFileNameBase);
        IndexFileNameBox.Text = string.IsNullOrWhiteSpace(savedIndexFileNameBase)
            ? Guid.NewGuid().ToString("N")
            : savedIndexFileNameBase;
        string? savedIndexFormat = _repo.GetStringSetting(SettingsKeys.IndexFileFormat);
        IndexFormatBox.SelectedIndex = savedIndexFormat switch { "txt" => 1, "json" => 2, _ => 0 };
        string? indexUrl = _repo.GetStringSetting(SettingsKeys.GistIndexUrl);
        IndexUrlText.Text = indexUrl is null ? "Current index URL: (none published yet)" : $"Current index URL: {indexUrl}";

        UploadFormatBox.SelectedIndex = _repo.GetStringSetting(SettingsKeys.UploadImageFormat) == "png" ? 1 : 0;

        DownloadStatusText.Text = GetModelStatusText(ModelDirTextBox.Text, "model.onnx", "selected_tags.csv");
        DownloadCcipStatusText.Text = GetModelStatusText(CcipModelDirTextBox.Text, "model_feat.onnx", "model_metrics.onnx");
        DownloadFaceDetectionStatusText.Text = GetModelStatusText(FaceDetectionModelDirTextBox.Text, "model.onnx");
        DownloadAvatarStatusText.Text = GetModelStatusText(AvatarModelDirTextBox.Text, "model.onnx", "labels.txt");

        CropPresetsList.ItemsSource = MainViewModel.UploadCropPresets.Select(DescribeCropPreset).ToList();
    }

    /// <summary>A real class rather than a (string Name, string Detail) tuple - WPF data
    /// binding can't reach named ValueTuple element names at runtime (they're compile-time-only
    /// syntactic sugar; the actual members are Item1/Item2), so {Binding Name}/{Binding Detail}
    /// in the ItemsControl's DataTemplate would silently fail and render every row blank. Same
    /// class of bug this codebase already hit once before in AvatarSearchListBox_MouseUp.</summary>
    private sealed record CropPresetDisplay(string Name, string Detail);

    /// <summary>Name + a human-readable ratio/example-resolution line for the read-only crop-
    /// presets reference panel - reads MainViewModel.UploadCropPresets directly (not a separate
    /// hardcoded list) so this can't silently drift out of sync with the real preset values.</summary>
    private static CropPresetDisplay DescribeCropPreset(MainViewModel.UploadCropPreset preset)
    {
        if (preset.AspectRatio is not double ratio)
        {
            return new CropPresetDisplay(preset.Name, "Uploads at the photo's own resolution, uncropped.");
        }

        // Same cap ThumbnailService.PrepareForUploadAsync actually applies - the larger side
        // hits UploadMaxSide exactly, the other side follows the ratio.
        (int w, int h) = ratio >= 1
            ? (ThumbnailService.UploadMaxSide, (int)Math.Round(ThumbnailService.UploadMaxSide / ratio))
            : ((int)Math.Round(ThumbnailService.UploadMaxSide * ratio), ThumbnailService.UploadMaxSide);
        return new CropPresetDisplay(preset.Name, $"Ratio {ratio:0.###} - up to {w}x{h}.");
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

    private void BrowseCcipModelDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select CCIP model folder" };
        if (dialog.ShowDialog() == true)
        {
            CcipModelDirTextBox.Text = dialog.FolderName;
        }
    }

    private async void DownloadCcipModel_Click(object sender, RoutedEventArgs e)
    {
        string targetDir = CcipModelDirTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            MessageBox.Show(this, "Enter or browse to a model folder first (it will be created if it doesn't exist).",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DownloadCcipButton.IsEnabled = false;
        _isDownloading = true;
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => DownloadCcipStatusText.Text = msg);
        try
        {
            bool haveBothFiles = File.Exists(Path.Combine(targetDir, "model_feat.onnx")) && File.Exists(Path.Combine(targetDir, "model_metrics.onnx"));
            string? localEtag = _repo.GetStringSetting(SettingsKeys.CcipModelEtag);

            DownloadCcipStatusText.Text = "Checking for updates...";
            string? remoteEtag = await _downloader.GetRemoteCcipModelETagAsync(_downloadCts.Token);

            if (haveBothFiles && remoteEtag is not null && remoteEtag == localEtag)
            {
                DownloadCcipStatusText.Text = "Already up to date.";
                return;
            }

            await _downloader.DownloadCcipModelAsync(targetDir, progress, _downloadCts.Token);
            if (remoteEtag is not null)
            {
                _repo.SetStringSetting(SettingsKeys.CcipModelEtag, remoteEtag);
            }
            DownloadCcipStatusText.Text += " Restart VRC Photo Manager to use it.";
        }
        catch (OperationCanceledException)
        {
            DownloadCcipStatusText.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            DownloadCcipStatusText.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            DownloadCcipButton.IsEnabled = true;
            _isDownloading = false;
        }
    }

    private void BrowseFaceDetectionModelDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select face detection model folder" };
        if (dialog.ShowDialog() == true)
        {
            FaceDetectionModelDirTextBox.Text = dialog.FolderName;
        }
    }

    private async void DownloadFaceDetectionModel_Click(object sender, RoutedEventArgs e)
    {
        string targetDir = FaceDetectionModelDirTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            MessageBox.Show(this, "Enter or browse to a model folder first (it will be created if it doesn't exist).",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DownloadFaceDetectionButton.IsEnabled = false;
        _isDownloading = true;
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => DownloadFaceDetectionStatusText.Text = msg);
        try
        {
            bool haveFile = File.Exists(Path.Combine(targetDir, "model.onnx"));
            string? localEtag = _repo.GetStringSetting(SettingsKeys.FaceDetectionModelEtag);

            DownloadFaceDetectionStatusText.Text = "Checking for updates...";
            string? remoteEtag = await _downloader.GetRemoteFaceDetectionModelETagAsync(_downloadCts.Token);

            if (haveFile && remoteEtag is not null && remoteEtag == localEtag)
            {
                DownloadFaceDetectionStatusText.Text = "Already up to date.";
                return;
            }

            await _downloader.DownloadFaceDetectionModelAsync(targetDir, progress, _downloadCts.Token);
            if (remoteEtag is not null)
            {
                _repo.SetStringSetting(SettingsKeys.FaceDetectionModelEtag, remoteEtag);
            }
            DownloadFaceDetectionStatusText.Text += " Restart VRC Photo Manager to use it.";
        }
        catch (OperationCanceledException)
        {
            DownloadFaceDetectionStatusText.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            DownloadFaceDetectionStatusText.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            DownloadFaceDetectionButton.IsEnabled = true;
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
        _repo.SetStringSetting(SettingsKeys.CcipModelDir, CcipModelDirTextBox.Text.Trim());
        _repo.SetStringSetting(SettingsKeys.FaceDetectionModelDir, FaceDetectionModelDirTextBox.Text.Trim());
        _repo.SetStringSetting(SettingsKeys.AvatarModelDir, AvatarModelDirTextBox.Text.Trim());
        _repo.SetBoolSetting(SettingsKeys.AutoCopyVrcdnUrlOnHover, AutoCopyUrlCheckBox.IsChecked == true);
        _repo.SetDoubleSetting(SettingsKeys.HoverPreviewDelaySeconds, HoverDelaySlider.Value);
        _repo.SetBoolSetting(SettingsKeys.SkipResolvedPhotosOnFaceScan, SkipResolvedPhotosCheckBox.IsChecked == true);

        // GistTokenBox left empty means "don't change the saved token" - only overwrite it when
        // something was actually typed, so reopening Settings and clicking Save without touching
        // this box (the common case) can't accidentally wipe out an already-configured token.
        if (GistTokenBox.Password.Length > 0)
        {
            _credentials.SaveGistToken(GistTokenBox.Password);
        }
        _repo.SetStringSetting(SettingsKeys.IndexFileNameBase, IndexFileNameBox.Text.Trim());
        string format = (IndexFormatBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string ?? "csv";
        _repo.SetStringSetting(SettingsKeys.IndexFileFormat, format);

        string uploadFormat = (UploadFormatBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string ?? "jpg";
        _repo.SetStringSetting(SettingsKeys.UploadImageFormat, uploadFormat);

        DialogResult = true;
        Close();
    }

    /// <summary>Pre-selects the "gist" scope and a descriptive name via query params, so
    /// generating a correctly (and minimally) scoped token is close to one click - see the
    /// VRCDN Photo Index section's explanation text.</summary>
    private void GenerateGistTokenLink_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(
            "https://github.com/settings/tokens/new?scopes=gist&description=VRC+Photo+Manager+Index")
        { UseShellExecute = true });
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        DialogResult = false;
        Close();
    }
}
