using System.Text.RegularExpressions;

namespace TvRemote;

public enum Intent { Play, Pause, Resume, Stop, Launch, PowerOff, Unknown }

/// <param name="Title">Title as spoken, e.g. "blade runner 2049".</param>
/// <param name="TitleWithoutYear">Title with a trailing year removed, if there was one.</param>
/// <param name="YearHint">Trailing year, which may be part of the title or a release year.</param>
public sealed record ParsedCommand(
    Intent Intent,
    string? Title = null,
    string? TitleWithoutYear = null,
    int? YearHint = null);

/// <summary>Turns "play scooby doo on the tv" into an intent + title. Deliberately simple regexes.</summary>
public static class CommandParser
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private const string Object = @"(?:\s+(?:it|that|this|playback|the\s+(?:movie|show|film|video|tv)))?";

    private static readonly Regex PowerOn = new(
        @"^(?:(?:turn|power|switch)\s+on|open|launch|start(?:\s+up)?)\s+(?:the\s+)?(?:tv|television|jellyfin)$" +
        @"|^(?:turn|power|switch)\s+(?:the\s+)?(?:tv|television)\s+on$", Opts);

    private static readonly Regex PowerOff = new(
        @"^(?:turn|shut|power|switch)\s+off\s+(?:the\s+)?(?:tv|television)$" +
        @"|^(?:turn|shut|power|switch)\s+(?:the\s+)?(?:tv|television)\s+off$", Opts);

    private static readonly Regex Pause = new(@"^pause" + Object + "$", Opts);
    private static readonly Regex Resume = new(
        @"^(?:resume|unpause|continue|play|(?:resume|continue|keep|start)\s+(?:playing|watching))" + Object + "$", Opts);
    private static readonly Regex Stop = new(@"^stop(?:\s+playing)?" + Object + "$", Opts);

    private static readonly Regex Play = new(
        @"^(?:play|watch|put\s+on|put|start(?:\s+playing|\s+watching)?|resume(?:\s+playing)?)\s+(?<title>.+)$", Opts);

    private static readonly Regex Suffix = new(@"\s+(?:on|to)\s+(?:the\s+)?(?:tv|television|vizio|living\s+room\s+tv)$", Opts);
    private static readonly Regex TitlePrefix = new(
        @"^(?:the\s+)?(?:next\s+episode\s+of\s+|movie\s+|show\s+|film\s+|series\s+)", Opts);
    private static readonly Regex FromYear = new(@"^(?<t>.+?)\s+from\s+(?<y>(?:19|20)\d{2})$", Opts);
    private static readonly Regex TrailingYear = new(@"^(?<t>.+?)\s+\(?(?<y>(?:19|20)\d{2})\)?$", Opts);

    public static ParsedCommand Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(Intent.Unknown);

        var t = Regex.Replace(text.Trim(), @"[\s.!?,]+$", "");
        t = Regex.Replace(t, @"^(?:please|can\s+you|could\s+you)\s+", "", Opts);
        t = Regex.Replace(t, @"\s+please$", "", Opts);
        t = Regex.Replace(t, @"\s+", " ");

        // Power phrases first: "turn on the tv" would otherwise lose "on the tv" to the suffix strip.
        if (PowerOn.IsMatch(t)) return new(Intent.Launch);
        if (PowerOff.IsMatch(t)) return new(Intent.PowerOff);

        t = Suffix.Replace(t, "");

        if (Pause.IsMatch(t)) return new(Intent.Pause);
        if (Stop.IsMatch(t)) return new(Intent.Stop);
        if (Resume.IsMatch(t)) return new(Intent.Resume);

        var m = Play.Match(t);
        return m.Success ? ForTitle(m.Groups["title"].Value) : new(Intent.Unknown);
    }

    /// <summary>Builds a Play command from a bare title (also used by POST /tv/play).</summary>
    public static ParsedCommand ForTitle(string rawTitle)
    {
        var title = TitlePrefix.Replace(rawTitle.Trim().Trim('"', '\''), "").Trim();
        if (title.Length == 0) return new(Intent.Unknown);

        var from = FromYear.Match(title);
        if (from.Success)
        {
            var t = from.Groups["t"].Value;
            return new(Intent.Play, t, t, int.Parse(from.Groups["y"].Value));
        }

        var trailing = TrailingYear.Match(title);
        return trailing.Success
            ? new(Intent.Play, title, trailing.Groups["t"].Value, int.Parse(trailing.Groups["y"].Value))
            : new(Intent.Play, title);
    }
}
