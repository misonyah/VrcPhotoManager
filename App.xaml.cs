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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, "Local\\VrcPhotoManager_SingleInstance", out bool isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show(
                "VRC Photo Manager is already running (or a diagnostic command is using its database) - only one instance can run at a time to avoid database lock conflicts.",
                "VRC Photo Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--test-classify")
        {
            RunClassifierSmokeTest(e.Args[1]);
            Shutdown();
            return;
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
            Shutdown();
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--test-metadata")
        {
            var meta = Services.PngMetadataReader.TryReadVrcxMetadata(e.Args[1]);
            Console.WriteLine(meta is null
                ? "No VRCX metadata found."
                : $"Author: {meta.Author?.DisplayName} ({meta.Author?.Id})\n" +
                  $"World: {meta.World?.Name} ({meta.World?.Id})\n" +
                  $"Players: {string.Join(", ", meta.Players?.Select(p => p.DisplayName) ?? [])}");
            Shutdown();
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--test-face-detect")
        {
            var detector = new Services.FaceDetectionService();
            var faces = detector.DetectFaces(e.Args[1]);
            Console.WriteLine($"Faces found: {faces.Count}");
            foreach (var f in faces)
            {
                Console.WriteLine($"  ({f.X}, {f.Y}) {f.Width}x{f.Height}");
            }
            Shutdown();
            return;
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
            Shutdown();
            return;
        }

        if (e.Args.Length == 1 && e.Args[0] == "--test-vrcdn-sync")
        {
            RunVrcdnSyncDiagnostic();
            Shutdown();
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--test-remove-object")
        {
            RunRemoveObjectDiagnostic(e.Args[1]);
            Shutdown();
            return;
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
            var photo = photos.GetAll().First(p => p.Id == photoId);
            var lookup = Services.VrcxProfileLookupService.TryCreate(out _);
            new Views.TagFacesWindow(faces, photos, avatarRegions, null, lookup, photo).ShowDialog();
            Shutdown();
            return;
        }

        if (e.Args.Length == 3 && e.Args[0] == "--test-face-repo")
        {
            RunFaceRepoDiagnostic(e.Args[1], long.Parse(e.Args[2]));
            Shutdown();
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--test-vrcx-profile-lookup")
        {
            RunVrcxProfileLookupDiagnostic(e.Args[1]);
            Shutdown();
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--test-clip-embed")
        {
            RunClipEmbedDiagnostic(e.Args[1]);
            Shutdown();
            return;
        }

        if (e.Args.Length == 3 && e.Args[0] == "--test-clip-similarity")
        {
            RunClipSimilarityDiagnostic(e.Args[1], e.Args[2]);
            Shutdown();
            return;
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

    private static void RunClipSimilarityDiagnostic(string dir1, string dir2)
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcdnManager");
        var repo = new Data.PhotoRepository(Path.Combine(dataDir, "vrcdn_manager.db"));
        string? modelDir = repo.GetStringSetting(Services.SettingsKeys.ClipModelDir);
        if (modelDir is null) { Console.WriteLine("CLIP model dir not configured."); return; }
        var clip = Services.ClipEmbeddingService.TryCreate(modelDir, out string? error);
        if (clip is null) { Console.WriteLine($"Unavailable: {error}"); return; }

        string[] imageExtensions = [".png", ".jpg", ".jpeg", ".webp"];
        var files1 = Directory.GetFiles(dir1).Where(f => imageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
        var files2 = Directory.GetFiles(dir2).Where(f => imageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
        Console.WriteLine($"dir1: {files1.Count} images, dir2: {files2.Count} images");

        var embeddings1 = files1.Select(f => clip.ComputeEmbeddingFromBytes(File.ReadAllBytes(f))).ToList();
        var embeddings2 = files2.Select(f => clip.ComputeEmbeddingFromBytes(File.ReadAllBytes(f))).ToList();

        var within1 = PairwiseSimilarities(embeddings1);
        var within2 = PairwiseSimilarities(embeddings2);
        var cross = new List<float>();
        foreach (var a in embeddings1)
            foreach (var b in embeddings2)
                cross.Add(Services.FaceMatcher.CosineSimilarity(a, b));

        ReportStats("Within dir1 (same person)", within1);
        ReportStats("Within dir2 (same person)", within2);
        ReportStats("Cross dir1<->dir2 (different people)", cross);
    }

    private static List<float> PairwiseSimilarities(List<float[]> embeddings)
    {
        var result = new List<float>();
        for (int i = 0; i < embeddings.Count; i++)
            for (int j = i + 1; j < embeddings.Count; j++)
                result.Add(Services.FaceMatcher.CosineSimilarity(embeddings[i], embeddings[j]));
        return result;
    }

    private static void ReportStats(string label, List<float> values)
    {
        if (values.Count == 0) { Console.WriteLine($"{label}: no pairs"); return; }
        Console.WriteLine($"{label}: min={values.Min():F4} avg={values.Average():F4} max={values.Max():F4} (n={values.Count})");
    }

    private static void RunClipEmbedDiagnostic(string imagePath)
    {
        // Same "VrcdnManager" data-dir + vrcdn_manager.db pattern as RunVrcdnSyncDiagnostic -
        // deliberately still the pre-rename folder name so existing installs keep their database.
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcdnManager");
        var repo = new Data.PhotoRepository(Path.Combine(dataDir, "vrcdn_manager.db"));
        string? modelDir = repo.GetStringSetting(Services.SettingsKeys.ClipModelDir);
        if (modelDir is null)
        {
            Console.WriteLine("CLIP model dir not configured (set it via Settings first).");
            return;
        }

        var clip = Services.ClipEmbeddingService.TryCreate(modelDir, out string? error);
        if (clip is null)
        {
            Console.WriteLine($"Unavailable: {error}");
            return;
        }

        byte[] bytes = File.ReadAllBytes(imagePath);
        float[] embedding = clip.ComputeEmbeddingFromBytes(bytes);
        Console.WriteLine($"Embedding length: {embedding.Length}");
        Console.WriteLine($"First 5 values: {string.Join(", ", embedding.Take(5).Select(v => v.ToString("F4")))}");
        float norm = MathF.Sqrt(embedding.Sum(v => v * v));
        Console.WriteLine($"L2 norm (should be ~1.0): {norm:F4}");
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

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
