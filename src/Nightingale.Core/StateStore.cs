using System.Text.Json;

namespace Nightingale.Core;

public sealed class AppSettings
{
    public double Volume { get; set; } = 75;
    public double Speed { get; set; } = 1;
    public double GainDb { get; set; }
    public bool Muted { get; set; }
    public bool SnapToSubtitles { get; set; }
    public bool Incognito { get; set; }
    public bool Repeat { get; set; }
    public bool KeepOnTop { get; set; }
    public double SubtitleFontSize { get; set; } = 32;
    public double SubtitleDelay { get; set; }
    public int SubtitleBottomMargin { get; set; } = 36;

    internal void Sanitize()
    {
        Volume = FiniteRange(Volume, 0, 100, 75);
        Speed = FiniteRange(Speed, .1, 16, 1);
        GainDb = FiniteRange(GainDb, -60, 30, 0);
        SubtitleFontSize = FiniteRange(SubtitleFontSize, 12, 128, 32);
        SubtitleDelay = FiniteRange(SubtitleDelay, -3600, 3600, 0);
        SubtitleBottomMargin = Math.Clamp(SubtitleBottomMargin, 0, 1000);
    }

    private static double FiniteRange(double value, double minimum, double maximum, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

public sealed class HistoryEntry
{
    public string Path { get; set; } = "";
    public double Position { get; set; }
    public double Duration { get; set; }
    public DateTime LastPlayedUtc { get; set; }
}

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    private readonly string _statePath;
    private bool _primaryStateValid;
    private List<string> _playlist;

    public StateStore(string? dataDirectory = null)
    {
        var environmentDirectory = Environment.GetEnvironmentVariable("NIGHTINGALE_DATA_DIR");
        DataDirectory = dataDirectory ?? (!string.IsNullOrWhiteSpace(environmentDirectory) ? environmentDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NightingalePlayer"));
        _statePath = Path.Combine(DataDirectory, "state.json");
        var state = Read(_statePath);
        _primaryStateValid = state is not null;
        state ??= Read(_statePath + ".bak") ?? Read(_statePath + ".tmp");
        Settings = state?.Settings ?? new AppSettings();
        Settings.Sanitize();
        History = NormalizeHistory(state?.History ?? []);
        _playlist = NormalizePlaylist(state?.Playlist ?? []);
    }

    public string DataDirectory { get; }
    public AppSettings Settings { get; }
    public List<HistoryEntry> History { get; }
    public IReadOnlyList<string> Playlist { get { lock (_gate) return _playlist.ToArray(); } }
    public string? LastSaveError { get; private set; }
    public string? LastLoadError { get; private set; }

    public HistoryEntry? Find(string path)
    {
        if (Settings.Incognito) return null;
        var normalized = NormalizePath(path);
        if (normalized is null) return null;
        lock (_gate) return History.FirstOrDefault(entry => entry.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public void Record(string path, double position, double duration)
    {
        if (Settings.Incognito) return;
        var normalized = NormalizePath(path);
        if (normalized is null) return;
        lock (_gate)
        {
            History.RemoveAll(entry => entry.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            duration = double.IsFinite(duration) ? Math.Max(0, duration) : 0;
            position = double.IsFinite(position) ? Math.Max(0, position) : 0;
            if (duration > 0) position = Math.Min(position, duration);
            History.Insert(0, new HistoryEntry { Path = normalized, Position = position, Duration = duration, LastPlayedUtc = DateTime.UtcNow });
            if (History.Count > 100) History.RemoveRange(100, History.Count - 100);
        }
    }

    public void RememberPlaylist(IEnumerable<string> paths)
    {
        if (Settings.Incognito) return;
        lock (_gate) _playlist = NormalizePlaylist(paths);
    }

    /// <summary>Writes in the same directory and atomically replaces the old state, retaining one backup.</summary>
    public bool Save()
    {
        lock (_gate)
        {
            try
            {
                Settings.Sanitize();
                var state = new SavedState { Settings = Settings, History = NormalizeHistory(History), Playlist = _playlist };
                Directory.CreateDirectory(DataDirectory);
                var temporary = _statePath + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, state, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                // A recovered backup must survive the next write: a corrupt primary
                // is not a useful backup and must not replace the last good state.
                if (File.Exists(_statePath)) File.Replace(temporary, _statePath, _primaryStateValid ? _statePath + ".bak" : null);
                else File.Move(temporary, _statePath);
                _primaryStateValid = true;
                LastSaveError = null;
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            {
                LastSaveError = error.Message;
                return false;
            }
        }
    }

    private static List<string> NormalizePlaylist(IEnumerable<string> paths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in paths)
        {
            var path = NormalizePath(input);
            if (path is not null && MediaFiles.IsMedia(path) && seen.Add(path)) result.Add(path);
        }
        return result;
    }

    private SavedState? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<SavedState>(stream, JsonOptions);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            LastLoadError = error.Message;
            return null;
        }
    }

    private static List<HistoryEntry> NormalizeHistory(IEnumerable<HistoryEntry> entries)
    {
        var result = new List<HistoryEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Where(entry => entry is not null).OrderByDescending(entry => entry.LastPlayedUtc))
        {
            var path = NormalizePath(entry.Path);
            if (path is null || !seen.Add(path)) continue;
            var duration = double.IsFinite(entry.Duration) ? Math.Max(0, entry.Duration) : 0;
            var position = double.IsFinite(entry.Position) ? Math.Max(0, entry.Position) : 0;
            if (duration > 0) position = Math.Min(position, duration);
            result.Add(new HistoryEntry { Path = path, Position = position, Duration = duration, LastPlayedUtc = entry.LastPlayedUtc });
            if (result.Count == 100) break;
        }
        return result;
    }

    private static string? NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    private sealed class SavedState
    {
        public AppSettings? Settings { get; set; }
        public List<HistoryEntry>? History { get; set; }
        public List<string>? Playlist { get; set; }
    }
}
