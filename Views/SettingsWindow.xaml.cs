using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VrcPhotoManager.Data;
using VrcPhotoManager.Models;
using VrcPhotoManager.Services;
using VrcPhotoManager.ViewModels;

namespace VrcPhotoManager.Views;

public partial class SettingsWindow : Window
{
    private readonly PhotoRepository _repo;
    private readonly AvatarCatalogRepository _avatarCatalog;
    private readonly LibraryRepository _libraries;
    private readonly MainViewModel _mainViewModel;
    private readonly CredentialStore _credentials;
    private readonly ModelDownloadService _downloader = new();
    private CancellationTokenSource? _downloadCts;

    public SettingsWindow(PhotoRepository repo, AvatarCatalogRepository avatarCatalog, LibraryRepository libraries, MainViewModel mainViewModel)
    {
        InitializeComponent();
        _avatarCatalog = avatarCatalog;
        _libraries = libraries;
        _mainViewModel = mainViewModel;
        _credentials = new CredentialStore(repo);
        // SizeToContent="Height" alone would let this window grow past the screen on a
        // packed tab (e.g. Avatars with a long catalog) - capping MaxHeight to the primary
        // monitor's work area (taskbar excluded) means it still grows to fit content up to
        // that point, then the ScrollViewer around the TabControl takes over instead of the
        // window running off-screen. Not multi-monitor-aware (WorkArea reflects the primary
        // screen only, not whichever one this window actually opens on via OpenNearCursor
        // below) - acceptable given this is a bounded UI polish fix, not a multi-monitor
        // layout feature.
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        DialogWindowBehavior.HideMinimizeAndMaximizeButtons(this);
        // Deliberately NOT DialogWindowBehavior.CloseOnDeactivated here - that's meant for
        // quick, disposable utility popups (its own doc comment says so), and closing this
        // window the instant ANY other window/app takes focus (an OS notification, alt-tabbing
        // to copy a folder path or a token) silently threw away an in-progress Settings session
        // with no way back short of reopening it from scratch - confirmed as a real reported
        // symptom, not a hypothetical. A real Settings dialog should stay open until the user
        // closes it (X, Cancel, or Save), same as virtually every other app's settings window.
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

        string? savedAvatarBodyDir = _repo.GetStringSetting(SettingsKeys.AvatarBodyModelDir);
        AvatarBodyModelDirTextBox.Text = string.IsNullOrWhiteSpace(savedAvatarBodyDir) ? DefaultModelPaths.AvatarBodyDetection : savedAvatarBodyDir;

        string? savedAvatarDir = _repo.GetStringSetting(SettingsKeys.AvatarModelDir);
        AvatarModelDirTextBox.Text = string.IsNullOrWhiteSpace(savedAvatarDir) ? DefaultModelPaths.Avatar : savedAvatarDir;

        AutoCopyUrlCheckBox.IsChecked = _repo.GetBoolSetting(SettingsKeys.AutoCopyVrcdnUrlOnHover);
        HoverDelaySlider.Value = _repo.GetDoubleSetting(SettingsKeys.HoverPreviewDelaySeconds, 0.25);
        SkipResolvedPhotosCheckBox.IsChecked = _repo.GetBoolSetting(SettingsKeys.SkipResolvedPhotosOnFaceScan, true);
        EnableExifEliminationCheckBox.IsChecked = _repo.GetBoolSetting(SettingsKeys.EnableExifElimination, true);
        PortalHopWindowSlider.Value = _repo.GetDoubleSetting(SettingsKeys.PortalHopWindowSeconds, 90);
        DiscordCacheSizeLimitSlider.Value = _repo.GetDoubleSetting(SettingsKeys.DiscordCacheSizeLimitGb, 5);

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
        DownloadAvatarBodyStatusText.Text = GetModelStatusText(AvatarBodyModelDirTextBox.Text, "model.onnx");

        CropPresetsList.ItemsSource = MainViewModel.UploadCropPresets.Select(DescribeCropPreset).ToList();

        RefreshAvatarCatalogList();
        RefreshLibraryList();

        DiscordApplicationIdTextBox.Text = _repo.GetStringSetting(SettingsKeys.DiscordApplicationId) ?? "";
    }

    private record AvatarCatalogRow(long Id, string DisplayName, string StoresText, string ParentText);
    private record LibraryRow(long Id, string DisplayName, string TypeText, string DetailText, bool IsDiscord, bool AutoDownloadOriginals);

    private static string DescribeStores(AvatarCatalog c)
    {
        var stores = new List<string>();
        if (c.BoothProduct is not null) stores.Add("Booth");
        if (c.GumroadUser is not null) stores.Add("Gumroad");
        if (c.JinxxyUser is not null) stores.Add("Jinxxy");
        return stores.Count > 0 ? string.Join(", ", stores) : "(no store links yet)";
    }

    /// <summary>Re-runs the search (or lists everything when query is blank - same "browse the
    /// full list" convention as SearchAvatarEntries in TagFacesWindow) and resolves each row's
    /// parent name for display. A second, unfiltered Search("") call just to build the
    /// id->name lookup is simplest here - this list is at most a few hundred rows, not worth a
    /// dedicated repository query.</summary>
    private void RefreshAvatarCatalogList(string query = "")
    {
        var all = _avatarCatalog.Search("");
        var byId = all.ToDictionary(c => c.Id);
        var results = string.IsNullOrWhiteSpace(query) ? all : _avatarCatalog.Search(query);
        AvatarCatalogListBox.ItemsSource = results.Select(c => new AvatarCatalogRow(
            c.Id,
            c.DisplayName ?? "(unnamed)",
            DescribeStores(c),
            c.ParentItemId is long parentId && byId.TryGetValue(parentId, out var parent)
                ? $"Based on: {parent.DisplayName ?? "(unnamed)"}"
                : ""
        )).ToList();
        EditAvatarCatalogButton.IsEnabled = false;
    }

    private void AvatarCatalogSearchTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshAvatarCatalogList(AvatarCatalogSearchTextBox.Text);

    private void AvatarCatalogListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        EditAvatarCatalogButton.IsEnabled = AvatarCatalogListBox.SelectedItem is not null;

    private void EditAvatarCatalogButton_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarCatalogListBox.SelectedItem is not AvatarCatalogRow row) return;
        new AvatarCatalogEditWindow(_avatarCatalog, row.Id).ShowDialog();
        RefreshAvatarCatalogList(AvatarCatalogSearchTextBox.Text);
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
        }
    }

    private void BrowseAvatarBodyModelDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select avatar body detection model folder" };
        if (dialog.ShowDialog() == true)
        {
            AvatarBodyModelDirTextBox.Text = dialog.FolderName;
        }
    }

    private async void DownloadAvatarBodyModel_Click(object sender, RoutedEventArgs e)
    {
        string targetDir = AvatarBodyModelDirTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            MessageBox.Show(this, "Enter or browse to a model folder first (it will be created if it doesn't exist).",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DownloadAvatarBodyButton.IsEnabled = false;
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => DownloadAvatarBodyStatusText.Text = msg);
        try
        {
            bool haveFile = File.Exists(Path.Combine(targetDir, "model.onnx"));
            string? localEtag = _repo.GetStringSetting(SettingsKeys.AvatarBodyModelEtag);

            DownloadAvatarBodyStatusText.Text = "Checking for updates...";
            string? remoteEtag = await _downloader.GetRemoteAvatarBodyModelETagAsync(_downloadCts.Token);

            if (haveFile && remoteEtag is not null && remoteEtag == localEtag)
            {
                DownloadAvatarBodyStatusText.Text = "Already up to date.";
                return;
            }

            await _downloader.DownloadAvatarBodyModelAsync(targetDir, progress, _downloadCts.Token);
            if (remoteEtag is not null)
            {
                _repo.SetStringSetting(SettingsKeys.AvatarBodyModelEtag, remoteEtag);
            }
            DownloadAvatarBodyStatusText.Text += " Restart VRC Photo Manager to use it.";
        }
        catch (OperationCanceledException)
        {
            DownloadAvatarBodyStatusText.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            DownloadAvatarBodyStatusText.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            DownloadAvatarBodyButton.IsEnabled = true;
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
        _repo.SetStringSetting(SettingsKeys.AvatarBodyModelDir, AvatarBodyModelDirTextBox.Text.Trim());
        _repo.SetBoolSetting(SettingsKeys.AutoCopyVrcdnUrlOnHover, AutoCopyUrlCheckBox.IsChecked == true);
        _repo.SetDoubleSetting(SettingsKeys.HoverPreviewDelaySeconds, HoverDelaySlider.Value);
        _repo.SetBoolSetting(SettingsKeys.SkipResolvedPhotosOnFaceScan, SkipResolvedPhotosCheckBox.IsChecked == true);
        _repo.SetBoolSetting(SettingsKeys.EnableExifElimination, EnableExifEliminationCheckBox.IsChecked == true);
        _repo.SetDoubleSetting(SettingsKeys.PortalHopWindowSeconds, PortalHopWindowSlider.Value);
        _repo.SetDoubleSetting(SettingsKeys.DiscordCacheSizeLimitGb, DiscordCacheSizeLimitSlider.Value);

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

    private void RefreshLibraryList()
    {
        LibraryListBox.ItemsSource = _libraries.GetAll().Select(l => new LibraryRow(
            l.Id,
            l.DisplayName,
            l.Type == LibraryType.LocalFolder ? "Local folder" : "Discord channel",
            l.Type == LibraryType.LocalFolder
                ? l.LocalPath ?? ""
                : $"Last synced: {(l.LastSyncedAt is DateTime d ? d.ToLocalTime().ToString("g") : "Never")}",
            l.Type == LibraryType.DiscordChannel,
            l.AutoDownloadOriginals
        )).ToList();
    }

    private void AddLocalFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select a folder to add as a photo library" };
        if (dialog.ShowDialog() == true)
        {
            string path = dialog.FolderName;
            string displayName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(displayName)) displayName = path;

            _libraries.AddLocalFolder(path, displayName);
            RefreshLibraryList();
        }
    }

    /// <summary>Fire-and-forget, matching this file's own AddDiscordChannelButton_Click/
    /// LoadDiscordChannelsAsync convention - progress and any errors surface via
    /// MainViewModel.StatusMessage, which MainWindow's own status bar already shows live while
    /// this non-modal Settings window sits alongside it.</summary>
    private void ScanSingleLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not long id) return;
        _ = _mainViewModel.ScanSingleLibraryAsync(id);
    }

    private void RemoveLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not long id) return;
        var library = _libraries.GetById(id);
        if (library is null) return;

        // Removing a library doesn't touch its already-scanned photos - they keep pointing at
        // this (now-deleted) LibraryId forever, with no future rescans and no UI to remove them
        // (an uncached Discord photo whose library was removed becomes permanently ineligible
        // for batch operations - see IsEligibleForBatchOperation). No reassignment UI exists, so
        // this is at minimum a clear warning before an otherwise-silent, effectively permanent
        // orphaning.
        var confirm = MessageBox.Show(this,
            $"Remove '{library.DisplayName}'? Photos already scanned from this library will remain " +
            "in your library but will no longer be updated by future scans.",
            "Remove Library", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _libraries.Remove(id);
        RefreshLibraryList();
    }

    /// <summary>Shows the token-entry panel the first time (no saved token yet), otherwise goes
    /// straight to loading the guild/channel picker with the already-saved token.</summary>
    private void AddDiscordChannelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_credentials.LoadDiscordBotToken() is null)
        {
            DiscordTokenSetupPanel.Visibility = Visibility.Visible;
            return;
        }
        _ = LoadDiscordChannelsAsync();
    }

    private void OpenDiscordDevPortalLink_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://discord.com/developers/applications") { UseShellExecute = true });
    }

    /// <summary>Discord's own invite-authorize URL accepts the target permissions as a precomputed
    /// bitmask query param, so the user never has to click through the OAuth2 "URL Generator"
    /// page's own checkboxes - paste the Application ID and this is already scoped exactly right.
    /// 66560 = View Channel (1&lt;&lt;10 = 1024) + Read Message History (1&lt;&lt;16 = 65536), the same
    /// two permissions DiscordTokenSetupPanel's instructions ask for.</summary>
    private void OpenDiscordInviteLink_Click(object sender, RoutedEventArgs e)
    {
        string clientId = DiscordApplicationIdTextBox.Text.Trim();
        if (string.IsNullOrEmpty(clientId))
        {
            MessageBox.Show(this, "Paste the Discord Application ID first (from the app's \"General Information\" page).",
                "Discord", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _repo.SetStringSetting(SettingsKeys.DiscordApplicationId, clientId);
        Process.Start(new ProcessStartInfo(
            $"https://discord.com/api/oauth2/authorize?client_id={Uri.EscapeDataString(clientId)}&permissions=66560&scope=bot")
        { UseShellExecute = true });
    }

    /// <summary>Always available (unlike AddDiscordChannelButton_Click, which only shows the
    /// token box when none is saved yet) - lets a saved-but-wrong or since-regenerated token be
    /// corrected without waiting for a failed API call to trigger the panel.</summary>
    private void ChangeDiscordTokenButton_Click(object sender, RoutedEventArgs e)
    {
        DiscordTokenSetupPanel.Visibility = Visibility.Visible;
    }

    private void SaveDiscordTokenButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DiscordTokenBox.Password)) return;
        _credentials.SaveDiscordBotToken(DiscordTokenBox.Password);
        DiscordTokenBox.Clear();
        DiscordTokenSetupPanel.Visibility = Visibility.Collapsed;
        _ = LoadDiscordChannelsAsync();
    }

    private record DiscordChannelRow(string GuildId, string GuildName, string ChannelId, string ChannelName);

    /// <summary>Lists every text channel in every guild the bot has been invited to, flattened
    /// into one picker list ("Guild / #channel") - the design doesn't call for per-guild
    /// grouping, and the bot is expected to only be in a small number of servers.</summary>
    private async Task LoadDiscordChannelsAsync()
    {
        string? token = _credentials.LoadDiscordBotToken();
        if (token is null) return;

        using var client = new DiscordApiClient(token);
        var rows = new List<DiscordChannelRow>();
        try
        {
            var guilds = await client.GetGuildsAsync(CancellationToken.None);
            foreach (var guild in guilds)
            {
                var channels = await client.GetChannelsAsync(guild.Id, CancellationToken.None);
                rows.AddRange(channels.Select(c => new DiscordChannelRow(guild.Id, guild.Name, c.Id, c.Name)));
            }
        }
        catch (Exception ex)
        {
            // Re-show the token panel on any failure rather than leaving the user stuck with a
            // silently-permanent bad token - LoadDiscordBotToken()==null is the only thing that
            // used to trigger it, so a bad-but-saved token had no way back into the UI short of
            // editing the database directly.
            string hint = ex.Message.Contains("401") || ex.Message.Contains("Unauthorized")
                ? "\n\nThis usually means the saved bot token is wrong (a common mix-up: pasting the Application ID or Client Secret instead of the Bot Token from Discord's developer portal). Re-enter it below."
                : "";
            MessageBox.Show(this, $"Failed to load Discord channels: {ex.Message}{hint}", "Discord", MessageBoxButton.OK, MessageBoxImage.Warning);
            DiscordTokenSetupPanel.Visibility = Visibility.Visible;
            return;
        }

        DiscordChannelPickerListBox.ItemsSource = rows.Select(r => $"{r.GuildName} / #{r.ChannelName}").ToList();
        DiscordChannelPickerListBox.Tag = rows; // stash the full rows for the selection handler
        DiscordChannelPickerListBox.Visibility = Visibility.Visible;
    }

    private void DiscordChannelPickerListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DiscordChannelPickerListBox.SelectedIndex < 0) return;
        if (DiscordChannelPickerListBox.Tag is not List<DiscordChannelRow> rows) return;
        var selected = rows[DiscordChannelPickerListBox.SelectedIndex];

        _libraries.AddDiscordChannel(selected.GuildId, selected.ChannelId, $"#{selected.ChannelName}");
        DiscordChannelPickerListBox.Visibility = Visibility.Collapsed;
        DiscordChannelPickerListBox.SelectedIndex = -1;
        RefreshLibraryList();
    }

    private void AutoDownloadToggle_Changed(object sender, RoutedEventArgs e)
    {
        if ((sender as CheckBox)?.Tag is not long id) return;
        _libraries.SetAutoDownloadOriginals(id, (sender as CheckBox)!.IsChecked == true);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        Close();
    }
}
