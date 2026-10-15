using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nightingale.Core;

public sealed record SubtitleCue(double Start, double End, string Text);

/// <summary>Subtitle times are seconds, matching mpv's time-pos and seek APIs.</summary>
public static partial class SubtitleParser
{
    [GeneratedRegex(@"\[(\d{1,3}:\d{2}(?::\d{2})?(?:\.\d{1,3})?)\]")]
    private static partial Regex LrcTimestampPattern();

    [GeneratedRegex(@"^\s*\[offset:([-+]?\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetPattern();

    [GeneratedRegex(@"^\s*(\S+)\s+-->\s+(\S+)(?:\s+.*)?$")]
    private static partial Regex TimingPattern();

    public static IReadOnlyList<SubtitleCue> Parse(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".lrc" => ParseLrc(lines),
                ".vtt" or ".srt" => ParseTimedText(lines),
                _ => []
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    public static IReadOnlyList<SubtitleCue> ParseLrc(IEnumerable<string> input)
    {
        var lines = input.ToArray();
        var offset = lines.Select(line => OffsetPattern().Match(line.TrimStart('\uFEFF')))
            .Where(match => match.Success)
            .Select(match => double.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value / 1000 : 0)
            .FirstOrDefault();
        var timedLines = new SortedDictionary<double, List<string>>();

        foreach (var line in lines)
        {
            var matches = LrcTimestampPattern().Matches(line);
            var text = LrcTimestampPattern().Replace(line, "").Trim().TrimStart('\uFEFF');
            if (text.Length == 0) continue;
            foreach (Match match in matches)
            {
                if (!TryTimestamp(match.Groups[1].Value, requireFraction: false, out var start)) continue;
                start = Math.Round(Math.Max(0, start + offset), 3);
                if (!double.IsFinite(start)) continue;
                if (!timedLines.TryGetValue(start, out var texts)) timedLines[start] = texts = [];
                if (!texts.Contains(text, StringComparer.Ordinal)) texts.Add(text);
            }
        }

        var entries = timedLines.ToArray();
        return entries.Select((entry, index) => new SubtitleCue(entry.Key,
            Math.Max(entry.Key + .5, index + 1 < entries.Length ? entries[index + 1].Key : entry.Key + 5),
            string.Join('\n', entry.Value))).ToArray();
    }

    public static IReadOnlyList<SubtitleCue> ParseTimedText(IEnumerable<string> input)
    {
        var lines = input.ToArray();
        var cues = new List<SubtitleCue>();
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimStart('\uFEFF');
            if (line == "NOTE" || line.StartsWith("NOTE ", StringComparison.Ordinal) || line.StartsWith("NOTE\t", StringComparison.Ordinal)
                || line is "STYLE" or "REGION")
            {
                while (index + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[index + 1])) index++;
                continue;
            }
            var match = TimingPattern().Match(line);
            if (!match.Success) continue;
            var validStart = TryTimestamp(match.Groups[1].Value, true, out var start);
            var validEnd = TryTimestamp(match.Groups[2].Value, true, out var end);
            var valid = validStart && validEnd && end > start;
            var text = new List<string>();
            while (index + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[index + 1])
                && !TimingPattern().IsMatch(lines[index + 1])) text.Add(lines[++index]);
            if (valid && text.Count > 0) cues.Add(new SubtitleCue(start, end, string.Join('\n', text)));
        }
        return cues.OrderBy(cue => cue.Start).ThenBy(cue => cue.End).ToArray();
    }

    public static string ToWebVtt(IEnumerable<SubtitleCue> cues)
    {
        var builder = new StringBuilder("WEBVTT\n\n");
        foreach (var cue in cues)
        {
            if (!double.IsFinite(cue.Start) || !double.IsFinite(cue.End) || cue.End <= cue.Start) continue;
            builder.Append(FormatTimestamp(cue.Start)).Append(" --> ").Append(FormatTimestamp(cue.End)).Append('\n');
            builder.Append(cue.Text.Replace("\r\n", "\n").Replace('\r', '\n')).Append("\n\n");
        }
        return builder.ToString();
    }

    public static double? FindPrevious(IReadOnlyList<SubtitleCue> cues, double position) => new SubtitleTimeline(cues).FindPrevious(position);
    public static double? FindNext(IReadOnlyList<SubtitleCue> cues, double position) => new SubtitleTimeline(cues).FindNext(position);

    private static bool TryTimestamp(string value, bool requireFraction, out double seconds)
    {
        seconds = 0;
        var parts = value.Replace(',', '.').Split(':');
        if (parts.Length is < 2 or > 3) return false;
        var tail = parts[^1].Split('.');
        if (tail.Length > 2 || (requireFraction && tail.Length != 2)) return false;
        if (tail.Length == 2 && (tail[1].Length is < 1 or > 3 || !tail[1].All(char.IsAsciiDigit))) return false;
        if (!tail[0].All(char.IsAsciiDigit) || !int.TryParse(tail[0], out var wholeSeconds) || wholeSeconds is < 0 or > 59) return false;
        if (!parts[0].All(char.IsAsciiDigit) || !int.TryParse(parts[0], out var first) || first < 0) return false;
        var fraction = tail.Length == 2 ? int.Parse(tail[1].PadRight(3, '0'), CultureInfo.InvariantCulture) / 1000d : 0;
        if (parts.Length == 2) seconds = first * 60d + wholeSeconds + fraction;
        else
        {
            if (!parts[1].All(char.IsAsciiDigit) || !int.TryParse(parts[1], out var minutes) || minutes is < 0 or > 59) return false;
            seconds = first * 3600d + minutes * 60d + wholeSeconds + fraction;
        }
        return true;
    }

    private static string FormatTimestamp(double seconds)
    {
        var ms = (long)Math.Round(Math.Clamp(seconds, 0, long.MaxValue / 2000d) * 1000);
        return FormattableString.Invariant($"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}");
    }
}
