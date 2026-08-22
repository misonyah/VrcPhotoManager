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
    private readonly List<(DateTime StartUtc, string Location, string? WorldName, string? WorldId, long TimeMs)> _visits;

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
            // world_id: VRCX records this alongside world_name in gamelog_location (it's not
            // parsed out of the location string - VRCX already gives it as its own column), so
            // the gamelog fallback can link to a world page, not just show its name as plain
            // text (see MetadataWindow.VrcLink - it only renders a hyperlink when an id is
            // available).
            cmd.CommandText = "SELECT created_at, location, world_name, world_id, time FROM gamelog_location ORDER BY created_at ASC";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (DateTime.TryParse(reader.GetString(0), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var startUtc))
                {
                    string? worldName = reader.IsDBNull(2) || string.IsNullOrWhiteSpace(reader.GetString(2))
                        ? null
                        : reader.GetString(2);
                    string? worldId = reader.IsDBNull(3) || string.IsNullOrWhiteSpace(reader.GetString(3))
                        ? null
                        : reader.GetString(3);
                    _visits.Add((startUtc, reader.GetString(1), worldName, worldId, reader.GetInt64(4)));
                }
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

        int bracketIndex = FindBracketIndex(_visits, v => v.StartUtc, captureTimeUtc);
        if (bracketIndex < 0) return null;

        var (windowStartUtc, location, _, _, _) = _visits[bracketIndex];

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
    /// every "what was true at this moment" lookup in this class (player presence, world name),
    /// all keyed off a sorted-by-StartUtc list loaded once in the constructor.
    /// Takes the source list directly plus a selector rather than a pre-projected copy, so
    /// callers don't have to re-allocate a full copy of a potentially ~8000-row list on every
    /// call (this runs once per candidate photo in a batch library run).</summary>
    private static int FindBracketIndex<T>(List<T> sorted, Func<T, DateTime> startUtcSelector, DateTime captureTimeUtc)
    {
        int bracketIndex = -1;
        for (int i = 0; i < sorted.Count; i++)
        {
            if (startUtcSelector(sorted[i]) <= captureTimeUtc) bracketIndex = i;
            else break;
        }
        return bracketIndex;
    }

    /// <summary>
    /// Friends who plausibly traveled to this instance with you via the same portal/invite -
    /// both their departure from your PREVIOUS instance and their arrival into THIS one fall
    /// within <paramref name="window"/> of your own transition moment. A single timestamp
    /// stands in for both "you left the old instance" and "you joined this one" - no separate
    /// gap to account for, since a visit's own recorded duration (gamelog_location.time) lines
    /// up with the next visit's start to within about a second in practice (there's no
    /// mid-loading-screen idle VRCX logs separately). Deliberately requires BOTH ends to match,
    /// not just one - matching arrival alone would also flag someone who happened to arrive from
    /// somewhere else entirely at a similar time, not someone who actually traveled with you.
    /// The window is intentionally generous (not exact-timestamp matching): a real portal/invite
    /// hop isn't perfectly synchronized, and you might go through before or after everyone else
    /// in the group - see SettingsKeys.PortalHopWindowSeconds.
    /// Returns null (not empty) if the visit bracketing captureTime has no PRECEDING visit to
    /// compare against (e.g. the very first visit VRCX ever recorded) - there's no "old instance"
    /// to have traveled from, so the question doesn't apply, as opposed to a real answer of zero
    /// people.
    /// </summary>
    public List<(string UserId, string DisplayName)>? FindTraveledTogether(DateTime localCaptureTime, TimeSpan window)
    {
        DateTime captureTimeUtc = DateTime.SpecifyKind(localCaptureTime, DateTimeKind.Local).ToUniversalTime();
        int bracketIndex = FindBracketIndex(_visits, v => v.StartUtc, captureTimeUtc);
        if (bracketIndex <= 0) return null;

        DateTime transitionUtc = _visits[bracketIndex].StartUtc;
        string newLocation = _visits[bracketIndex].Location;
        string oldLocation = _visits[bracketIndex - 1].Location;
        string windowStart = (transitionUtc - window).ToString("o");
        string windowEnd = (transitionUtc + window).ToString("o");

        var leftOldAround = new Dictionary<string, string>();
        using (var leftCmd = _conn.CreateCommand())
        {
            leftCmd.CommandText = """
                SELECT DISTINCT user_id, display_name FROM gamelog_join_leave
                WHERE location = @location AND type = 'OnPlayerLeft'
                    AND created_at >= @start AND created_at <= @end
                """;
            leftCmd.Parameters.AddWithValue("@location", oldLocation);
            leftCmd.Parameters.AddWithValue("@start", windowStart);
            leftCmd.Parameters.AddWithValue("@end", windowEnd);
            using var reader = leftCmd.ExecuteReader();
            while (reader.Read()) leftOldAround[reader.GetString(0)] = reader.GetString(1);
        }
        if (leftOldAround.Count == 0) return [];

        var result = new List<(string, string)>();
        using (var joinedCmd = _conn.CreateCommand())
        {
            joinedCmd.CommandText = """
                SELECT DISTINCT user_id, display_name FROM gamelog_join_leave
                WHERE location = @location AND type = 'OnPlayerJoined'
                    AND created_at >= @start AND created_at <= @end
                """;
            joinedCmd.Parameters.AddWithValue("@location", newLocation);
            joinedCmd.Parameters.AddWithValue("@start", windowStart);
            joinedCmd.Parameters.AddWithValue("@end", windowEnd);
            using var reader = joinedCmd.ExecuteReader();
            while (reader.Read())
            {
                string userId = reader.GetString(0);
                if (leftOldAround.TryGetValue(userId, out string? displayName))
                {
                    result.Add((userId, displayName));
                }
            }
        }
        return result;
    }

    /// <summary>World name and id recorded for whichever visit brackets this capture time, or
    /// null if no visit brackets it (same "no data, not a guess" convention as
    /// FindPresentPlayers), the bracketed visit itself has no recorded world name (a normal gap
    /// in gamelog_location, not an error), or the capture time falls after that visit's own
    /// recorded duration (StartUtc + TimeMs) - i.e. the account had already left by then, so
    /// attributing that world would be a stale guess, not a fact. WorldId can still be null even
    /// when WorldName isn't - older gamelog rows recorded before VRCX itself tracked world_id.</summary>
    public (string WorldName, string? WorldId)? TryGetWorld(DateTime localCaptureTime)
    {
        DateTime captureTimeUtc = DateTime.SpecifyKind(localCaptureTime, DateTimeKind.Local).ToUniversalTime();
        int bracketIndex = FindBracketIndex(_visits, v => v.StartUtc, captureTimeUtc);
        if (bracketIndex < 0) return null;

        var visit = _visits[bracketIndex];
        DateTime visitEndUtc = visit.StartUtc.AddMilliseconds(visit.TimeMs);
        if (captureTimeUtc > visitEndUtc || visit.WorldName is null) return null;
        return (visit.WorldName, visit.WorldId);
    }
}
