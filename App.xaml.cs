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
