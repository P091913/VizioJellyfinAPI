using System.Net.Http.Json;
using System.Text.Json;

namespace TvRemote;

public sealed record JfUserData(long PlaybackPositionTicks, bool Played);

public sealed record JfItem(
    string Id,
    string Name,
    string Type,
    int? ProductionYear = null,
    string? SeriesName = null,
    int? ParentIndexNumber = null,
    int? IndexNumber = null,
    JfUserData? UserData = null)
{
    public string Label => Type switch
    {
        "Episode" => $"{SeriesName} S{ParentIndexNumber}E{IndexNumber} \"{Name}\"",
        "Series" => ProductionYear is int y ? $"{Name} ({y}, series)" : $"{Name} (series)",
        _ => ProductionYear is int y2 ? $"{Name} ({y2})" : Name,
    };
}

public sealed record JfSession(
    string Id,
    string? UserId,
    string? UserName,
    string? Client,
    string? DeviceName,
    string? DeviceId,
    bool SupportsRemoteControl,
    DateTimeOffset? LastActivityDate,
    JfItem? NowPlayingItem);

internal sealed record JfQueryResult(List<JfItem>? Items);

/// <summary>Talks to the Jellyfin server with an API key.</summary>
public sealed class JellyfinClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<JfItem>> SearchAsync(string term, CancellationToken ct)
    {
        var url = $"/Items?searchTerm={Uri.EscapeDataString(term)}" +
                  "&includeItemTypes=Movie,Series&recursive=true&limit=25&fields=ProductionYear";
        var res = await http.GetFromJsonAsync<JfQueryResult>(url, Json, ct);
        return res?.Items ?? [];
    }

    public async Task<IReadOnlyList<JfSession>> GetSessionsAsync(CancellationToken ct) =>
        await http.GetFromJsonAsync<List<JfSession>>("/Sessions", Json, ct) ?? [];

    /// <summary>Item with the user's watch state (for resume).</summary>
    public async Task<JfItem?> GetItemAsync(string itemId, string userId, CancellationToken ct)
    {
        try
        {
            return await http.GetFromJsonAsync<JfItem>($"/Items/{itemId}?userId={userId}", Json, ct);
        }
        catch (HttpRequestException)
        {
            // Older servers
            return await http.GetFromJsonAsync<JfItem>($"/Users/{userId}/Items/{itemId}", Json, ct);
        }
    }

    public async Task<JfItem?> GetNextUpAsync(string seriesId, string userId, CancellationToken ct)
    {
        var res = await http.GetFromJsonAsync<JfQueryResult>(
            $"/Shows/NextUp?seriesId={seriesId}&userId={userId}&limit=1&enableResumable=true", Json, ct);
        return res?.Items?.FirstOrDefault();
    }

    public async Task<JfItem?> GetFirstEpisodeAsync(string seriesId, string userId, CancellationToken ct)
    {
        var res = await http.GetFromJsonAsync<JfQueryResult>(
            $"/Shows/{seriesId}/Episodes?userId={userId}&limit=1", Json, ct);
        return res?.Items?.FirstOrDefault();
    }

    public async Task PlayAsync(string sessionId, string itemId, long? startTicks, CancellationToken ct)
    {
        var url = $"/Sessions/{sessionId}/Playing?playCommand=PlayNow&itemIds={itemId}";
        if (startTicks is > 0) url += $"&startPositionTicks={startTicks}";
        using var res = await http.PostAsync(url, null, ct);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>command: Pause, Unpause, Stop, PlayPause, NextTrack, PreviousTrack.</summary>
    public async Task PlaystateAsync(string sessionId, string command, CancellationToken ct)
    {
        using var res = await http.PostAsync($"/Sessions/{sessionId}/Playing/{command}", null, ct);
        res.EnsureSuccessStatusCode();
    }
}
