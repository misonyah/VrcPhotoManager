using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace VrcPhotoManager.Services;

/// <summary>
/// Cross-references a photo's capture time (parsed from its filename) against the local VRCX
/// account's own gamelog (gamelog_location + gamelog_join_leave) to infer who was present in
/// the instance - a fallback for photos with no VRCX-embedded PlayerList of their own (e.g.
/// taken by someone else in the same instance). Only ever consults this account's own gamelog;
/// a photo whose capture time isn't covered by any recorded visit gets no data rather than a
/// guess. See docs/superpowers/specs/2026-08-01-gamelog-player-inference-design.md.
/// </summary>
public class GamelogCorrelationService : IDisposable
{
    // Single-account machine (see project CLAUDE.md) - VRCX names this table after the local
    // account's VRChat user id with "usr_" stripped and dashes removed. Same identifier
    // VrcxProfileLookupService uses for its own table names.
    private const string LocalAccountIdNoPrefix = "f9065286b1f24b7fa00815fc2a117546";

    private readonly SqliteConnection _conn;
    private readonly List<(DateTime StartUtc, string Location, string? WorldName)> _visits;
    private readonly List<(DateTime StartUtc, string AvatarId)> _avatarSwitches;
    private readonly Dictionary<string, string> _avatarNamesById;

    /// <summary>Loads every recorded visit once up front (small table, a few thousand rows at
    /// most) rather than per-photo - this service is built for a batch run across the whole
    /// library, not a single lookup.</summary>
    private GamelogCorrelationService(string vrcxDbPath)
    {
        _conn = new SqliteConnection($"Data Source={vrcxDbPath};Mode=ReadOnly");
        _conn.Open();

        _visits = [];
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT created_at, location, world_name FROM gamelog_location ORDER BY created_at ASC";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (DateTime.TryParse(reader.GetString(0), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var startUtc))
                {
                    string? worldName = reader.IsDBNull(2) ? null : reader.GetString(2);
                    _visits.Add((startUtc, reader.GetString(1), worldName));
                }
            }
        }

        _avatarSwitches = [];
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT created_at, avatar_id FROM "usr{LocalAccountIdNoPrefix}_avatar_history"
                ORDER BY created_at ASC
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (DateTime.TryParse(reader.GetString(0), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var startUtc))
                {
                    _avatarSwitches.Add((startUtc, reader.GetString(1)));
                }
            }
        }

        _avatarNamesById = new Dictionary<string, string>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name FROM cache_avatar";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1)) _avatarNamesById[reader.GetString(0)] = reader.GetString(1);
            }
        }
    }

    /// <summary>Mirrors VrcxProfileLookupService.TryCreate - a missing VRCX install degrades to
    /// "unavailable", never a startup crash.</summary>
    public static GamelogCorrelationService? TryCreate(out string? error)
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCX", "VRCX.sqlite3");
        if (!File.Exists(path))
        {
            error = $"VRCX database not found at {path}";
            return null;
        }
        error = null;
        return new GamelogCorrelationService(path);
    }

    public void Dispose() => _conn.Dispose();

    private static readonly Regex DateFirstPattern = new(
        @"VRChat_(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2})_(?<h>\d{2})-(?<mi>\d{2})-(?<s>\d{2})\.(?<ms>\d{3})_",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ResFirstPattern = new(
        @"VRChat_\d+x\d+_(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2})_(?<h>\d{2})-(?<mi>\d{2})-(?<s>\d{2})\.(?<ms>\d{3})\.",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses the capture time VRChat itself embedded in the filename - not file mtime, which
    /// can shift on copy/backup (same precedent as PhotoRepository's VRCDN-upload name
    /// matching). The filename encodes local wall-clock time, not UTC - callers get a
    /// DateTimeKind.Local value back, matching FindPresentPlayers' expectation.
    /// </summary>
    public static DateTime? TryParseCaptureTime(string localPath)
    {
        string fileName = Path.GetFileName(localPath);
        var m = DateFirstPattern.Match(fileName);
        m = m.Success ? m : ResFirstPattern.Match(fileName);
        if (!m.Success) return null;

        try
        {
            return new DateTime(
                int.Parse(m.Groups["y"].Value), int.Parse(m.Groups["mo"].Value), int.Parse(m.Groups["d"].Value),
                int.Parse(m.Groups["h"].Value), int.Parse(m.Groups["mi"].Value), int.Parse(m.Groups["s"].Value),
                int.Parse(m.Groups["ms"].Value), DateTimeKind.Local);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // malformed/impossible date component - treat like no match
        }
    }

    /// <summary>
    /// Who was present in-instance at the given capture time, or null if no recorded visit
    /// brackets it (gamelog doesn't cover that time - VRCX was closed, a gap in the log, etc.)
    /// - deliberately no nearest-match guessing. Bracketing window is [this visit's start, the
    /// next visit's start) - no explicit "left" event is needed for the account's own presence.
    /// </summary>
    public List<(string UserId, string DisplayName)>? FindPresentPlayers(DateTime localCaptureTime)
    {
        DateTime captureTimeUtc = DateTime.SpecifyKind(localCaptureTime, DateTimeKind.Local).ToUniversalTime();

        var visitsForBracket = _visits.Select(v => (v.StartUtc, v.Location)).ToList();
        int bracketIndex = FindBracketIndex(visitsForBracket, captureTimeUtc);
        if (bracketIndex < 0) return null;

        var (windowStartUtc, location, _) = _visits[bracketIndex];

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT type, display_name, user_id FROM gamelog_join_leave
            WHERE location = @location AND created_at >= @windowStart AND created_at <= @captureTime
            ORDER BY created_at ASC
            """;
        cmd.Parameters.AddWithValue("@location", location);
        cmd.Parameters.AddWithValue("@windowStart", windowStartUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@captureTime", captureTimeUtc.ToString("o"));

        // A dictionary keyed by user id doubles as the "currently present" set - OnPlayerLeft
        // removes, OnPlayerJoined (re-)adds, so replaying events in order up to captureTime
        // leaves exactly who was there at that moment.
        var present = new Dictionary<string, string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string type = reader.GetString(0);
            string displayName = reader.GetString(1);
            string userId = reader.GetString(2);
            if (type == "OnPlayerJoined") present[userId] = displayName;
            else if (type == "OnPlayerLeft") present.Remove(userId);
        }
        return present.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Index of the last entry whose StartUtc is at-or-before captureTimeUtc, or -1
    /// if none brackets it (list is empty, or captureTimeUtc predates every entry) - shared by
    /// every "what was true at this moment" lookup in this class (player presence, world name,
    /// worn avatar), all keyed off a sorted-by-StartUtc list loaded once in the constructor.</summary>
    private static int FindBracketIndex<T>(List<(DateTime StartUtc, T Value)> sorted, DateTime captureTimeUtc)
    {
        int bracketIndex = -1;
        for (int i = 0; i < sorted.Count; i++)
        {
            if (sorted[i].StartUtc <= captureTimeUtc) bracketIndex = i;
            else break;
        }
        return bracketIndex;
    }

    /// <summary>World name recorded for whichever visit brackets this capture time, or null if
    /// no visit brackets it (same "no data, not a guess" convention as FindPresentPlayers) or
    /// the bracketed visit itself has no recorded world name (a normal gap in gamelog_location,
    /// not an error).</summary>
    public string? TryGetWorldName(DateTime localCaptureTime)
    {
        DateTime captureTimeUtc = DateTime.SpecifyKind(localCaptureTime, DateTimeKind.Local).ToUniversalTime();
        var visitsForBracket = _visits.Select(v => (v.StartUtc, v.WorldName)).ToList();
        int bracketIndex = FindBracketIndex(visitsForBracket, captureTimeUtc);
        return bracketIndex < 0 ? null : _visits[bracketIndex].WorldName;
    }

    /// <summary>The local account's avatar at the moment brackated by this capture time, or
    /// null if no switch brackets it. AvatarName is null if VRCX's own avatar cache doesn't
    /// have this id (a normal gap, not an error). WornUntilUtc is the next recorded switch's
    /// timestamp, or null if this is the most recent switch VRCX has ever recorded (i.e. still
    /// worn as of the last time VRCX observed a change) - gives the same upper-bound
    /// information the standalone vrc-photo-avatar skill reports, without needing separate
    /// boundary-uncertainty logic.</summary>
    public (string AvatarId, string? AvatarName, DateTime? WornUntilUtc)? TryGetWornAvatar(DateTime localCaptureTime)
    {
        DateTime captureTimeUtc = DateTime.SpecifyKind(localCaptureTime, DateTimeKind.Local).ToUniversalTime();
        int bracketIndex = FindBracketIndex(_avatarSwitches, captureTimeUtc);
        if (bracketIndex < 0) return null;

        string avatarId = _avatarSwitches[bracketIndex].AvatarId;
        string? avatarName = _avatarNamesById.TryGetValue(avatarId, out var name) ? name : null;
        DateTime? wornUntilUtc = bracketIndex + 1 < _avatarSwitches.Count
            ? _avatarSwitches[bracketIndex + 1].StartUtc
            : null;
        return (avatarId, avatarName, wornUntilUtc);
    }
}
