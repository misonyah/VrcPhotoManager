namespace VrcPhotoManager.Services;

/// <summary>Shared AppSetting key names, so MainViewModel's resolvers and the Settings
/// window agree on what they're reading/writing without duplicating magic strings.</summary>
public static class SettingsKeys
{
    public const string WdModelDir = "wd14_model_dir";
    public const string ClipModelDir = "clip_model_dir";
    public const string AvatarModelDir = "avatar_model_dir";
    public const string WdModelEtag = "wd14_model_etag";
    public const string ClipModelEtag = "clip_model_etag";
    public const string AvatarModelEtag = "avatar_model_etag";
    public const string AutoCopyVrcdnUrlOnHover = "auto_copy_vrcdn_url_on_hover";
    public const string HoverPreviewDelaySeconds = "hover_preview_delay_seconds";
    public const string SkipResolvedPhotosOnFaceScan = "skip_resolved_photos_on_face_scan";

    // Session-state persistence (restore where you left off) - written once at Closing, read
    // once at startup, deliberately not live-synced on every change (ThumbnailSize alone can
    // change dozens of times per second during an Alt+scroll resize - see MainWindow's
    // HandleRowScroll/MainWindow_PreviewMouseWheel - so a write-on-every-change approach would
    // hammer SQLite for no benefit over a single write at shutdown).
    public const string WindowWidth = "window_width";
    public const string WindowHeight = "window_height";
    public const string WindowMaximized = "window_maximized";
    public const string LastThumbnailSize = "last_thumbnail_size";
    public const string LastScrollOffset = "last_scroll_offset";
}
