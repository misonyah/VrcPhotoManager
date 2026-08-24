namespace VrcPhotoManager.Services;

/// <summary>Shared AppSetting key names, so MainViewModel's resolvers and the Settings
/// window agree on what they're reading/writing without duplicating magic strings.</summary>
public static class SettingsKeys
{
    public const string WdModelDir = "wd14_model_dir";
    /// <summary>deepghs/ccip_onnx's model folder (two files: model_feat.onnx,
    /// model_metrics.onnx - see CcipEmbeddingService) - replaced the earlier general-purpose
    /// CLIP model for face matching, which was never trained to discriminate between people/
    /// characters at all (see FaceMatcher.cs's doc comment). "clip_model_dir"/"clip_model_etag"
    /// are deliberately not migrated - this is a one-time model swap, not a cross-version
    /// setting, and every stored face embedding needs recomputing against the new model anyway
    /// (the two are NOT comparable - see Models.DetectedFace.Embedding's doc comment).</summary>
    public const string CcipModelDir = "ccip_model_dir";
    /// <summary>deepghs/anime_face_detection's face_detect_v1.4_s (YOLOv8s, model.onnx) -
    /// replaced the earlier bundled lbpcascade_animeface.xml asset after a real, measured
    /// comparison (see FaceDetectionService.cs's doc comment): the LBP cascade missed 2 of 3
    /// clearly-visible faces in a real close-up photo, and tuning its own parameters couldn't
    /// fix it. Downloadable via Settings like the other ML models, rather than bundled with the
    /// app - the old cascade was a ~1MB XML file, small enough to ship; this one is ~45MB.</summary>
    public const string FaceDetectionModelDir = "face_detection_model_dir";
    public const string AvatarModelDir = "avatar_model_dir";
    /// <summary>deepghs/anime_person_detection's person_detect_v1.3_s (YOLOv8s, model.onnx) -
    /// detects each individual avatar's body in a photo, so "Classify Avatars" can run the
    /// existing whole-photo classifier per detected body instead of once for the whole frame -
    /// see AvatarBodyDetectionService.cs. Optional, same as every other model here: with this
    /// unconfigured, Classify Avatars just keeps its original single whole-photo-guess
    /// behavior.</summary>
    public const string AvatarBodyModelDir = "avatar_body_model_dir";
    public const string WdModelEtag = "wd14_model_etag";
    public const string CcipModelEtag = "ccip_model_etag";
    public const string FaceDetectionModelEtag = "face_detection_model_etag";
    public const string AvatarModelEtag = "avatar_model_etag";
    public const string AvatarBodyModelEtag = "avatar_body_model_etag";
    public const string AutoCopyVrcdnUrlOnHover = "auto_copy_vrcdn_url_on_hover";
    public const string HoverPreviewDelaySeconds = "hover_preview_delay_seconds";
    public const string SkipResolvedPhotosOnFaceScan = "skip_resolved_photos_on_face_scan";
    /// <summary>How wide a window (seconds) GamelogCorrelationService.FindTraveledTogether
    /// allows between your own instance transition and a friend's matching departure/arrival
    /// before counting them as having portal-hopped with you - see MetadataWindow's "Traveled
    /// together" section. Real-world portal/invite hops aren't perfectly synchronized (you might
    /// go through before or after everyone else), so this needs to be generous - a real design
    /// discussion landed on 90 seconds as the default, user-configurable since the right width
    /// depends on how a given friend group actually travels together.</summary>
    public const string PortalHopWindowSeconds = "portal_hop_window_seconds";
    /// <summary>Default true. See FaceSuggestionService.RunAsync's elimination pass: when a
    /// photo has VRCX presence data (photo_players or gamelog_inferred_players) and exactly one
    /// detected face is still unidentified while exactly one listed person is unaccounted for,
    /// that face is that person by pure elimination - no CCIP embedding involved at all. A
    /// photo_players (native VRCX metadata) match auto-confirms directly; a
    /// gamelog_inferred_players (weaker fallback - present in the instance, not necessarily in
    /// frame) match lands as a normal pending suggestion instead. FaceLabelSource.ExifElimination
    /// marks results from this pass specifically.</summary>
    public const string EnableExifElimination = "enable_exif_elimination";

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

    // Filter/sort state - same write-once-at-Closing, read-once-at-startup pattern as the
    // session-state keys above (see MainViewModel.SaveFilterState/RestoreFilterState).
    public const string RatingFilter = "rating_filter";
    public const string StatusFilter = "status_filter";
    public const string UploadCropModeFilter = "upload_crop_mode_filter";
    public const string AvatarTypeFilter = "avatar_type_filter";
    public const string FaceCountFilter = "face_count_filter";
    public const string PlayerCountFilter = "player_count_filter";
    public const string MinSuggestionConfidence = "min_suggestion_confidence";
    public const string SortOption = "sort_option";
    public const string TaggedOnlyFilter = "tagged_only_filter";
    public const string OwnPhotosOnlyFilter = "own_photos_only_filter";
    /// <summary>Serialized PlayerFilterCriteria rows - see MainViewModel.SaveFilterState for the
    /// format. Restored separately from the other filters above, after RefreshPlayerFilterOptions
    /// has run (needs the real PlayerFilterOption objects to match against), not directly in the
    /// constructor.</summary>
    public const string PlayerFilterCriteria = "player_filter_criteria";

    // VRCDN index file (for a Udon world script to randomly pick an uploaded photo - see
    // MainViewModel.GenerateVrcdnIndexAsync) - the base filename (no extension) and format
    // ("csv", "json", or "txt") used the next time the index is generated. IndexFileNameBase
    // defaults to a random GUID the first time it's read (see MainViewModel's resolver) rather
    // than a fixed name, only to avoid accidentally colliding with a real photo's filename in
    // VRCDN's own file list - it's otherwise just a human-readable label, since VRCDN assigns
    // the actual object id/URL server-side regardless of what name is uploaded under.
    public const string IndexFileNameBase = "vrcdn_index_file_name_base";
    public const string IndexFileFormat = "vrcdn_index_file_format";

    // Not secret (a gist id/URL identifies but doesn't grant access) - the GitHub token itself
    // is stored separately via CredentialStore.SaveGistToken/LoadGistToken (DPAPI-encrypted).
    public const string GistId = "vrcdn_index_gist_id";
    public const string GistIndexUrl = "vrcdn_index_gist_url";

    /// <summary>"jpg" (default) or "png" - set via Settings' "Upload Image Format" section
    /// (right column). PNG uploads are just as metadata-free as jpg - see
    /// ThumbnailService.ResizeAsync, which never attaches BitmapMetadata to either encoder.</summary>
    public const string UploadImageFormat = "upload_image_format";

    /// <summary>Discord full-size photo cache size limit in gigabytes - see
    /// DiscordPhotoCacheService.EnforceCacheLimitAsync for the eviction logic.</summary>
    public const string DiscordCacheSizeLimitGb = "discord_cache_size_limit_gb";
}
