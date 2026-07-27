using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;

namespace VrcdnManager;

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

        // Unhandled exceptions on the UI thread (e.g. from an async void command handler)
        // would otherwise take the whole app down with no explanation - show them instead.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"Unexpected error:\n\n{args.Exception.Message}",
                "VRCDN Manager", MessageBoxButton.OK, MessageBoxImage.Error);
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
