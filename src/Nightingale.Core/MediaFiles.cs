namespace Nightingale.Core;

public static class MediaFiles
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".webm", ".avi", ".m4v", ".ts", ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus"
    };
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lrc", ".vtt", ".srt", ".ass", ".ssa"
    };

    public static bool IsMedia(string path) => !string.IsNullOrWhiteSpace(path) && MediaExtensions.Contains(Path.GetExtension(path));
    public static bool IsSubtitle(string path) => !string.IsNullOrWhiteSpace(path) && SubtitleExtensions.Contains(Path.GetExtension(path));

    public static IReadOnlyList<string> Enumerate(IEnumerable<string> inputs)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input)) continue;
            try { pending.Push(Path.GetFullPath(input)); }
            catch (Exception error) when (IsFileError(error)) { }
        }
        while (pending.TryPop(out var path))
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    if (IsMedia(path)) found.Add(path);
                    continue;
                }
                // Junctions and directory symlinks must not lead a scan outside its tree or into a loop.
                if ((attributes & FileAttributes.ReparsePoint) != 0 || !visited.Add(path)) continue;
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0
                })) pending.Push(entry);
            }
            catch (Exception error) when (IsFileError(error)) { }
        }
        return found.OrderBy(path => path, NaturalPathComparer.Instance).ToArray();
    }

    public static IReadOnlyList<string> FindSubtitles(string mediaPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(mediaPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (directory is null || !Directory.Exists(directory)) return [];
            var stem = Path.GetFileNameWithoutExtension(fullPath);
            var fullName = Path.GetFileName(fullPath);
            return Directory.EnumerateFiles(directory, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
                .Where(IsSubtitle)
                .Where(path => Path.GetFileNameWithoutExtension(path).Equals(stem, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileNameWithoutExtension(path).StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path =>
                {
                    var subtitleStem = Path.GetFileNameWithoutExtension(path);
                    return subtitleStem.Equals(stem, StringComparison.OrdinalIgnoreCase) ? 0
                        : subtitleStem.Equals(fullName, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                })
                .ThenBy(path => path, NaturalPathComparer.Instance).ToArray();
        }
        catch (Exception error) when (IsFileError(error)) { return []; }
    }

    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}

/// <summary>Ordinal Windows path ordering with numeric runs ordered by magnitude, without integer overflow.</summary>
public sealed class NaturalPathComparer : IComparer<string>
{
    public static NaturalPathComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        var a = 0;
        var b = 0;
        while (a < left.Length && b < right.Length)
        {
            if (char.IsAsciiDigit(left[a]) && char.IsAsciiDigit(right[b]))
            {
                var aEnd = a;
                var bEnd = b;
                while (aEnd < left.Length && char.IsAsciiDigit(left[aEnd])) aEnd++;
                while (bEnd < right.Length && char.IsAsciiDigit(right[bEnd])) bEnd++;
                var aSignificant = a;
                var bSignificant = b;
                while (aSignificant < aEnd - 1 && left[aSignificant] == '0') aSignificant++;
                while (bSignificant < bEnd - 1 && right[bSignificant] == '0') bSignificant++;
                var lengthComparison = (aEnd - aSignificant).CompareTo(bEnd - bSignificant);
                if (lengthComparison != 0) return lengthComparison;
                var digitsComparison = string.Compare(left, aSignificant, right, bSignificant, aEnd - aSignificant, StringComparison.Ordinal);
                if (digitsComparison != 0) return digitsComparison;
                a = aEnd;
                b = bEnd;
            }
            else
            {
                var comparison = char.ToUpperInvariant(left[a]).CompareTo(char.ToUpperInvariant(right[b]));
                if (comparison != 0) return comparison;
                a++;
                b++;
            }
        }
        var remaining = (left.Length - a).CompareTo(right.Length - b);
        return remaining != 0 ? remaining : StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }
}
