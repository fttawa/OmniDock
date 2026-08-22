using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace OmniDock.Services;

/// <summary>用户可调的外观与行为设置，存在 %LOCALAPPDATA%\OmniDock\settings.json。</summary>
internal sealed class AppSettings
{
    /// <summary>界面整体缩放。</summary>
    public double Scale { get; set; } = 1.0d;

    /// <summary>内容宽度（缩放前的逻辑宽度）。</summary>
    public double ContentWidth { get; set; } = 296d;

    /// <summary>玻璃厚度，就是亚克力着色的 alpha：越大越不透。</summary>
    public int GlassOpacity { get; set; } = 0x18;

    /// <summary>强调色，用于状态灯和水位正常时的用量条。</summary>
    public string AccentColor { get; set; } = AccentPresets[0];

    /// <summary>自动刷新间隔（秒）。</summary>
    public int RefreshSeconds { get; set; } = 1;

    /// <summary>是否置顶。</summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>上次关闭时的窗口位置；为空表示还没记过，用默认的右上角。</summary>
    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    [JsonIgnore]
    internal static string[] AccentPresets { get; } =
    [
        "#6CD3A6", // 青绿
        "#6CB6E8", // 蓝
        "#A78BFA", // 紫
        "#E8A66C", // 橙
        "#E88AB8"  // 粉
    ];

    /// <summary>把玻璃厚度拼成 SetWindowCompositionAttribute 要的 0xAABBGGRR。</summary>
    internal uint GlassTint => ((uint)Math.Clamp(GlassOpacity, 0x04, 0x80) << 24) | 0x202028u;

    internal AppSettings Clamped()
    {
        Scale = Math.Clamp(Scale, 0.8d, 1.6d);
        ContentWidth = Math.Clamp(ContentWidth, 240d, 440d);
        GlassOpacity = Math.Clamp(GlassOpacity, 0x04, 0x80);
        RefreshSeconds = Math.Clamp(RefreshSeconds, 1, 60);
        if (string.IsNullOrWhiteSpace(AccentColor))
        {
            AccentColor = AccentPresets[0];
        }

        return this;
    }
}

/// <summary>设置的读写。任何一步失败都退回默认值，不让配置问题挡住启动。</summary>
internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmniDock",
        "settings.json");

    internal static AppSettings Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings()).Clamped();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    internal static void Save(AppSettings settings)
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 存不下就算了，不影响本次运行
        }
    }
}

/// <summary>当前生效的强调色。用量条和状态灯都从这里取。</summary>
internal static class Theme
{
    private static Brush _accent = Freeze(AppSettings.AccentPresets[0]);

    internal static Brush Accent => _accent;

    /// <summary>水位偏高时的警示色，语义固定，不跟着强调色走。</summary>
    internal static Brush Warning { get; } = Freeze("#E8C468");

    internal static Brush Critical { get; } = Freeze("#E8746C");

    internal static void SetAccent(string hex) => _accent = Freeze(hex);

    internal static Brush Freeze(string hex)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.White;
        }
    }
}
