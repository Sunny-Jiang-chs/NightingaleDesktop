namespace Nightingale.Core;

/// <summary>Sentence navigation and directional seek snapping, ported from the Android player.</summary>
public sealed class SubtitleTimeline
{
    private readonly double[] _starts;

    public SubtitleTimeline(IEnumerable<SubtitleCue> cues, double duration = double.PositiveInfinity)
        : this(cues.Select(cue => cue.Start), duration) { }

    public SubtitleTimeline(IEnumerable<double> cueStarts, double duration = double.PositiveInfinity)
    {
        _starts = cueStarts.Where(value => double.IsFinite(value) && value >= 0 && value <= duration)
            .Distinct().Order().ToArray();
    }

    public bool IsAvailable => _starts.Length > 0;

    public double? FindPrevious(double position)
    {
        if (!double.IsFinite(position)) return null;
        var index = LowerBound(position, 0, _starts.Length) - 1;
        return index >= 0 ? _starts[index] : null;
    }

    public double? FindNext(double position)
    {
        if (!double.IsFinite(position)) return null;
        var index = UpperBound(position);
        return index < _starts.Length ? _starts[index] : null;
    }

    public double? Snap(double target, double origin, int direction)
    {
        if (!IsAvailable || !double.IsFinite(target) || !double.IsFinite(origin)) return null;
        target = Math.Max(0, target);
        origin = Math.Max(0, origin);
        var from = direction > 0 ? UpperBound(origin) : 0;
        var until = direction < 0 ? LowerBound(origin, 0, _starts.Length) : _starts.Length;
        if (from >= until) return null;
        var insertion = LowerBound(target, from, until);
        if (insertion < until && _starts[insertion] == target) return _starts[insertion];
        double? before = insertion - 1 >= from ? _starts[insertion - 1] : null;
        double? after = insertion < until ? _starts[insertion] : null;
        if (before is null) return after;
        if (after is null) return before;
        var beforeDistance = target - before.Value;
        var afterDistance = after.Value - target;
        if (Math.Abs(beforeDistance - afterDistance) < 0.000001) return direction < 0 ? before : after;
        return beforeDistance < afterDistance ? before : after;
    }

    private int UpperBound(double value)
    {
        var low = 0;
        var high = _starts.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_starts[middle] <= value) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private int LowerBound(double value, int low, int high)
    {
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_starts[middle] < value) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
