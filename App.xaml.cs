using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace VrcPhotoManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"Unexpected error:\n\n{args.Exception.Message}",
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
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
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
}
