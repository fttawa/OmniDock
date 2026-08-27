using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniDock.Services;

/// <summary>某个整点上，各窗口的已用量。</summary>
internal sealed class HourSample
{
    [JsonPropertyName("t")]
    public DateTimeOffset At { get; set; }

    [JsonPropertyName("w")]
    public Dictionary<string, double> Used { get; set; } = [];
}

/// <summary>历史里读出来的长期节奏。</summary>
internal readonly record struct LongTermRate(double PerMinute, TimeSpan Span, double ActiveFraction);

/// <summary>
/// 按整点记录各窗口的已用量，攒出一份跨越多个昼夜的消耗史。
///
/// 存在的理由：窗口内平均要等窗口自己走过一天才含得上休息时间，可 7 天窗口
/// 每周都要重置一次，每次重置后又是一天没有预测。历史不随窗口重置清零，
/// 上一周攒下的节奏立刻就能用。
///
/// 只记整点，一天 24 个点、留两周也就几 KB。程序没开的时间段会留下空档，
/// 但那不影响平均速率——那段时间的消耗会体现在下一个点的增量里，分子分母
/// 同时包含它，算出来仍是这段时间的真实平均。
/// </summary>
internal sealed class UsageHistory
{
    /// <summary>留两周，够覆盖两个 7 天周期。</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    /// <summary>算长期速率时只看最近这么久。</summary>
    private static readonly TimeSpan Lookback = TimeSpan.FromDays(7);

    /// <summary>历史短于一个昼夜就还不算「长期」，跟窗口内平均是一个道理。</summary>
    private static readonly TimeSpan MinSpan = TimeSpan.FromHours(24);

    /// <summary>没跨小时时的落盘间隔，防止进程被杀掉丢掉当前这一小时。</summary>
    private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(5);

    /// <summary>一小时消耗低于这个数就当作没在用，用来估活跃时段占比。</summary>
    private const double ActiveHourThreshold = 1000d;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly List<HourSample> _samples = [];
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    private bool _dirty;

    internal UsageHistory() => Load();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmniDock",
        "usage-history.json");

    /// <summary>每次成功拉到数据时调用；同一小时内反复调用只保留最后一次。</summary>
    internal void Record(DateTimeOffset now, IReadOnlyList<UsageWindowDto> windows)
    {
        if (windows.Count == 0)
        {
            return;
        }

        var hour = FloorToHour(now);
        var values = new Dictionary<string, double>(windows.Count);
        foreach (var window in windows)
        {
            values[window.Name] = window.Used;
        }

        if (_samples.Count > 0 && _samples[^1].At == hour)
        {
            // 同一小时内以最新值为准：整点那一刻的真实读数只能这样逼近
            _samples[^1].Used = values;
            _dirty = true;
        }
        else
        {
            _samples.Add(new HourSample { At = hour, Used = values });
            _dirty = true;

            // 跨小时是新增数据点，立刻落盘，别等节流
            _lastSave = DateTimeOffset.MinValue;
        }

        Trim(now);

        if (_dirty && now - _lastSave >= SaveInterval)
        {
            Save();
            _lastSave = now;
        }
    }

    /// <summary>
    /// 最近一段历史里的平均速率。历史太短或没消耗返回 null。
    ///
    /// 这里刻意不做加权：长期节奏要的就是把忙的和闲的一起平均掉。
    /// </summary>
    internal LongTermRate? RateOf(string windowName, DateTimeOffset now)
    {
        var since = now - Lookback;
        var points = _samples
            .Where(sample => sample.At >= since && sample.Used.ContainsKey(windowName))
            .ToList();

        if (points.Count < 2)
        {
            return null;
        }

        var span = now - points[0].At;
        if (span < MinSpan)
        {
            return null;
        }

        var total = 0d;
        var activeHours = 0d;
        var coveredHours = 0d;

        for (var i = 1; i < points.Count; i++)
        {
            var previous = points[i - 1].Used[windowName];
            var current = points[i].Used[windowName];

            // used 变小说明窗口在这中间重置了，这一段的消耗就是重置后新攒的那些
            var delta = current >= previous ? current - previous : current;
            total += delta;

            // 程序没开时一个点能横跨好几小时，按它实际覆盖的时长记账
            var hours = (points[i].At - points[i - 1].At).TotalHours;
            coveredHours += hours;
            if (delta >= ActiveHourThreshold)
            {
                activeHours += hours;
            }
        }

        if (total <= 0d)
        {
            return null;
        }

        var fraction = coveredHours > 0d ? activeHours / coveredHours : 0d;
        return new LongTermRate(total / span.TotalMinutes, span, fraction);
    }

    /// <summary>退出前把没落盘的补上。</summary>
    internal void Flush()
    {
        if (_dirty)
        {
            Save();
        }
    }

    private void Trim(DateTimeOffset now)
    {
        var cutoff = now - Retention;
        var removed = _samples.RemoveAll(sample => sample.At < cutoff);
        if (removed > 0)
        {
            _dirty = true;
        }
    }

    /// <summary>UTC 整点，跨时区/夏令时都不会错位。</summary>
    private static DateTimeOffset FloorToHour(DateTimeOffset moment)
    {
        var utc = moment.UtcDateTime;
        return new DateTimeOffset(
            new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc));
    }

    private void Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<List<HourSample>>(File.ReadAllText(path));
            if (loaded is null)
            {
                return;
            }

            // 存档可能是手改过的，排好序再用，后面的差分全靠顺序
            _samples.AddRange(loaded.Where(sample => sample.Used.Count > 0).OrderBy(sample => sample.At));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // 历史只是让预测更早可用，读不出来就当没有
            _samples.Clear();
        }
    }

    private void Save()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_samples, Options));
            _dirty = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
