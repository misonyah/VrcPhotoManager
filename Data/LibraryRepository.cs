using Microsoft.EntityFrameworkCore;
using VrcPhotoManager.Models;

namespace VrcPhotoManager.Data;

/// <summary>CRUD for Library - see Models/Library.cs and the multi-library design spec.</summary>
public class LibraryRepository(string dbPath)
{
    private VrcdnDbContext NewContext() => new(dbPath);

    public Library AddLocalFolder(string path, string displayName)
    {
        using var context = NewContext();
        var library = new Library { Type = LibraryType.LocalFolder, LocalPath = path, DisplayName = displayName };
        context.Libraries.Add(library);
        context.SaveChanges();
        return library;
    }

    public Library AddDiscordChannel(string guildId, string channelId, string displayName, string? guildIconUrl = null)
    {
        using var context = NewContext();
        var library = new Library
        {
            Type = LibraryType.DiscordChannel,
            DiscordGuildId = guildId,
            DiscordGuildIconUrl = guildIconUrl,
            DiscordChannelId = channelId,
            DisplayName = displayName,
        };
        context.Libraries.Add(library);
        context.SaveChanges();
        return library;
    }

    public List<Library> GetAll()
    {
        using var context = NewContext();
        return context.Libraries.AsNoTracking().OrderBy(l => l.Id).ToList();
    }

    public Library? GetById(long id)
    {
        using var context = NewContext();
        return context.Libraries.AsNoTracking().FirstOrDefault(l => l.Id == id);
    }

    public void Remove(long id)
    {
        using var context = NewContext();
        context.Libraries.Where(l => l.Id == id).ExecuteDelete();
    }

    public void UpdateLastSynced(long id, DateTime syncedAt, string? lastMessageId)
    {
        using var context = NewContext();
        context.Libraries.Where(l => l.Id == id).ExecuteUpdate(s => s
            .SetProperty(l => l.LastSyncedAt, syncedAt)
            .SetProperty(l => l.LastSyncedMessageId, lastMessageId));
    }

    public void SetAutoDownloadOriginals(long id, bool value)
    {
        using var context = NewContext();
        context.Libraries.Where(l => l.Id == id).ExecuteUpdate(s => s.SetProperty(l => l.AutoDownloadOriginals, value));
    }
}
