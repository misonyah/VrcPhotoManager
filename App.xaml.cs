using System.Configuration;
using System.Data;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Velopack;

namespace VrcPhotoManager;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    /// <summary>Velopack (installer + auto-update, see .github/workflows/release.yml) needs to
    /// run as literally the first thing in the process - before WPF, the single-instance mutex,
    /// or any diagnostic --test-* argument handling - so it can apply a staged update and/or
    /// respond to install/uninstall hook invocations without any of that other startup logic
    /// running first. That means it can't live in OnStartup (which already runs reasonably
    /// early, but after WPF itself has spun up); it needs its own Main(), which requires turning
    /// off the SDK's auto-generated WPF entry point - see the csproj's StartupObject/
    /// ApplicationDefinition-to-Page override.</summary>
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>Every diagnostic --test-*/--run-* branch below calls this instead of the more
    /// obvious `Shutdown(); return;` - a real, reproduced hang proved that pairing insufficient:
    /// Shutdown() called this early (synchronously inside OnStartup, before Application.Run()'s
    /// Dispatcher has actually started pumping messages) does not reliably fire OnExit at all
    /// when ShutdownMode is still its OnLastWindowClose default and no window was ever shown -
    /// the app just proceeds into Run()'s message loop anyway, now idling forever with nothing
    /// left to close it. Confirmed live: a diagnostic that loads a CcipEmbeddingService (ONNX
    /// Runtime + DirectML, which spins up its own non-background worker threads - a live
    /// foreground thread alone would keep the process alive even past a normal Main() return)
    /// sat at ~3GB RSS for 10+ minutes after printing its own "done" output, still holding the
    /// single-instance mutex the whole time - exactly the mechanism that would make the real app
    /// report "already running" for no visible reason. Environment.Exit sidesteps the entire WPF
    /// shutdown-timing question by terminating the process immediately and unconditionally,
    /// regardless of Dispatcher state or any other thread still running.</summary>
    private static void ExitProcess() => Environment.Exit(0);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, "Local\\VrcPhotoManager_SingleInstance", out bool isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show(
                "VRC Photo Manager is already running (or a diagnostic command is using its database) - only one instance can run at a time to avoid database lock conflicts.",
                "VRC Photo Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            ExitProcess();
        }

        if (e.Args.Length == 2 && e.Args[0] == "--test-classify")
        {
            RunClassifierSmokeTest(e.Args[1]);
            ExitProcess();
        }

        if (e.Args.Length == 2 && e.Args[0] == "--test-crop-print")
        {
            bool isWhite = Services.CropPrintService.HasWhiteBorder(e.Args[1]);
            Console.WriteLine($"HasWhiteBorder: {isWhite}");
            if (isWhite)
            {
                string newPath = Services.CropPrintService.CropAndSave(e.Args[1]);
                Console.WriteLine($"Saved: {newPath}");
            }
            ExitProcess();
        }

        if (e.Args.Length == 2 && e.Args[0] == "--test-metadata")
        {
            var meta = Services.PngMetadataReader.TryReadVrcxMetadata(e.Args[1]);
            Console.WriteLine(meta is null
                ? "No VRCX metadata found."
                : $"Author: {meta.Author?.DisplayName} ({meta.Author?.Id})\n" +
                  $"World: {meta.World?.Name} ({meta.World?.Id})\n" +
                  $"Players: {string.Join(", ", meta.Players?.Select(p => p.DisplayName) ?? [])}");
            ExitProcess();
        }

        if (e.Args.Length == 2 && e.Args[0] == "--test-face-detect")
        {
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcdnManager");
            var repo = new Data.PhotoRepository(Path.Combine(dataDir, "vrcdn_manager.db"));
            string? modelDir = repo.GetStringSetting(Services.SettingsKeys.FaceDetectionModelDir);
            if (modelDir is null)
            {
                Console.WriteLine("Face detection model dir not configured (set it via Settings first).");
                ExitProcess();
                return;
            }
            var detector = Services.FaceDetectionService.TryCreate(modelDir, out string? detectorError);
            if (detector is null)
            {
                Console.WriteLine($"Unavailable: {detectorError}");
                ExitProcess();
                return;
            }
            var faces = detector.DetectFaces(e.Args[1]);
            Console.WriteLine($"Faces found: {faces.Count}");
            foreach (var f in faces)
            {
                Console.WriteLine($"  ({f.X}, {f.Y}) {f.Width}x{f.Height}");
            }
            ExitProcess();
        }

        // Unhandled exceptions on the UI thread (e.g. from an async void command handler)
        // would otherwise take the whole app down with no explanation - show them instead.
        // Walks InnerException too - EF Core's "An error occurred while saving the entity
        // changes" wraps the actually-useful detail (e.g. the real SQLite error) one level
        // down, and the outer message alone isn't enough to diagnose a real report.
        DispatcherUnhandledException += (_, args) =>
        {
            var messages = new List<string>();
            for (Exception? ex = args.Exception; ex is not null; ex = ex.InnerException)
            {
                messages.Add($"{ex.GetType().Name}: {ex.Message}");
            }
            MessageBox.Show(
                $"Unexpected error:\n\n{string.Join("\n\n", messages)}",
                "VRC Photo Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        if (e.Args.Length == 1 && e.Args[0] == "--debug-login")
        {
            // LoginWindow sets DialogResult on success, which requires ShowDialog (not Show).
            var window = new Views.LoginWindow();
            bool? result = window.ShowDialog();
            Console.WriteLine($"Login result: {result}, cookie: {window.SessionCookie}");
            ExitProcess();
        }

        if (e.Args.Length == 1 && e.Args[0] == "--test-library-repo")
        {
            RunLibraryRepoDiagnostic();
            ExitProcess();
        }

        if (e.Args.Length == 1 && e.Args[0] == "--test-vrcdn-sync")
        {
            RunVrcdnSyncDiagnostic();
            ExitProcess();
        }

        if (e.Args.Length == 2 && e.Args[0] == "--test-remove-object")
        {
            RunRemoveObjectDiagnostic(e.Args[1]);
            ExitProcess();
        }

        if (e.Args.Length == 3 && e.Args[0] == "--debug-tag-faces")
        {
            // Verification-only hook (like --debug-login) - accepts a db path (point at a
            // scratch copy) so this never writes real tag data into the live database.
            string dbPath = e.Args[1];
            long photoId = long.Parse(e.Args[2]);
            var photos = new Data.PhotoRepository(dbPath);
            var faces = new Data.FaceRepository(dbPath);
            var avatarRegions = new Data.AvatarRegionRepository(dbPath);
            var avatarCatalog = new Data.AvatarCatalogRepository(dbPath);
            var photo = photos.GetAll().First(p => p.Id == photoId);
            var lookup = Services.VrcxProfileLookupService.TryCreate(out _);
            // Same DiscordCache dir as RunResolvePhotoDiagnostic - sharing it across this
            // scratch-db diagnostic and the real app is fine, the cache is keyed by
            // RemoteSourceId and purely additive.
            string diagDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcdnManager");
            var diagCredentials = new Services.CredentialStore(photos);
            string? diagDiscordToken = diagCredentials.LoadDiscordBotToken();
            var diagDiscordClient = diagDiscordToken is not null ? new Services.DiscordApiClient(diagDiscordToken) : null;
            var diagCache = new Services.DiscordPhotoCacheService(Path.Combine(diagDataDir, "DiscordCache"));
            var photoSourceResolver = new Services.PhotoSourceResolver(photos, diagCache, diagDiscordClient);
            new Views.TagFacesWindow(faces, photos, avatarRegions, avatarCatalog, photoSourceResolver, null, lookup, photo).ShowDialog();
            ExitProcess();
        }

        if (e.Args.Length == 3 && e.Args[0] == "--test-face-repo")
        {
            RunFaceRepoDiagnostic(e.Args[1], long.Parse(e.Args[2]));
            ExitProcess();
        }

        if (e.Args.Length == 2 && e.Args[0] == "--test-vrcx-profile-lookup")
        {
            RunVrcxProfileLookupDiagnostic(e.Args[1]);
            ExitProcess();
        }

        if (e.Args.Length == 2 && e.Args[0] == "--test-ccip-embed")
        {
            RunCcipEmbedDiagnostic(e.Args[1]);
            ExitProcess();
        }

        if (e.Args.Length == 3 && e.Args[0] == "--test-ccip-similarity")
        {
            RunCcipSimilarityDiagnostic(e.Args[1], e.Args[2]);
            ExitProcess();
        }

        if (e.Args.Length == 1 && e.Args[0] == "--run-suggest-faces")
        {
            RunSuggestFacesDiagnostic();
            ExitProcess();
        }

        if (e.Args.Length == 1 && e.Args[0] == "--run-detect-faces")
        {
            RunDetectFacesDiagnostic();
            ExitProcess();
        }

        if (e.Args.Length == 1 && e.Args[0] == "--run-classify-avatars")
        {
            RunClassifyAvatarsDiagnostic();
            ExitProcess();
        }

        if (e.Args.Length == 2 && e.Args[0] == "--test-avatar-classify")
        {
            RunAvatarClassifySmokeTest(e.Args[1]);
            ExitProcess();
        }

        if (e.Args.Length == 2 && e.Args[0] == "--test-discord-token")
        {
            RunDiscordTokenDiagnostic(e.Args[1]);
            ExitProcess();
        }

        if (e.Args.Length == 1 && e.Args[0] == "--test-discord-guilds")
        {
            RunDiscordGuildsDiagnostic();
            ExitProcess();
        }

        if (e.Args.Length == 2 && e.Args[0] == "--test-discord-cache-evict")
        {
            RunDiscordCacheEvictDiagnostic(long.Parse(e.Args[1]));
            ExitProcess();
        }

        if (e.Args.Length == 2 && e.Args[0] == "--test-resolve-photo")
        {
            RunResolvePhotoDiagnostic(long.Parse(e.Args[1]));
            ExitProcess();
        }

        // Only reached by a real app launch (every diagnostic branch above returns early).
        // Fire-and-forget: never block startup on a network check, and never let a failure
        // (offline, GitHub unreachable, no releases published yet) surface as an error - this
        // is silent best-effort by design.
        _ = CheckForUpdatesAsync();
    }

    /// <summary>Checks GitHub Releases (see .github/workflows/release.yml) for a newer version
    /// and downloads it if found - Velopack then applies it automatically the NEXT time the app
    /// cold-starts (VelopackApp.Build().Run() in Main, before anything else), so no explicit
    /// "restart now" prompt is needed here. UpdateManager.IsInstalled is false when running from
    /// `dotnet run`/a raw build output folder (not through the Velopack-installed app), which is
    /// every dev/debug session - skip entirely rather than erroring on a nonexistent install.</summary>
    private static async Task CheckForUpdatesAsync()
    {
        try
        {
            var mgr = new UpdateManager(new Velopack.Sources.GithubSource(
                "https://github.com/misonyah/VrcPhotoManager", null, false));
            if (!mgr.IsInstalled) return;

            var newVersion = await mgr.CheckForUpdatesAsync();
            if (newVersion is null) return;

            await mgr.DownloadUpdatesAsync(newVersion);
        }
        catch
        {
            // Best-effort - offline, rate-limited, no releases yet, etc. are all fine; the app
            // just runs at its current version until the next successful check.
        }
    }

    /// <summary>Uses CcipEmbeddingService.ComputeMatchScores (the real learned metric model,
    /// see its doc comment) rather than plain cosine similarity - a diagnostic built on cosine
    /// similarity would be measuring something CCIP's own accept/reject logic never actually
    /// uses, silently misrepresenting real suggestion quality.</summary>
    private static void RunCcipSimilarityDiagnostic(string dir1, string dir2)
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcdnManager");
        var repo = new Data.PhotoRepository(Path.Combine(dataDir, "vrcdn_manager.db"));
        string? modelDir = repo.GetStringSetting(Services.SettingsKeys.CcipModelDir);
        if (modelDir is null) { Console.WriteLine("CCIP model dir not configured."); return; }
        var ccip = Services.CcipEmbeddingService.TryCreate(modelDir, out string? error);
        if (ccip is null) { Console.WriteLine($"Unavailable: {error}"); return; }

        string[] imageExtensions = [".png", ".jpg", ".jpeg", ".webp"];
        var files1 = Directory.GetFiles(dir1).Where(f => imageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
        var files2 = Directory.GetFiles(dir2).Where(f => imageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
        Console.WriteLine($"dir1: {files1.Count} images, dir2: {files2.Count} images");

        var embeddings1 = files1.Select(f => ccip.ComputeEmbeddingFromBytes(File.ReadAllBytes(f))).ToList();
        var embeddings2 = files2.Select(f => ccip.ComputeEmbeddingFromBytes(File.ReadAllBytes(f))).ToList();

        var within1 = PairwiseMatchScores(ccip, embeddings1);
        var within2 = PairwiseMatchScores(ccip, embeddings2);
        var cross = new List<float>();
        foreach (var a in embeddings1)
        {
            cross.AddRange(ccip.ComputeMatchScores(a, embeddings2));
        }

        ReportStats("Within dir1 (same person) - higher score = more similar", within1);
        ReportStats("Within dir2 (same person) - higher score = more similar", within2);
        ReportStats("Cross dir1<->dir2 (different people) - higher score = more similar", cross);
    }

    private static List<float> PairwiseMatchScores(Services.CcipEmbeddingService ccip, List<float[]> embeddings)
    {
        var result = new List<float>();
        for (int i = 0; i < embeddings.Count; i++)
        {
            result.AddRange(ccip.ComputeMatchScores(embeddings[i], embeddings.Skip(i + 1).ToList()));
        }
        return result;
    }

    private static void ReportStats(string label, List<float> values)
    {
        if (values.Count == 0) { Console.WriteLine($"{label}: no pairs"); return; }
        Console.WriteLine($"{label}: min={values.Min():F4} avg={values.Average():F4} max={values.Max():F4} (n={values.Count})");
    }

    private static void RunCcipEmbedDiagnostic(string imagePath)
    {
        // Same "VrcdnManager" data-dir + vrcdn_manager.db pattern as RunVrcdnSyncDiagnostic -
        // deliberately still the pre-rename folder name so existing installs keep their database.
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcdnManager");
        var repo = new Data.PhotoRepository(Path.Combine(dataDir, "vrcdn_manager.db"));
        string? modelDir = repo.GetStringSetting(Services.SettingsKeys.CcipModelDir);
        if (modelDir is null)
        {
            Console.WriteLine("CCIP model dir not configured (set it via Settings first).");
            return;
        }

        var ccip = Services.CcipEmbeddingService.TryCreate(modelDir, out string? error);
        if (ccip is null)
        {
            Console.WriteLine($"Unavailable: {error}");
            return;
        }

        byte[] bytes = File.ReadAllBytes(imagePath);
        float[] embedding = ccip.ComputeEmbeddingFromBytes(bytes);
        Console.WriteLine($"Embedding length: {embedding.Length}");
        Console.WriteLine($"First 5 values: {string.Join(", ", embedding.Take(5).Select(v => v.ToString("F4")))}");
        float norm = MathF.Sqrt(embedding.Sum(v => v * v));
        Console.WriteLine($"L2 norm (should be ~1.0): {norm:F4}");
    }

    /// <summary>Headless equivalent of clicking "Suggest Faces" in the running app - runs
    /// against the real live database (no scratch-copy convention here, unlike
    /// --debug-tag-faces/--test-face-repo, since this is exactly the same write the button
    /// itself performs), so the single-instance mutex check above already guarantees the real
    /// app isn't running concurrently. Shares FaceSuggestionService.RunAsync with
    /// MainViewModel.SuggestFacesAsync so this never drifts from the actual button's logic.</summary>
    private static void RunSuggestFacesDiagnostic()
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcdnManager");
        string dbPath = Path.Combine(dataDir, "vrcdn_manager.db");
        var photoRepo = new Data.PhotoRepository(dbPath);
        var faceRepo = new Data.FaceRepository(dbPath);
        var libraries = new Data.LibraryRepository(dbPath);

        string? modelDir = photoRepo.GetStringSetting(Services.SettingsKeys.CcipModelDir);
        if (modelDir is null)
        {
            Console.WriteLine("CCIP model dir not configured (set it via Settings first).");
            return;
        }
        var ccip = Services.CcipEmbeddingService.TryCreate(modelDir, out string? error);
        if (ccip is null)
        {
            Console.WriteLine($"Unavailable: {error}");
            return;
        }

        // Same PhotoSourceResolver every UI/diagnostic consumer goes through (see
        // RunResolvePhotoDiagnostic) - needed here so an eligible-but-not-yet-cached Discord
        // photo (see isEligible below) actually gets downloaded before RunAsync's embedding loop
        // tries to read it, same fix as MainViewModel.SuggestFacesAsync.
        var credentials = new Services.CredentialStore(photoRepo);
        string? discordToken = credentials.LoadDiscordBotToken();
        var discordClient = discordToken is not null ? new Services.DiscordApiClient(discordToken) : null;
        var cache = new Services.DiscordPhotoCacheService(Path.Combine(dataDir, "DiscordCache"));
        var resolver = new Services.PhotoSourceResolver(photoRepo, cache, discordClient);

        // Same eligibility rule as MainViewModel.IsEligibleForBatchOperation - skip an uncached
        // Discord photo unless its library has explicitly opted into auto-downloading originals.
        bool isEligible(Models.Photo p) => p.RemoteSourceId is null || p.LocalPath is not null
            || libraries.GetById(p.LibraryId)?.AutoDownloadOriginals == true;

        var photos = photoRepo.GetAll();
        var avatarTypeById = photos.ToDictionary(p => p.Id, p => p.AvatarType);
        Data.PhotoRepository? eliminationRepo = photoRepo.GetBoolSetting(Services.SettingsKeys.EnableExifElimination, true) ? photoRepo : null;

        // Same Task.Run(...).GetAwaiter().GetResult() pattern as RunVrcdnSyncDiagnostic -
        // OnStartup runs on the WPF Dispatcher thread; blocking directly on an async chain
        // that resumes on that same SynchronizationContext deadlocks.
        Task.Run(async () =>
        {
            // Only eligible photos get a path entry at all (an ineligible one is a silent skip
            // in RunAsync's embedding loop, not a caught exception); an eligible-but-uncached
            // Discord photo that still needs embedding gets resolved/downloaded here first - see
            // MainViewModel.SuggestFacesAsync for the identical, fuller-commented version of this.
            var needingEmbeddingPhotoIds = faceRepo.GetDetectedFacesWithoutEmbedding()
                .Select(f => f.PhotoId).ToHashSet();
            var pathById = new Dictionary<long, string>();
            foreach (var p in photos)
            {
                if (!isEligible(p)) continue;
                if (p.LocalPath is not null) { pathById[p.Id] = p.LocalPath; continue; }
                if (!needingEmbeddingPhotoIds.Contains(p.Id)) continue;
                try
                {
                    pathById[p.Id] = await resolver.ResolveLocalPathAsync(p);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Resolve failed for photo {p.Id}: {ex.Message}");
                }
            }

            var result = await Services.FaceSuggestionService.RunAsync(
                faceRepo, ccip, pathById, avatarTypeById, msg => Console.WriteLine(msg),
                photos: eliminationRepo);
            string exifPart = result.ExifEliminations > 0 ? $" {result.ExifEliminations} identified by VRCX-presence elimination." : "";
            if (result.NoEligiblePeople)
            {
                Console.WriteLine(result.ExifEliminations > 0
                    ? $"Suggest Faces done:{exifPart} No registered person has enough reference photos yet for CCIP matching (need >= {Services.FaceMatcher.MinReferenceEmbeddings})."
                    : $"No registered person has enough reference photos yet (need >= {Services.FaceMatcher.MinReferenceEmbeddings}: profile picture + confirmed tags combined).");
                return;
            }
            Console.WriteLine($"Suggest Faces done: {result.Embedded} embeddings computed, {result.Suggested} new suggestions across {result.EligiblePeople} eligible people"
                + (result.EliminationsApplied > 0 ? $" ({result.EliminationsApplied} faces had a candidate eliminated - already confirmed elsewhere in the same photo)." : ".")
                + exifPart);
        }).GetAwaiter().GetResult();
    }

    /// <summary>Headless equivalent of clicking "Detect Faces" in the running app - runs
    /// against the real live database, same rationale as RunSuggestFacesDiagnostic. Deliberately
    /// does NOT honor SettingsKeys.SkipResolvedPhotosOnFaceScan (unlike MainViewModel.
    /// ScanFacesAsync's normal incremental behavior) - this exists specifically for the
    /// "swapped in a better detector model" case that setting's own doc comment already
    /// anticipates: a photo that was previously fully resolved under the OLD detector (every
    /// detection tagged) can still have real, previously-missed faces the new detector would
    /// find, so every photo needs a genuine full rescan here, not just the ones with something
    /// still outstanding.</summary>
    private static void RunDetectFacesDiagnostic()
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcdnManager");
        string dbPath = Path.Combine(dataDir, "vrcdn_manager.db");
        var photoRepo = new Data.PhotoRepository(dbPath);
        var faceRepo = new Data.FaceRepository(dbPath);

        string? modelDir = photoRepo.GetStringSetting(Services.SettingsKeys.FaceDetectionModelDir);
        if (modelDir is null)
        {
            Console.WriteLine("Face detection model dir not configured (set it via Settings first).");
            return;
        }
        var detector = Services.FaceDetectionService.TryCreate(modelDir, out string? error);
        if (detector is null)
        {
            Console.WriteLine($"Unavailable: {error}");
            return;
        }

        // Same PhotoSourceResolver every UI consumer now goes through (see MainViewModel's
        // constructor / RunResolvePhotoDiagnostic) - a Discord photo not yet cached locally gets
        // downloaded-and-cached on demand here too, instead of DetectFaces choking on a null/
        // stale LocalPath.
        var credentials = new Services.CredentialStore(photoRepo);
        string? discordToken = credentials.LoadDiscordBotToken();
        var discordClient = discordToken is not null ? new Services.DiscordApiClient(discordToken) : null;
        var cache = new Services.DiscordPhotoCacheService(Path.Combine(dataDir, "DiscordCache"));
        var resolver = new Services.PhotoSourceResolver(photoRepo, cache, discordClient);

        var photos = photoRepo.GetAll();
        Console.WriteLine($"Scanning {photos.Count} photos (full rescan, not just unresolved ones)...");

        int processed = 0, totalExisting = 0, totalNew = 0, totalRemoved = 0;
        // Task.Run(...).GetAwaiter().GetResult() pattern, same as every other async diagnostic
        // in this file (see RunResolvePhotoDiagnostic/RunVrcdnSyncDiagnostic) - OnStartup runs
        // on the WPF Dispatcher's SynchronizationContext, so blocking directly on this async
        // loop would deadlock.
        Task.Run(async () =>
        {
            foreach (var photo in photos)
            {
                try
                {
                    string localPath = await resolver.ResolveLocalPathAsync(photo);
                    var faces = detector.DetectFaces(localPath);
                    var result = faceRepo.InsertDetectedFaces(photo.Id, faces);
                    photoRepo.SetFacesScanned(photo.Id);
                    totalExisting += result.Existing;
                    totalNew += result.New;
                    totalRemoved += result.Removed;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Face detection failed for {photo.FileName}: {ex.Message}");
                }

                processed++;
                if (processed % 25 == 0 || processed == photos.Count)
                {
                    Console.WriteLine($"Scanning for faces... {processed}/{photos.Count} photos, {totalNew} new, {totalExisting} existing so far");
                }
            }
        }).GetAwaiter().GetResult();

        Console.WriteLine($"Face scan complete: {totalNew} new faces, {totalExisting} existing across {photos.Count} photos"
            + (totalRemoved > 0 ? $" ({totalRemoved} stale untagged boxes removed)." : "."));
    }

    /// <summary>Headless equivalent of clicking "Classify Avatars" in the running app - same
    /// selection logic as MainViewModel.ClassifyAvatarsAsync (classify anything missing a result
    /// or previously scored "no confident match", skip photos that already have per-region
    /// results from a prior multi-avatar run), run sequentially rather than with bounded
    /// concurrency since this is a one-off diagnostic, not a UI-responsiveness-sensitive path.
    /// AvatarCatalogRepository.GetOrCreateByTrainedCatalogId resolves each classification's flat
    /// "booth:"/"local:" id to a real AvatarCatalog row exactly like the real UI path does.</summary>
    private static void RunClassifyAvatarsDiagnostic()
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcdnManager");
        string dbPath = Path.Combine(dataDir, "vrcdn_manager.db");
        var photoRepo = new Data.PhotoRepository(dbPath);
        var avatarRegions = new Data.AvatarRegionRepository(dbPath);
        var avatarCatalog = new Data.AvatarCatalogRepository(dbPath);
        var libraries = new Data.LibraryRepository(dbPath);

        string? modelDir = photoRepo.GetStringSetting(Services.SettingsKeys.AvatarModelDir);
        if (modelDir is null || !Directory.Exists(modelDir))
        {
            modelDir = Directory.Exists(Services.DefaultModelPaths.Avatar) ? Services.DefaultModelPaths.Avatar : null;
        }
        if (modelDir is null)
        {
            Console.WriteLine("Avatar classifier model dir not configured (set it via Settings first).");
            return;
        }
        var classifier = Services.AvatarTypeService.TryCreate(modelDir, out string? error);
        if (classifier is null)
        {
            Console.WriteLine($"Unavailable: {error}");
            return;
        }

        string? bodyModelDir = photoRepo.GetStringSetting(Services.SettingsKeys.AvatarBodyModelDir);
        if (bodyModelDir is null || !Directory.Exists(bodyModelDir))
        {
            bodyModelDir = Directory.Exists(Services.DefaultModelPaths.AvatarBodyDetection)
                ? Services.DefaultModelPaths.AvatarBodyDetection : null;
        }
        var bodyDetector = bodyModelDir is not null
            ? Services.AvatarBodyDetectionService.TryCreate(bodyModelDir, out _)
            : null;
        Console.WriteLine(bodyDetector is not null
            ? "Multi-avatar body detection available - group photos will get per-avatar regions."
            : "Multi-avatar body detection not configured - every photo gets one whole-photo classification.");

        // Same PhotoSourceResolver every UI consumer now goes through (see MainViewModel's
        // constructor / RunResolvePhotoDiagnostic) - a Discord photo not yet cached locally gets
        // downloaded-and-cached on demand here too, instead of DetectBodies/Classify choking on
        // a null/stale LocalPath.
        var credentials = new Services.CredentialStore(photoRepo);
        string? discordToken = credentials.LoadDiscordBotToken();
        var discordClient = discordToken is not null ? new Services.DiscordApiClient(discordToken) : null;
        var cache = new Services.DiscordPhotoCacheService(Path.Combine(dataDir, "DiscordCache"));
        var resolver = new Services.PhotoSourceResolver(photoRepo, cache, discordClient);

        // Same eligibility rule as MainViewModel.IsEligibleForBatchOperation - skip an uncached
        // Discord photo unless its library has explicitly opted into auto-downloading originals,
        // so this headless diagnostic and the "Classify Avatars" button never drift out of sync
        // on which photos a batch run is actually allowed to touch.
        bool isEligible(Models.Photo p) => p.RemoteSourceId is null || p.LocalPath is not null
            || libraries.GetById(p.LibraryId)?.AutoDownloadOriginals == true;

        var missingIds = photoRepo.GetPhotoIdsMissingAvatarType();
        var retryIds = photoRepo.GetPhotoIdsWithNoConfidentMatch();
        var regionIds = avatarRegions.GetPhotoIdsWithRegions();
        var toClassify = photoRepo.GetAll()
            .Where(p => (missingIds.Contains(p.Id) || retryIds.Contains(p.Id)) && !regionIds.Contains(p.Id) && isEligible(p))
            .ToList();
        if (toClassify.Count == 0) { Console.WriteLine("Nothing to classify - every photo already has an avatar-type result."); return; }
        Console.WriteLine($"Classifying {toClassify.Count} photos...");

        int processed = 0, matched = 0, multiAvatarPhotos = 0, regionsCreated = 0, failed = 0;
        // Task.Run(...).GetAwaiter().GetResult() pattern, same as every other async diagnostic
        // in this file - OnStartup runs on the WPF Dispatcher's SynchronizationContext, so
        // blocking directly on this async loop would deadlock.
        Task.Run(async () =>
        {
            foreach (var photo in toClassify)
            {
                try
                {
                    string localPath = await resolver.ResolveLocalPathAsync(photo);
                    var bodies = bodyDetector?.DetectBodies(localPath) ?? [];
                    if (bodies.Count < 2)
                    {
                        var (label, catalogId, confidence) = classifier.Classify(localPath);
                        long? resolvedCatalogId = label is not null && catalogId is not null
                            ? avatarCatalog.GetOrCreateByTrainedCatalogId(catalogId, label)
                            : null;
                        photoRepo.SetAvatarType(photo.Id, label, resolvedCatalogId, confidence);
                        if (label is not null) matched++;
                    }
                    else
                    {
                        multiAvatarPhotos++;
                        foreach (var body in bodies)
                        {
                            var (label, catalogId, confidence) = classifier.Classify(localPath, (body.X, body.Y, body.Width, body.Height));
                            long? resolvedCatalogId = label is not null && catalogId is not null
                                ? avatarCatalog.GetOrCreateByTrainedCatalogId(catalogId, label)
                                : null;
                            avatarRegions.AddAutoDetectedRegion(photo.Id, body.X, body.Y, body.Width, body.Height,
                                resolvedCatalogId, label, confidence);
                            regionsCreated++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"Avatar classification failed for {photo.FileName}: {ex.Message}");
                }

                processed++;
                if (processed % 25 == 0 || processed == toClassify.Count)
                {
                    Console.WriteLine($"Classifying avatars... {processed}/{toClassify.Count}, {matched} matched, {multiAvatarPhotos} multi-avatar so far");
                }
            }
        }).GetAwaiter().GetResult();

        string multiAvatarPart = multiAvatarPhotos > 0
            ? $" {multiAvatarPhotos} group photos got {regionsCreated} per-avatar regions instead (review in Tag Faces)."
            : "";
        Console.WriteLine($"Avatar classification complete: {matched}/{toClassify.Count} whole-photo matches"
            + (failed > 0 ? $", {failed} failed." : ".") + multiAvatarPart);
    }

    /// <summary>Single-photo avatar-classify smoke test - unlike RunClassifyAvatarsDiagnostic's
    /// batch path (which only logs ex.Message on failure), this prints the FULL exception
    /// (type + stack) so a load/decode failure like WPF's BitmapDecoder throwing "Unexpected
    /// property type or value" on a specific file's metadata can actually be diagnosed instead
    /// of just counted.</summary>
    private static void RunAvatarClassifySmokeTest(string imagePath)
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcdnManager");
        var photoRepo = new Data.PhotoRepository(Path.Combine(dataDir, "vrcdn_manager.db"));

        string? modelDir = photoRepo.GetStringSetting(Services.SettingsKeys.AvatarModelDir);
        if (modelDir is null || !Directory.Exists(modelDir))
        {
            modelDir = Directory.Exists(Services.DefaultModelPaths.Avatar) ? Services.DefaultModelPaths.Avatar : null;
        }
        if (modelDir is null)
        {
            Console.WriteLine("Avatar classifier model dir not configured (set it via Settings first).");
            return;
        }
        var classifier = Services.AvatarTypeService.TryCreate(modelDir, out string? error);
        if (classifier is null)
        {
            Console.WriteLine($"Unavailable: {error}");
            return;
        }

        try
        {
            var (label, catalogId, confidence) = classifier.Classify(imagePath);
            Console.WriteLine($"Label: {label ?? "(no confident match)"}");
            Console.WriteLine($"CatalogId: {catalogId ?? "(none)"}");
            Console.WriteLine($"Confidence: {confidence:F4}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED:");
            Console.WriteLine(ex.ToString());
        }
    }

    private static void RunDiscordTokenDiagnostic(string token)
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcdnManager");
        var repo = new Data.PhotoRepository(Path.Combine(dataDir, "vrcdn_manager.db"));
        var credentials = new Services.CredentialStore(repo);

        credentials.SaveDiscordBotToken(token);
        string? loaded = credentials.LoadDiscordBotToken();
        Console.WriteLine(loaded == token ? "OK: round-tripped correctly" : $"MISMATCH: got '{loaded}'");
    }

    private static void RunDiscordGuildsDiagnostic()
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcdnManager");
        var repo = new Data.PhotoRepository(Path.Combine(dataDir, "vrcdn_manager.db"));
        var credentials = new Services.CredentialStore(repo);
        string? token = credentials.LoadDiscordBotToken();
        if (token is null) { Console.WriteLine("No Discord bot token configured - run --test-discord-token first."); return; }

        using var client = new Services.DiscordApiClient(token);
        Task.Run(async () =>
        {
            var guilds = await client.GetGuildsAsync(CancellationToken.None);
            Console.WriteLine($"{guilds.Count} guild(s):");
            foreach (var g in guilds)
            {
                Console.WriteLine($"  {g.Id}  {g.Name}");
                var channels = await client.GetChannelsAsync(g.Id, CancellationToken.None);
                foreach (var c in channels) Console.WriteLine($"    #{c.Name} ({c.Id})");
            }
        }).GetAwaiter().GetResult();
    }

    private static void RunVrcxProfileLookupDiagnostic(string vrcUserId)
    {
        var service = Services.VrcxProfileLookupService.TryCreate(out string? error);
        if (service is null)
        {
            Console.WriteLine($"Unavailable: {error}");
            return;
        }

        // Same Task.Run(...).GetAwaiter().GetResult() pattern as RunVrcdnSyncDiagnostic -
        // OnStartup runs on the WPF Dispatcher thread; blocking directly on an async chain
        // that resumes on that same SynchronizationContext deadlocks.
        Task.Run(async () =>
        {
            byte[]? bytes = await service.TryFetchLatestThumbnailAsync(vrcUserId);
            Console.WriteLine(bytes is null
                ? "No thumbnail found (user never observed by VRCX, or fetch failed)."
                : $"Fetched {bytes.Length} bytes.");
        }).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Exercises FaceRepository/PhotoRepository's tagging methods end-to-end against a given
    /// db path (pass a scratch copy - this writes a throwaway person/label row). Verifies the
    /// LINQ actually translates to valid SQL, not just that it compiles.
    /// </summary>
    private static void RunFaceRepoDiagnostic(string dbPath, long photoId)
    {
        var faces = new Data.FaceRepository(dbPath);
        var photos = new Data.PhotoRepository(dbPath);

        var detected = faces.GetDetectedFaces(photoId);
        Console.WriteLine($"DetectedFaces for photo {photoId}: {detected.Count}");
        if (detected.Count == 0)
        {
            Console.WriteLine("No detected faces for this photo - pick one that has been face-scanned.");
            return;
        }

        var person = faces.FindOrCreatePersonByVrcUserId("usr_diagnostic_test_0000", "DiagnosticTestPerson");
        Console.WriteLine($"FindOrCreatePersonByVrcUserId -> Id={person.Id}, Name={person.Name}");

        long faceId = detected[0].Id;
        faces.UpsertFaceLabel(faceId, person.Id, confirmed: true, Models.FaceLabelSource.Manual);
        Console.WriteLine($"Tagged face {faceId} -> person {person.Id}");

        var labels = faces.GetFaceLabelsByPhoto(photoId);
        Console.WriteLine($"GetFaceLabelsByPhoto: {labels.Count} label(s), face {faceId} confirmed={labels[faceId].Confirmed}");

        var taggedIds = faces.GetTaggedUserIds();
        Console.WriteLine($"GetTaggedUserIds contains usr_diagnostic_test_0000: {taggedIds.Contains("usr_diagnostic_test_0000")}");

        var taggedPhotoIds = faces.GetTaggedPhotoIdsForUser("usr_diagnostic_test_0000");
        Console.WriteLine($"GetTaggedPhotoIdsForUser contains photo {photoId}: {taggedPhotoIds.Contains(photoId)}");

        faces.SetVrcProfileThumbnail(person.Id, [1, 2, 3]);
        Console.WriteLine("SetVrcProfileThumbnail: OK");

        var distinctPlayers = photos.GetDistinctPlayers();
        Console.WriteLine($"GetDistinctPlayers: {distinctPlayers.Count} distinct players");

        var playersForPhoto = photos.GetPlayersForPhoto(photoId);
        Console.WriteLine($"GetPlayersForPhoto({photoId}): {playersForPhoto.Count} player(s)");

        faces.DeleteFaceLabel(faceId);
        Console.WriteLine("DeleteFaceLabel: OK (cleanup)");
    }

    private static void RunLibraryRepoDiagnostic()
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcdnManager");
        string dbPath = Path.Combine(dataDir, "vrcdn_manager.db");
        _ = new Data.PhotoRepository(dbPath); // triggers migration (see PhotoRepository.EnsureDatabaseUpToDate)
        var libraries = new Data.LibraryRepository(dbPath);

        var created = libraries.AddLocalFolder(@"C:\temp\test-library-diagnostic", "Diagnostic Test Folder");
        Console.WriteLine($"Created library id={created.Id}, type={created.Type}, path={created.LocalPath}");

        var all = libraries.GetAll();
        Console.WriteLine($"Total libraries: {all.Count}");
        foreach (var lib in all)
        {
            Console.WriteLine($"  [{lib.Id}] {lib.Type} \"{lib.DisplayName}\" path={lib.LocalPath} discord={lib.DiscordChannelId}");
        }

        libraries.Remove(created.Id);
        Console.WriteLine($"Removed id={created.Id}, remaining: {libraries.GetAll().Count}");
    }

    private static void RunVrcdnSyncDiagnostic()
    {
        // Deliberately still "VrcdnManager" - the on-disk data folder name, kept stable
        // across the app's rename so existing installs don't lose their database.
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcdnManager");
        var repo = new Data.PhotoRepository(Path.Combine(dataDir, "vrcdn_manager.db"));
        var credentials = new Services.CredentialStore(repo);

        string? cookie;
        try
        {
            cookie = credentials.LoadCookie(null);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"Cookie load failed: {ex.Message}");
            return;
        }

        if (cookie is null)
        {
            Console.WriteLine("No stored session cookie - not logged in.");
            return;
        }
        Console.WriteLine($"Cookie loaded (length={cookie.Length}).");

        var api = new Services.VrcdnApiClient(cookie);
        try
        {
            // OnStartup runs on the WPF Dispatcher thread, which has a SynchronizationContext -
            // blocking here with .GetAwaiter().GetResult() directly on an async chain that tries
            // to resume on that same context deadlocks. Task.Run hops off it first.
            Task.Run(async () =>
            {
                string username = await api.GetUsernameAsync();
                Console.WriteLine($"Username: {username}");

                var objects = await api.ListObjectsAsync();
                Console.WriteLine($"ListObjectsAsync returned {objects.Count} objects.");

                var unresolved = repo.SyncRemoteMatches(
                    objects.Select(o => (o.Original, o.Id, o.Extension, o.Size)), username);
                Console.WriteLine($"Matched {objects.Count - unresolved.Count}/{objects.Count}, {unresolved.Count} unresolved.");
                foreach (string name in unresolved)
                {
                    Console.WriteLine($"  UNRESOLVED: {name}");
                }
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Throwaway diagnostic for a real "Remove from VRCDN didn't work" report - calls
    /// RemoveObjectAsync directly against a single remote object id and prints the real
    /// exception/response, since MainViewModel.RemoveFromVrcdnAsync's catch block only shows
    /// ex.Message in StatusMessage (easy to miss/lose in the UI).</summary>
    private static void RunRemoveObjectDiagnostic(string objectId)
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcdnManager");
        var repo = new Data.PhotoRepository(Path.Combine(dataDir, "vrcdn_manager.db"));
        var credentials = new Services.CredentialStore(repo);
        string? cookie = credentials.LoadCookie(null);
        if (cookie is null)
        {
            Console.WriteLine("No stored session cookie - not logged in.");
            return;
        }

        var api = new Services.VrcdnApiClient(cookie);
        try
        {
            Task.Run(async () =>
            {
                Console.WriteLine($"Removing object {objectId}...");
                await api.RemoveObjectAsync(objectId);
                Console.WriteLine("RemoveObjectAsync returned successfully.");
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.GetType().FullName}: {ex.Message}");
            for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            {
                Console.WriteLine($"  INNER: {inner.GetType().FullName}: {inner.Message}");
            }
        }
    }

    private static void RunClassifierSmokeTest(string imagePath)
    {
        var tagger = Services.WdTaggerService.TryCreate(@"D:\AI-Tools\wd14-tagger\model", out string? error);
        if (tagger is null)
        {
            Console.WriteLine($"FAILED TO LOAD: {error}");
            return;
        }
        string rating = tagger.ClassifyRating(imagePath);
        Console.WriteLine($"RATING: {rating}");
    }

    private static void RunDiscordCacheEvictDiagnostic(long limitBytes)
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcdnManager");
        var repo = new Data.PhotoRepository(Path.Combine(dataDir, "vrcdn_manager.db"));
        string cacheDir = Path.Combine(dataDir, "DiscordCache");
        var cache = new Services.DiscordPhotoCacheService(cacheDir);

        var before = repo.GetCachedDiscordPhotosForEviction();
        Console.WriteLine($"Before: {before.Count} cached photos, {before.Sum(p => p.FileSize)} bytes total");

        // Wrapped in Task.Run(...).GetAwaiter().GetResult() rather than a bare
        // .GetAwaiter().GetResult() - EnforceCacheLimitAsync awaits Task.Run(() => File.Delete(...))
        // internally, and a bare call here would try to resume that continuation on this same
        // Dispatcher thread while it's blocked synchronously waiting for it - the same deadlock
        // class documented on RunResolvePhotoDiagnostic/RunVrcdnSyncDiagnostic elsewhere in this
        // file. Confirmed live: the bare form hung indefinitely running this diagnostic for real.
        Task.Run(() => cache.EnforceCacheLimitAsync(repo, limitBytes)).GetAwaiter().GetResult();

        var after = repo.GetCachedDiscordPhotosForEviction();
        Console.WriteLine($"After (limit={limitBytes}): {after.Count} cached photos, {after.Sum(p => p.FileSize)} bytes total");
    }

    /// <summary>Headless verification for PhotoSourceResolver - resolves a single photo's local
    /// path (downloading-and-caching a Discord original on demand, or short-circuiting instantly
    /// for an already-cached/local-folder photo) and reports whether the result is a real,
    /// existing, non-empty file. Same Task.Run(...).GetAwaiter().GetResult() pattern as
    /// RunVrcxProfileLookupDiagnostic/RunVrcdnSyncDiagnostic - OnStartup runs on the WPF
    /// Dispatcher thread, which has a SynchronizationContext; blocking directly on an async
    /// chain that tries to resume on that same context deadlocks.</summary>
    private static void RunResolvePhotoDiagnostic(long photoId)
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcdnManager");
        var repo = new Data.PhotoRepository(Path.Combine(dataDir, "vrcdn_manager.db"));
        var photo = repo.GetAll().FirstOrDefault(p => p.Id == photoId);
        if (photo is null) { Console.WriteLine("Photo not found."); return; }

        var credentials = new Services.CredentialStore(repo);
        string? token = credentials.LoadDiscordBotToken();
        var discordClient = token is not null ? new Services.DiscordApiClient(token) : null;
        var cache = new Services.DiscordPhotoCacheService(Path.Combine(dataDir, "DiscordCache"));
        var resolver = new Services.PhotoSourceResolver(repo, cache, discordClient);

        Task.Run(async () =>
        {
            string path = await resolver.ResolveLocalPathAsync(photo);
            Console.WriteLine($"Resolved: {path}");
            Console.WriteLine($"File exists: {File.Exists(path)}, size: {(File.Exists(path) ? new FileInfo(path).Length : 0)} bytes");
        }).GetAwaiter().GetResult();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        base.OnExit(e);

        // Same DirectML-foreground-thread risk ExitProcess's doc comment covers for the
        // diagnostic CLI paths - Suggest Faces (CcipEmbeddingService) can run from the real GUI
        // too, so a normal window-close shutdown needs the same forced termination, not just the
        // headless hooks. OnExit is already the last real app-level hook (Main() has nothing
        // after app.Run() to interrupt), so this is safe for every shutdown path.
        Environment.Exit(0);
    }
}
