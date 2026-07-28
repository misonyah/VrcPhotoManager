namespace VrcPhotoManager.Services;

/// <summary>Shared AppSetting key names, so MainViewModel's resolvers and the Settings
/// window agree on what they're reading/writing without duplicating magic strings.</summary>
public static class SettingsKeys
{
    public const string WdModelDir = "wd14_model_dir";
    public const string WdIndexDb = "wd14_index_db_path";
}
