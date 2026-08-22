using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OmniDock.Services;

namespace OmniDock;

/// <summary>
/// 设置面板。改动即时生效（回调交给主窗口应用），写盘做了防抖，
/// 免得拖滑块时每一帧都去碰文件。
/// </summary>
public partial class SettingsWindow : GlassWindow
{
    /// <summary>收起时留下的高度，差不多是标题栏那一条。</summary>
    private const double CollapsedHeight = 38d;

    private readonly AppSettings _settings;
    private readonly Action _apply;
    private readonly DispatcherTimer _saveDebounce;
    private bool _loading = true;
    private bool _collapsing;

    internal SettingsWindow(AppSettings settings, Action apply)
    {
        _settings = settings;
        _apply = apply;

        InitializeComponent();
        GlassTint = settings.GlassTint;

        // 先折叠着显示，布局完成后再展开，避免整窗先闪一下
        SizeToContent = SizeToContent.Manual;
        Height = CollapsedHeight;
        RootContent.Opacity = 0d;
        Loaded += (_, _) => AnimateExpand(RootContent, CollapsedHeight);

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            SettingsStore.Save(_settings);
        };

        BuildAccentDots();
        LoadFromSettings();
        _loading = false;
    }

    private void LoadFromSettings()
    {
        ScaleSlider.Value = _settings.Scale;
        WidthSlider.Value = _settings.ContentWidth;
        GlassSlider.Value = _settings.GlassOpacity;
        RefreshSlider.Value = _settings.RefreshSeconds;

        TopmostToggle.IsChecked = _settings.AlwaysOnTop;
        TickerToggle.IsChecked = _settings.UseLocalTicker;
        AlertsToggle.IsChecked = _settings.EnableAlerts;
        TrayToggle.IsChecked = _settings.ShowTrayIcon;
        CloseToTrayToggle.IsChecked = _settings.CloseToTray;

        // 自启的真实状态在注册表里，不在配置里
        AutoStartToggle.IsChecked = AutoStart.IsEnabled();

        foreach (var toggle in new[] { TopmostToggle, TickerToggle, AlertsToggle, AutoStartToggle, TrayToggle, CloseToTrayToggle })
        {
            toggle.Background = Theme.Accent;
        }

        UpdateLabels();
        SyncAccentSelection();
        SyncTrayDependency();
    }

    /// <summary>没有托盘图标时「关闭时收进托盘」不能开，否则窗口关了就找不回来。</summary>
    private void SyncTrayDependency()
    {
        var available = _settings.ShowTrayIcon;
        CloseToTrayToggle.IsEnabled = available;
        CloseToTrayLabel.Opacity = available ? 1d : 0.4d;
        CloseToTrayToggle.Opacity = available ? 1d : 0.4d;
    }

    private void UpdateLabels()
    {
        ScaleValue.Text = $"{_settings.Scale * 100d:0}%";
        WidthValue.Text = $"{_settings.ContentWidth:0} px";
        GlassValue.Text = $"{_settings.GlassOpacity * 100d / 128d:0}%";
        RefreshValue.Text = $"{_settings.RefreshSeconds:0.#} 秒";
    }

    private void BuildAccentDots()
    {
        foreach (var hex in AppSettings.AccentPresets)
        {
            var dot = new RadioButton
            {
                Style = (Style)FindResource("ColorDotStyle"),
                Background = Theme.Freeze(hex),
                GroupName = "Accent",
                Tag = hex,
                ToolTip = hex
            };

            dot.Checked += OnAccentChecked;
            AccentPanel.Children.Add(dot);
        }
    }

    private void SyncAccentSelection()
    {
        foreach (var child in AccentPanel.Children)
        {
            if (child is RadioButton dot)
            {
                dot.IsChecked = (string?)dot.Tag == _settings.AccentColor;
            }
        }
    }

    private void OnAccentChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string hex })
        {
            return;
        }

        _settings.AccentColor = hex;
        Theme.SetAccent(hex);
        TopmostToggle.Background = Theme.Accent;
        Commit();
    }

    private void OnScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _settings.Scale = e.NewValue;
        Commit();
    }

    private void OnWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _settings.ContentWidth = e.NewValue;
        Commit();
    }

    private void OnGlassChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _settings.GlassOpacity = (int)e.NewValue;

        // 设置窗口自己也跟着换厚度，改的效果当场就能看见
        GlassTint = _settings.GlassTint;
        ApplyGlass();
        Commit();
    }

    private void OnRefreshChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _settings.RefreshSeconds = e.NewValue;
        Commit();
    }

    private void OnTopmostChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.AlwaysOnTop = TopmostToggle.IsChecked == true;
        Commit();
    }

    private void OnTickerChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.UseLocalTicker = TickerToggle.IsChecked == true;
        Commit();
    }

    private void OnAlertsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.EnableAlerts = AlertsToggle.IsChecked == true;
        Commit();
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // 写注册表可能失败，用实际结果回填开关
        var applied = AutoStart.Set(AutoStartToggle.IsChecked == true);
        if (applied != (AutoStartToggle.IsChecked == true))
        {
            _loading = true;
            AutoStartToggle.IsChecked = applied;
            _loading = false;
        }
    }

    private void OnTrayChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.ShowTrayIcon = TrayToggle.IsChecked == true;

        // 关掉托盘图标时顺手关掉依赖它的选项
        if (!_settings.ShowTrayIcon && _settings.CloseToTray)
        {
            _settings.CloseToTray = false;
            _loading = true;
            CloseToTrayToggle.IsChecked = false;
            _loading = false;
        }

        SyncTrayDependency();
        Commit();
    }

    private void OnCloseToTrayChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.CloseToTray = CloseToTrayToggle.IsChecked == true;
        Commit();
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        _settings.Scale = defaults.Scale;
        _settings.ContentWidth = defaults.ContentWidth;
        _settings.GlassOpacity = defaults.GlassOpacity;
        _settings.AccentColor = defaults.AccentColor;
        _settings.RefreshSeconds = defaults.RefreshSeconds;
        _settings.AlwaysOnTop = defaults.AlwaysOnTop;
        _settings.UseLocalTicker = defaults.UseLocalTicker;
        _settings.EnableAlerts = defaults.EnableAlerts;
        _settings.ShowTrayIcon = defaults.ShowTrayIcon;
        _settings.CloseToTray = defaults.CloseToTray;

        Theme.SetAccent(_settings.AccentColor);
        _loading = true;
        LoadFromSettings();
        _loading = false;

        GlassTint = _settings.GlassTint;
        ApplyGlass();
        Commit();
    }

    /// <summary>刷新数值标签、让主窗口应用改动、延迟写盘。</summary>
    private void Commit()
    {
        _settings.Clamped();
        UpdateLabels();
        _apply();

        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseAnimated();

    /// <summary>Esc 也走收起动画。</summary>
    protected override void RequestClose() => CloseAnimated();

    /// <summary>收起后关闭。重复调用只认第一次。</summary>
    internal void CloseAnimated()
    {
        if (_collapsing)
        {
            return;
        }

        _collapsing = true;
        AnimateCollapse(RootContent, CollapsedHeight, Close);
    }

    protected override void OnClosed(EventArgs e)
    {
        // 还没落盘的改动在这里补上
        _saveDebounce.Stop();
        SettingsStore.Save(_settings);
        base.OnClosed(e);
    }
}
