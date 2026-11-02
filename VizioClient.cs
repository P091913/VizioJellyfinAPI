using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace TvRemote;

/// <summary>Talks to the SmartCast REST API on the TV.</summary>
public sealed class VizioClient(HttpClient http, IOptions<TvOptions> options, ILogger<VizioClient> log)
{
    private readonly TvOptions _opt = options.Value;

    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.TryAddWithoutValidation("AUTH", _opt.AuthToken);
        if (body is not null)
        {
            // Default options on purpose: SmartCast wants the UPPERCASE property names as written.
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }
        return req;
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var res = await http.SendAsync(Request(method, path, body), cts.Token);
        var text = await res.Content.ReadAsStringAsync(cts.Token);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static bool IsSuccess(JsonNode? json) =>
        string.Equals(json?["STATUS"]?["RESULT"]?.GetValue<string>(), "SUCCESS", StringComparison.OrdinalIgnoreCase);

    /// <summary>true = on, false = standby, null = unreachable.</summary>
    public async Task<bool?> GetPowerOnAsync(CancellationToken ct)
    {
        try
        {
            var json = await SendAsync(HttpMethod.Get, "/state/device/power_mode", null, TimeSpan.FromSeconds(3), ct);
            return json?["ITEMS"]?[0]?["VALUE"]?.GetValue<int>() == 1;
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            return null;
        }
    }

    public async Task<bool> SendPowerKeyAsync(bool on, CancellationToken ct)
    {
        var body = new { KEYLIST = new[] { new { CODESET = 11, CODE = on ? 1 : 0, ACTION = "KEYPRESS" } } };
        try
        {
            return IsSuccess(await SendAsync(HttpMethod.Put, "/key_command/", body, TimeSpan.FromSeconds(4), ct));
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            return false;
        }
    }

    public async Task<bool> LaunchAsync(string url, CancellationToken ct)
    {
        var body = new { VALUE = new { MESSAGE = url, NAME_SPACE = 4, APP_ID = "3001" } };
        try
        {
            var json = await SendAsync(HttpMethod.Put, "/app/launch", body, TimeSpan.FromSeconds(6), ct);
            if (!IsSuccess(json)) log.LogWarning("Launch returned {Json}", json?.ToJsonString());
            return IsSuccess(json);
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            log.LogWarning("Launch failed: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>true = our page is showing, false = something else is, null = can't tell.</summary>
    public async Task<bool?> IsShowingAsync(string appUrl, CancellationToken ct)
    {
        try
        {
            var json = await SendAsync(HttpMethod.Get, "/app/current", null, TimeSpan.FromSeconds(3), ct);
            if (!IsSuccess(json)) return null;
            var authority = new Uri(appUrl).Authority;
            return json!.ToJsonString().Contains(authority, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            return null;
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested &&
        ex is HttpRequestException or OperationCanceledException or JsonException or IOException;
}
