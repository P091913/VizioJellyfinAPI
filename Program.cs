using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using TvRemote;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.Configure<TvOptions>(builder.Configuration.GetSection("Tv"));
builder.Services.Configure<JellyfinOptions>(builder.Configuration.GetSection("Jellyfin"));
builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection("Api"));

// These come from environment variables (.env). Stop now with a clear message if one is missing,
// instead of failing later on the first voice command.
string[] required = ["Tv:Ip", "Tv:AuthToken", "Tv:AppUrl", "Jellyfin:ApiKey"];
var missing = required.Where(k => string.IsNullOrWhiteSpace(builder.Configuration[k])).Select(k => k.Replace(":", "__")).ToList();
if (missing.Count > 0)
    throw new InvalidOperationException($"Missing settings: {string.Join(", ", missing)}. Set them in .env.");

builder.Services.AddHttpClient<VizioClient>((sp, c) =>
    {
        var o = sp.GetRequiredService<IOptions<TvOptions>>().Value;
        c.BaseAddress = new Uri(o.ApiBaseUrl ?? $"https://{o.Ip}:{o.Port}");
        c.Timeout = TimeSpan.FromSeconds(10);
    })
    // The TV uses a self-signed certificate; this client only ever talks to the TV.
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

builder.Services.AddHttpClient<JellyfinClient>((sp, c) =>
{
    var o = sp.GetRequiredService<IOptions<JellyfinOptions>>().Value;
    c.BaseAddress = new Uri(o.BaseUrl);
    c.Timeout = TimeSpan.FromSeconds(10);
    c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("MediaBrowser", $"Token=\"{o.ApiKey}\"");
});

builder.Services.AddTransient<TvOrchestrator>();

var app = builder.Build();

// Optional shared-secret check
app.Use(async (ctx, next) =>
{
    var key = ctx.RequestServices.GetRequiredService<IOptions<ApiOptions>>().Value.Key;
    if (string.IsNullOrEmpty(key) || ctx.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }

    var supplied = ctx.Request.Headers["X-Api-Key"].FirstOrDefault() ?? ctx.Request.Query["key"].FirstOrDefault();
    if (supplied != key)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new CommandResult("unauthorized", "Missing or wrong API key."));
        return;
    }
    await next();
});

// Long-running TV operations shouldn't die if the caller (e.g. a phone) disconnects mid-wake.
var stopping = app.Lifetime.ApplicationStopping;

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/tv/status", async (TvOrchestrator tv) => Results.Ok(await tv.StatusAsync(stopping)));

app.MapPost("/tv/command", async (CommandRequest req, TvOrchestrator tv) =>
    ToHttp(await tv.HandleTextAsync(req.Text, req.DryRun, stopping)));

app.MapPost("/tv/play", async (PlayRequest req, TvOrchestrator tv) =>
    ToHttp(await tv.PlayAsync(CommandParser.ForTitle(req.Title ?? ""), req.DryRun, stopping)));

app.MapPost("/tv/launch", async (TvOrchestrator tv) => ToHttp(await tv.LaunchAsync(stopping)));
app.MapPost("/tv/off", async (TvOrchestrator tv) => ToHttp(await tv.PowerOffAsync(stopping)));
app.MapPost("/tv/pause", async (TvOrchestrator tv) => ToHttp(await tv.PlaystateAsync("Pause", "Paused.", stopping)));
app.MapPost("/tv/resume", async (TvOrchestrator tv) => ToHttp(await tv.PlaystateAsync("Unpause", "Resuming.", stopping)));
app.MapPost("/tv/stop", async (TvOrchestrator tv) => ToHttp(await tv.PlaystateAsync("Stop", "Stopped.", stopping)));

app.Run();

static IResult ToHttp(CommandResult r) => r.Status switch
{
    "not_found" => Results.NotFound(r),
    "unknown_command" => Results.BadRequest(r),
    "error" => Results.Json(r, statusCode: StatusCodes.Status502BadGateway),
    _ => Results.Ok(r),   // playing, ok, would_play, would_do, ambiguous, not_playing
};

public sealed record CommandRequest(string? Text, bool DryRun = false);
public sealed record PlayRequest(string? Title, bool DryRun = false);
