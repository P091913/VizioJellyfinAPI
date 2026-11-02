namespace TvRemote;

// Values that describe one house (addresses, token, keys) have no defaults on purpose.
// They come from environment variables such as Tv__Ip; see .env.example.

public sealed class TvOptions
{
    public string Ip { get; set; } = "";
    public int Port { get; set; } = 7345;

    /// <summary>Overrides https://{Ip}:{Port} — only needed for testing.</summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>Vizio SmartCast pairing token.</summary>
    public string? AuthToken { get; set; }

    /// <summary>The page the TV opens (through the tvshim proxy).</summary>
    public string AppUrl { get; set; } = "";

    public int PowerOnTimeoutSeconds { get; set; } = 45;
    public int BootSettleSeconds { get; set; } = 8;
    public int SessionTimeoutSeconds { get; set; } = 40;

    /// <summary>Exact Jellyfin DeviceId of the TV. Empty = auto-detect via SessionMatch.</summary>
    public string? JellyfinDeviceId { get; set; }

    /// <summary>Text found in the TV's decoded DeviceId / device name.</summary>
    public string SessionMatch { get; set; } = "VIZIO";
}

public sealed class JellyfinOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8096";
    public string ApiKey { get; set; } = "";
}

public sealed class ApiOptions
{
    /// <summary>If set, callers must send X-Api-Key (or ?key=). Empty = no auth.</summary>
    public string? Key { get; set; }
}
