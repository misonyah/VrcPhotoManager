namespace VrcdnManager.Models;

public class AppSetting
{
    public required string Key { get; set; }
    public byte[]? Value { get; set; }
}
