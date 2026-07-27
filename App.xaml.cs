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
        // Unhandled exceptions on the UI thread (e.g. from an async void command handler)
        // would otherwise take the whole app down with no explanation - show them instead.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"Unexpected error:\n\n{args.Exception.Message}",
                "VRCDN Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
