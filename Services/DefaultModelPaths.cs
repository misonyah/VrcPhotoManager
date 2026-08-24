using System.IO;

namespace VrcPhotoManager.Services;

/// <summary>Stable, install-location-independent default folder for each downloadable
/// model, under %LOCALAPPDATA%\VrcPhotoManager - works across app updates and would keep
/// working under MSIX/Store packaging too, unlike a folder bundled next to the exe (not
/// guaranteed writable or stable there). Deliberately its own top-level folder, not nested
/// under the app's "VrcdnManager" data folder (that name is kept stable on purpose for its
/// existing database - see App.xaml.cs/MainViewModel.cs - and isn't otherwise something new
/// features should keep propagating). Only used as a fallback when nothing is configured
/// (and, for WD14, only when its legacy bundled-next-to-exe folder doesn't exist either) -
/// existing installs that already rely on a configured or bundled folder are unaffected.</summary>
public static class DefaultModelPaths
{
    private static string ModelsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcPhotoManager", "Models");

    public static string WdTagger => Path.Combine(ModelsRoot, "WD14");
    public static string Ccip => Path.Combine(ModelsRoot, "CCIP");
    public static string FaceDetection => Path.Combine(ModelsRoot, "FaceDetection");
    public static string Avatar => Path.Combine(ModelsRoot, "Avatar");
    public static string AvatarBodyDetection => Path.Combine(ModelsRoot, "AvatarBodyDetection");
}
