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
}
