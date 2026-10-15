using System.Diagnostics;
using System.Text;
using Nightingale.Core;

var root = Path.GetFullPath(AppContext.BaseDirectory);
while (!Directory.Exists(Path.Combine(root, "src", "Nightingale.Core")))
{
    var parent = Directory.GetParent(root);
    if (parent is null) { root = Directory.GetCurrentDirectory(); break; }
    root = parent.FullName;
}
var scratch = Path.Combine(root, "tmp", "core-tests", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(scratch);
var passed = 0;
var failed = 0;

Run("LRC offsets, repeated timestamps and final duration match Android", () =>
{
    var cues = SubtitleParser.ParseLrc(["[offset:-500]", "[00:01.2][00:03.45]First line", "[00:05.678]Second line"]);
    Equal(3, cues.Count);
    Equal(new SubtitleCue(.7, 2.95, "First line"), cues[0]);
    Equal(new SubtitleCue(2.95, 5.178, "First line"), cues[1]);
    Equal(new SubtitleCue(5.178, 10.178, "Second line"), cues[2]);
});
Run("LRC duplicate texts merge, negative offsets clamp, short cues retain 500 ms", () =>
{
    var cues = SubtitleParser.ParseLrc(["[offset:-2000]", "[00:01.00]first", "[00:01.50]first", "[00:02.00]second", "[00:02.20]third", "[00:60.00]bad"]);
    Equal(2, cues.Count);
    Equal(new SubtitleCue(0, .5, "first\nsecond"), cues[0]);
    Equal(new SubtitleCue(.2, 5.2, "third"), cues[1]);
});
Run("VTT metadata, cue IDs, settings, invalid timing and multiline text", () =>
{
    var cues = SubtitleParser.ParseTimedText([
        "WEBVTT", "", "NOTE discarded", "00:01.000 --> 00:02.000", "not a cue", "", "STYLE", "::cue { color: red }", "",
        "cue-one", "00:00:01.200 --> 00:00:03.000 align:start", "First cue", "second line", "",
        "00:05.000 --> 00:06.000", "Second cue", "", "01:00:00.500 --> 01:00:02.000", "Third cue", "",
        "00:60.000 --> 01:01.000", "bad seconds", "", "00:06.000 --> 00:05.000", "backwards", ""
    ]);
    Equal(3, cues.Count);
    Equal(new SubtitleCue(1.2, 3, "First cue\nsecond line"), cues[0]);
    Equal(3600.5, cues[2].Start);
});
Run("SRT Unicode paths and UTF-16 BOM parse without losing text", () =>
{
    var path = Path.Combine(scratch, "中文 字幕.srt");
    File.WriteAllText(path, "1\r\n00:00:01,000 --> 00:00:03,500\r\n夜雨\r\n第二行\r\n\r\n", Encoding.Unicode);
    Equal(new SubtitleCue(1, 3.5, "夜雨\n第二行"), SubtitleParser.Parse(path).Single());
    Equal(0, SubtitleParser.Parse(Path.Combine(scratch, "missing.srt")).Count);
});
Run("WebVTT serialization round trips cue times and text", () =>
{
    var cues = new[] { new SubtitleCue(1, 6, "hello"), new SubtitleCue(3600.5, 3601.001, "你好\n世界") };
    var text = SubtitleParser.ToWebVtt(cues);
    Check(text.StartsWith("WEBVTT\n\n00:00:01.000 --> 00:00:06.000\nhello\n\n"), text);
    Sequence(cues, SubtitleParser.ParseTimedText(text.Split('\n')));
});
Run("Directional subtitle snapping matches Android including ties and bounds", () =>
{
    var timeline = new SubtitleTimeline(new double[] { 9, 5, 1, 5, -1, 11, double.NaN }, 10);
    Equal<double?>(5, timeline.Snap(3, 2, 1));
    Equal<double?>(1, timeline.Snap(3, 4, -1));
    Equal<double?>(9, timeline.Snap(8.2, 4, 1));
    Equal<double?>(null, timeline.Snap(9.5, 9, 1));
    Equal<double?>(null, new SubtitleTimeline(Array.Empty<double>(), 6).Snap(1, 0, 1));
    Equal<double?>(1, timeline.FindPrevious(5));
    Equal<double?>(9, timeline.FindNext(5));
    Equal<double?>(null, timeline.FindPrevious(1));
    Equal<double?>(null, timeline.FindNext(9));
    Equal<double?>(null, timeline.Snap(double.NaN, 1, 1));
});
Run("Media scanning handles nesting, natural order, duplicate inputs and bad paths", () =>
{
    var folder = Path.Combine(scratch, "媒体 文件夹");
    Directory.CreateDirectory(Path.Combine(folder, "子目录"));
    foreach (var filename in new[] { "episode10.MP4", "episode2.mkv", "episode1.mp3", "episode9999999999999999999999.mp4", "readme.txt", "episode1.srt" })
        File.WriteAllText(Path.Combine(folder, filename), "");
    File.WriteAllText(Path.Combine(folder, "子目录", "sound.FLAC"), "");
    var files = MediaFiles.Enumerate([folder, Path.Combine(folder, "episode2.mkv"), Path.Combine(folder, "missing"), "\0"]);
    Equal(5, files.Count);
    Sequence(new[] { "episode1.mp3", "episode2.mkv", "episode10.MP4", "episode9999999999999999999999.mp4", "sound.FLAC" }, files.Select(Path.GetFileName));
    Check(MediaFiles.IsMedia("你好.OPUS"));
    Check(MediaFiles.IsSubtitle("video.ass"));
    Check(!MediaFiles.IsMedia("video.mp4.exe"));
});
Run("Directory junctions do not leave the selected media tree", () =>
{
    if (!OperatingSystem.IsWindows()) return;
    var folder = Path.Combine(scratch, "junction-input");
    var outside = Path.Combine(scratch, "junction-outside");
    Directory.CreateDirectory(folder);
    Directory.CreateDirectory(outside);
    File.WriteAllText(Path.Combine(outside, "outside.mp4"), "");
    var link = Path.Combine(folder, "linked");
    var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add("/c");
    info.ArgumentList.Add("mklink");
    info.ArgumentList.Add("/J");
    info.ArgumentList.Add(link);
    info.ArgumentList.Add(outside);
    using var process = Process.Start(info)!;
    process.WaitForExit();
    Check(process.ExitCode == 0, process.StandardError.ReadToEnd());
    Equal(0, MediaFiles.Enumerate([folder]).Count);
    Equal(0, MediaFiles.Enumerate([link]).Count);
});
Run("Subtitle discovery prioritizes exact names and respects stem boundaries", () =>
{
    var folder = Path.Combine(scratch, "subtitle-matching");
    Directory.CreateDirectory(folder);
    foreach (var filename in new[] { "movie.en.srt", "movie2.srt", "movie.mp4.vtt", "movie.SRT", "movie.zh-CN.ass", "movie.lrc", "movieposter.srt", "movie.mp4" })
        File.WriteAllText(Path.Combine(folder, filename), "");
    Sequence(new[] { "movie.lrc", "movie.SRT", "movie.mp4.vtt", "movie.en.srt", "movie.zh-CN.ass" }, MediaFiles.FindSubtitles(Path.Combine(folder, "movie.mp4")).Select(Path.GetFileName));
});
Run("Settings and playback history persist with Unicode, bounded positions and case deduplication", () =>
{
    var folder = Path.Combine(scratch, "state");
    var state = new StateStore(folder);
    Equal(75d, state.Settings.Volume);
    var media = Path.Combine(scratch, "中文 有空格.MP4");
    state.Settings.Volume = 42;
    state.Settings.SnapToSubtitles = true;
    state.Record(media, 50, 40);
    state.Record(media.ToUpperInvariant(), 12, 40);
    state.Save();
    Equal<string?>(null, state.LastSaveError);
    var loaded = new StateStore(folder);
    Equal(42d, loaded.Settings.Volume);
    Check(loaded.Settings.SnapToSubtitles);
    Equal(1, loaded.History.Count);
    Equal(12d, loaded.Find(media)!.Position);
    Equal(DateTimeKind.Utc, loaded.Find(media)!.LastPlayedUtc.Kind);
    Check(!File.Exists(Path.Combine(folder, "state.json.tmp")));
});
Run("Environment override isolates application state without touching user settings", () =>
{
    var previous = Environment.GetEnvironmentVariable("NIGHTINGALE_DATA_DIR");
    var folder = Path.Combine(scratch, "environment-state");
    try
    {
        Environment.SetEnvironmentVariable("NIGHTINGALE_DATA_DIR", folder);
        var state = new StateStore();
        Equal(folder, state.DataDirectory);
        state.Settings.Volume = 63;
        state.Save();
        Equal(63d, new StateStore().Settings.Volume);
        Equal(Path.Combine(scratch, "explicit-state"), new StateStore(Path.Combine(scratch, "explicit-state")).DataDirectory);
    }
    finally { Environment.SetEnvironmentVariable("NIGHTINGALE_DATA_DIR", previous); }
});
Run("Playlist ordering and history survive a new store while malformed and duplicate paths are ignored", () =>
{
    var folder = Path.Combine(scratch, "playlist-state");
    var first = Path.Combine(scratch, "第一集.mp4");
    var second = Path.Combine(scratch, "第二集.flac");
    var state = new StateStore(folder);
    state.RememberPlaylist([second, first, second.ToUpperInvariant(), "\0", "", Path.Combine(scratch, "note.txt")]);
    state.Record(first, 28, 300);
    Check(state.Save());
    var reopened = new StateStore(folder);
    Sequence(new[] { second, first }, reopened.Playlist);
    Equal(28d, reopened.Find(first)!.Position);
    reopened.RememberPlaylist([]);
    Check(reopened.Save());
    Equal(0, new StateStore(folder).Playlist.Count);
});
Run("Incognito persists as a setting while private playback never enters history or playlist files", () =>
{
    var folder = Path.Combine(scratch, "incognito-state");
    var publicMedia = Path.Combine(scratch, "remembered.mp4");
    var privateMedia = Path.Combine(scratch, "private-session-marker.mp4");
    var state = new StateStore(folder);
    state.Record(publicMedia, 23, 100);
    state.RememberPlaylist([publicMedia]);
    Check(state.Save());
    state.Settings.Incognito = true;
    state.Record(publicMedia, 77, 100);
    state.Record(privateMedia, 30, 100);
    state.RememberPlaylist([privateMedia]);
    Check(state.Save());
    Check(state.Save());
    Check(state.Find(publicMedia) is null, "Incognito must not offer automatic resume.");
    Equal(23d, state.History.Single().Position);
    var reopened = new StateStore(folder);
    Check(reopened.Settings.Incognito);
    Equal(1, reopened.History.Count);
    Sequence(new[] { publicMedia }, reopened.Playlist);
    foreach (var file in Directory.EnumerateFiles(folder))
        Check(!File.ReadAllText(file).Contains("private-session-marker", StringComparison.Ordinal), "Private path leaked to " + Path.GetFileName(file));
    reopened.Settings.Incognito = false;
    Equal(23d, reopened.Find(publicMedia)!.Position);
    reopened.Record(privateMedia, 40, 100);
    reopened.RememberPlaylist([privateMedia]);
    Check(reopened.Save());
    var normal = new StateStore(folder);
    Check(!normal.Settings.Incognito);
    Equal(2, normal.History.Count);
    Equal(40d, normal.Find(privateMedia)!.Position);
    Sequence(new[] { privateMedia }, normal.Playlist);
});
Run("Older state without playlist or privacy fields retains history and normal defaults", () =>
{
    var folder = Path.Combine(scratch, "old-state");
    Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "state.json"), "{\"Settings\":{\"Volume\":40},\"History\":[{\"Path\":\"C:\\\\older.mp4\",\"Position\":12,\"Duration\":60}]}");
    var state = new StateStore(folder);
    Check(!state.Settings.Incognito);
    Equal(0, state.Playlist.Count);
    Equal(1, state.History.Count);
    Equal(12d, state.History[0].Position);
});
Run("Atomic save retains backup and corrupted JSON recovers the previous good state", () =>
{
    var folder = Path.Combine(scratch, "state-corruption");
    var state = new StateStore(folder);
    state.Settings.Volume = 21;
    state.Save();
    state.Settings.Volume = 35;
    state.Save();
    Check(File.Exists(Path.Combine(folder, "state.json.bak")));
    Equal(35d, new StateStore(folder).Settings.Volume);
    File.WriteAllText(Path.Combine(folder, "state.json"), "{broken");
    var recovered = new StateStore(folder);
    Equal(21d, recovered.Settings.Volume);
    Check(recovered.LastLoadError is not null);
    File.WriteAllText(Path.Combine(folder, "state.json.bak"), "null");
    Equal(75d, new StateStore(folder).Settings.Volume);
});
Run("Saving a recovered store preserves the last healthy backup", () =>
{
    var folder = Path.Combine(scratch, "recovered-state");
    var media = Path.Combine(scratch, "recovery.mp4");
    var state = new StateStore(folder);
    state.Record(media, 21, 120);
    Check(state.Save());
    state.Record(media, 35, 120);
    Check(state.Save());
    File.WriteAllText(Path.Combine(folder, "state.json"), "{broken");
    var recovered = new StateStore(folder);
    Equal(21d, recovered.Find(media)!.Position);
    recovered.Record(media, 44, 120);
    Check(recovered.Save());
    Equal(44d, new StateStore(folder).Find(media)!.Position);
    File.WriteAllText(Path.Combine(folder, "state.json"), "{broken-again");
    Equal(21d, new StateStore(folder).Find(media)!.Position);
});
Run("An interrupted first atomic save recovers a complete temporary state", () =>
{
    var folder = Path.Combine(scratch, "interrupted-state");
    var media = Path.Combine(scratch, "interrupted.mp4");
    var state = new StateStore(folder);
    state.Record(media, 18, 120);
    state.RememberPlaylist([media]);
    Check(state.Save());
    File.Move(Path.Combine(folder, "state.json"), Path.Combine(folder, "state.json.tmp"));
    var recovered = new StateStore(folder);
    Equal(18d, recovered.Find(media)!.Position);
    Sequence(new[] { media }, recovered.Playlist);
    Check(recovered.Save());
    Equal(18d, new StateStore(folder).Find(media)!.Position);
});
Run("Invalid values, null history records, unwritable state location and 100-entry cap stay safe", () =>
{
    var folder = Path.Combine(scratch, "state-validation");
    Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "state.json"), "{\"Settings\":{\"Volume\":-2,\"Speed\":0,\"SubtitleFontSize\":200},\"History\":[null,{\"Path\":null},{\"Path\":\"C:\\\\media.mp4\",\"Position\":100,\"Duration\":20}]}");
    var state = new StateStore(folder);
    Equal(0d, state.Settings.Volume);
    Equal(.1, state.Settings.Speed);
    Equal(128d, state.Settings.SubtitleFontSize);
    Equal(20d, state.History.Single().Position);
    for (var index = 0; index < 110; index++) state.Record(Path.Combine(scratch, $"item{index}.mp4"), double.NaN, -2);
    Equal(100, state.History.Count);
    Equal(0d, state.History[0].Position);
    state.Save();
    Equal(100, new StateStore(folder).History.Count);
    var blocked = Path.Combine(scratch, "file-instead-of-directory");
    File.WriteAllText(blocked, "exists");
    var blockedStore = new StateStore(blocked);
    Check(!blockedStore.Save());
    Check(blockedStore.LastSaveError is not null);
});

Console.WriteLine($"\n{passed} passed, {failed} failed. Test fixtures retained at {scratch}");
return failed == 0 ? 0 : 1;

void Run(string name, Action test)
{
    try { test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}\n{error}"); }
}
static void Check(bool condition, string? details = null)
{
    if (!condition) throw new Exception(details ?? "Assertion failed.");
}
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected <{expected}>, actual <{actual}>.");
}
static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
{
    var expectedArray = expected.ToArray();
    var actualArray = actual.ToArray();
    if (!expectedArray.SequenceEqual(actualArray)) throw new Exception($"Expected [{string.Join(", ", expectedArray)}], actual [{string.Join(", ", actualArray)}].");
}
