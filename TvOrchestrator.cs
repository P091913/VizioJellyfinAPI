using System.Text;
using Microsoft.Extensions.Options;

namespace TvRemote;

public sealed class TvException(string message) : Exception(message);

/// <param name="Status">playing | ok | would_play | ambiguous | not_found | not_playing | unknown_command | error</param>
/// <param name="Message">A sentence a voice assistant can read out as-is.</param>
public sealed record CommandResult(string Status, string Message, object? Data = null);

/// <summary>Wakes the TV, opens Jellyfin, finds the TV's session, and plays things on it.</summary>
public sealed class TvOrchestrator(
    VizioClient tv,
    JellyfinClient jf,
    IOptions<TvOptions> options,
    ILogger<TvOrchestrator> log)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly TvOptions _opt = options.Value;

    // ---------- Commands ----------

    public async Task<CommandResult> HandleTextAsync(string? text, bool dryRun, CancellationToken ct)
    {
        var cmd = CommandParser.Parse(text);
        if (dryRun && cmd.Intent is not (Intent.Play or Intent.Unknown))
            return new("would_do", $"Understood: {cmd.Intent}.", new { intent = cmd.Intent.ToString() });

        return cmd.Intent switch
        {
            Intent.Play => await PlayAsync(cmd, dryRun, ct),
            Intent.Pause => await PlaystateAsync("Pause", "Paused.", ct),
            Intent.Resume => await PlaystateAsync("Unpause", "Resuming.", ct),
            Intent.Stop => await PlaystateAsync("Stop", "Stopped.", ct),
            Intent.Launch => await LaunchAsync(ct),
            Intent.PowerOff => await PowerOffAsync(ct),
            _ => new("unknown_command",
                $"I didn't understand \"{text}\". Try \"play <title>\", \"pause\", \"resume\", \"stop\", or \"turn off the TV\"."),
        };
    }

    public async Task<CommandResult> PlayAsync(ParsedCommand cmd, bool dryRun, CancellationToken ct)
    {
        return await Guard(async () =>
        {
            if (cmd.Intent != Intent.Play || string.IsNullOrWhiteSpace(cmd.Title))
                return new CommandResult("unknown_command", "Tell me what to play.");

            // 1. Search (before waking the TV, so a typo doesn't turn it on)
            var results = new List<JfItem>();
            foreach (var term in TitleMatcher.SearchVariants(cmd))
            {
                results.AddRange(await jf.SearchAsync(term, ct));
                if (results.Any(r => TitleMatcher.Score(cmd, r) >= 98)) break;
            }

            // Last resort: a short prefix catches "spiderman" -> "Spider-Man"; the scorer filters the noise.
            if (results.Count == 0 && TitleMatcher.PrefixVariant(cmd) is { } prefix)
                results.AddRange(await jf.SearchAsync(prefix, ct));

            var match = TitleMatcher.Pick(cmd, results);
            var candidates = match.Candidates.Select(c => new { c.Id, c.Name, c.Type, c.ProductionYear, c.Label }).ToList();

            if (match.Kind == MatchKind.NotFound)
            {
                var msg = candidates.Count == 0
                    ? $"I couldn't find \"{cmd.Title}\" in your library."
                    : $"I couldn't find \"{cmd.Title}\". Did you mean {JoinOr(match.Candidates.Select(c => c.Label))}?";
                return new CommandResult("not_found", msg, new { candidates });
            }

            if (match.Kind == MatchKind.Ambiguous)
                return new CommandResult("ambiguous",
                    $"Which one: {JoinOr(match.Candidates.Select(c => c.Label))}?", new { candidates });

            var item = match.Item!;
            if (dryRun)
                return new CommandResult("would_play", $"Would play {item.Label}.", new { item });

            // 2. Make sure the TV is on, showing Jellyfin, with a live session
            var session = await EnsureReadyAsync(ct);
            if (string.IsNullOrEmpty(session.UserId))
                throw new TvException("Jellyfin is open on the TV but nobody is logged in.");

            // 3. Series -> next episode; anything -> resume position
            var target = item;
            if (item.Type == "Series")
            {
                target = await jf.GetNextUpAsync(item.Id, session.UserId, ct)
                         ?? await jf.GetFirstEpisodeAsync(item.Id, session.UserId, ct)
                         ?? throw new TvException($"{item.Name} has no episodes I can play.");
            }

            var withState = await jf.GetItemAsync(target.Id, session.UserId, ct) ?? target;
            long? resumeTicks = withState.UserData is { Played: false, PlaybackPositionTicks: > 0 } ud
                ? ud.PlaybackPositionTicks
                : null;

            await jf.PlayAsync(session.Id, target.Id, resumeTicks, ct);

            var spoken = $"Playing {withState.Label}";
            if (resumeTicks is long t) spoken += $", resuming {SpokenDuration(TimeSpan.FromTicks(t))} in";
            log.LogInformation("{Message} (session {Session})", spoken, session.Id);
            return new CommandResult("playing", spoken + ".", new { item = withState, sessionId = session.Id, resumeTicks });
        });
    }

    public Task<CommandResult> PlaystateAsync(string command, string message, CancellationToken ct) =>
        Guard(async () =>
        {
            var session = await FindTvSessionAsync(ct);
            if (session is null) return new CommandResult("not_playing", "Jellyfin isn't open on the TV.");
            await jf.PlaystateAsync(session.Id, command, ct);
            return new CommandResult("ok", message);
        });

    public Task<CommandResult> LaunchAsync(CancellationToken ct) =>
        Guard(async () =>
        {
            var session = await EnsureReadyAsync(ct);
            return new CommandResult("ok", "Jellyfin is up on the TV.", new { sessionId = session.Id });
        });

    public Task<CommandResult> PowerOffAsync(CancellationToken ct) =>
        Guard(async () =>
        {
            if (await tv.GetPowerOnAsync(ct) != true) return new CommandResult("ok", "The TV is already off.");
            return await tv.SendPowerKeyAsync(false, ct)
                ? new CommandResult("ok", "Turning the TV off.")
                : throw new TvException("The TV didn't accept the power-off command.");
        });

    public async Task<object> StatusAsync(CancellationToken ct)
    {
        var power = await tv.GetPowerOnAsync(ct);
        var showing = power == true ? await tv.IsShowingAsync(_opt.AppUrl, ct) : null;
        JfSession? session = null;
        string? jellyfinError = null;
        try { session = await FindTvSessionAsync(ct); }
        catch (HttpRequestException ex) { jellyfinError = ex.Message; }

        return new
        {
            tvPower = power switch { true => "on", false => "standby", null => "unreachable" },
            jellyfinShowing = showing,
            sessionId = session?.Id,
            loggedInAs = session?.UserName,
            nowPlaying = session?.NowPlayingItem?.Label,
            jellyfinError,
        };
    }

    // ---------- Readiness ----------

    /// <summary>Powers on the TV if needed, opens Jellyfin if needed, returns the TV's session.</summary>
    public async Task<JfSession> EnsureReadyAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            bool needLaunch;
            var power = await tv.GetPowerOnAsync(ct);
            if (power != true)
            {
                await PowerOnAsync(ct);
                needLaunch = true;
            }
            else
            {
                var showing = await tv.IsShowingAsync(_opt.AppUrl, ct);
                needLaunch = showing switch
                {
                    true => false,
                    false => true,
                    null => await FindTvSessionAsync(ct) is null,   // can't tell what's on screen
                };
            }

            if (!needLaunch && await FindTvSessionAsync(ct) is { } existing)
                return existing;

            var before = (await jf.GetSessionsAsync(ct)).Select(s => s.Id).ToHashSet();
            var since = DateTimeOffset.UtcNow.AddSeconds(-2);
            if (needLaunch) await LaunchWithRetryAsync(ct);

            // Wait for the TV's session. While freshly launched, ignore stale sessions for the first half.
            var start = DateTime.UtcNow;
            var timeout = TimeSpan.FromSeconds(_opt.SessionTimeoutSeconds);
            while (DateTime.UtcNow - start < timeout)
            {
                var strict = needLaunch && DateTime.UtcNow - start < timeout / 2;
                var session = strict
                    ? await FindTvSessionAsync(ct, s => !before.Contains(s.Id) || s.LastActivityDate >= since)
                    : await FindTvSessionAsync(ct);

                if (session is not null)
                {
                    if (needLaunch) await Task.Delay(TimeSpan.FromSeconds(2), ct); // let its websocket settle
                    return session;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            throw new TvException("Jellyfin opened on the TV, but it never connected to the server. Is the TV logged in?");
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task PowerOnAsync(CancellationToken ct)
    {
        log.LogInformation("TV is off or unreachable; powering on");

        var deadline = DateTime.UtcNow.AddSeconds(_opt.PowerOnTimeoutSeconds);
        var nextKey = DateTime.UtcNow;
        while (await tv.GetPowerOnAsync(ct) != true)
        {
            if (DateTime.UtcNow > deadline)
                throw new TvException("The TV didn't turn on. Check that its Power Mode is set to Quick Start.");
            if (DateTime.UtcNow >= nextKey)
            {
                await tv.SendPowerKeyAsync(true, ct);
                nextKey = DateTime.UtcNow.AddSeconds(5);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        log.LogInformation("TV is on; waiting {Seconds}s for SmartCast", _opt.BootSettleSeconds);
        await Task.Delay(TimeSpan.FromSeconds(_opt.BootSettleSeconds), ct);
    }

    private async Task LaunchWithRetryAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (await tv.LaunchAsync(_opt.AppUrl, ct))
            {
                log.LogInformation("Launched {Url}", _opt.AppUrl);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(4), ct);
        }
        throw new TvException("The TV wouldn't open Jellyfin after 3 tries.");
    }

    private async Task<JfSession?> FindTvSessionAsync(CancellationToken ct, Func<JfSession, bool>? filter = null)
    {
        var sessions = await jf.GetSessionsAsync(ct);
        return sessions
            .Where(s => s.SupportsRemoteControl && IsTv(s))
            .Where(s => filter is null || filter(s))
            .OrderByDescending(s => s.LastActivityDate)
            .FirstOrDefault();
    }

    private bool IsTv(JfSession s)
    {
        if (!string.IsNullOrWhiteSpace(_opt.JellyfinDeviceId))
            return string.Equals(s.DeviceId, _opt.JellyfinDeviceId, StringComparison.Ordinal);

        var match = _opt.SessionMatch;
        return (s.DeviceName?.Contains(match, StringComparison.OrdinalIgnoreCase) ?? false)
               || DecodeDeviceId(s.DeviceId).Contains(match, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Jellyfin Web's DeviceId is base64 of the browser user agent.</summary>
    private static string DecodeDeviceId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        try
        {
            var s = id.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch (FormatException)
        {
            return "";
        }
    }

    // ---------- Helpers ----------

    private async Task<CommandResult> Guard(Func<Task<CommandResult>> action)
    {
        try
        {
            return await action();
        }
        catch (TvException ex)
        {
            log.LogWarning("{Message}", ex.Message);
            return new CommandResult("error", ex.Message);
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Jellyfin request failed");
            var hint = ex.StatusCode == System.Net.HttpStatusCode.Unauthorized ? " Check Jellyfin:ApiKey." : "";
            return new CommandResult("error", $"Jellyfin request failed ({ex.StatusCode?.ToString() ?? ex.Message}).{hint}");
        }
        catch (Exception ex)   // timeouts, bad JSON: still give the phone something to say
        {
            log.LogError(ex, "Command failed");
            return new CommandResult("error", "Something went wrong. Check the TvRemote log.");
        }
    }

    private static string JoinOr(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " or " + list[^1],
        };
    }

    private static string SpokenDuration(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours} hour{(t.Hours == 1 ? "" : "s")} {t.Minutes} minute{(t.Minutes == 1 ? "" : "s")}"
            : $"{Math.Max(1, (int)t.TotalMinutes)} minute{((int)t.TotalMinutes == 1 ? "" : "s")}";
}
