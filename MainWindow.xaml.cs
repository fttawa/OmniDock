using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OmniDock.Services;
using OmniDock.ViewModels;

namespace OmniDock;

public partial class MainWindow : GlassWindow
{
    /// <summary>连不上时退避的上限，免得每秒重跑一遍端点发现。</summary>
    private static readonly TimeSpan MaxRetryInterval = TimeSpan.FromSeconds(20);

    private readonly LimitsClient _client = new();
    private readonly ObservableCollection<UsageWindowViewModel> _windows = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatcherTimer _heartbeat;
    private readonly AppSettings _settings = SettingsStore.Load();

    private readonly DispatcherTimer _positionSave;

    private SettingsWindow? _settingsWindow;
    private TimeSpan _refreshInterval;
    private TimeSpan _retryInterval;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private bool _fetching;

    /// <summary>位置恢复完成前不记录移动，否则会把本次启动的默认位置覆盖掉存档。</summary>
    private bool _positionRestored;

    public MainWindow()
    {
        Theme.SetAccent(_settings.AccentColor);
        _refreshInterval = TimeSpan.FromSeconds(_settings.RefreshSeconds);
        _retryInterval = _refreshInterval;

        InitializeComponent();
        WindowsList.ItemsSource = _windows;

        // 一个 1 秒心跳管两件事：推进倒计时、按当前间隔拉数据
        _heartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _heartbeat.Tick += async (_, _) => await OnHeartbeatAsync();

        // 拖动过程中会连续触发位置变化，攒一下再写盘
        _positionSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _positionSave.Tick += (_, _) =>
        {
            _positionSave.Stop();
            SettingsStore.Save(_settings);
        };
    }

    protected override async void OnSourceInitialized(EventArgs e)
    {
        GlassTint = _settings.GlassTint;
        base.OnSourceInitialized(e); // 基类在这里贴玻璃

        ApplySettings();
        RestorePosition();

        RootBorder.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0d, 1d, new Duration(TimeSpan.FromMilliseconds(220))));

        _heartbeat.Start();
        await RefreshAsync();
    }

    /// <summary>回到上次关闭时的位置；位置不可用（换了分辨率/显示器）就退回右上角。</summary>
    private void RestorePosition()
    {
        if (_settings.WindowLeft is { } left && _settings.WindowTop is { } top && IsReachableOnScreen(left, top))
        {
            Left = left;
            Top = top;
        }
        else
        {
            MoveToTopRightCorner();
        }

        // 到这里为止的位置变化都是初始化产生的，之后的才是用户挪的
        _positionRestored = true;
    }

    /// <summary>要求窗口还留着一块能抓住拖动的区域在桌面范围内。</summary>
    private bool IsReachableOnScreen(double left, double top)
    {
        const double grabbable = 80d;
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        return left + grabbable < virtualRight
            && left + Width - grabbable > virtualLeft
            && top + 32d < virtualBottom
            && top + 32d > virtualTop;
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (!_positionRestored)
        {
            return;
        }

        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _positionSave.Stop();
        _positionSave.Start();

        // 设置面板开着就跟着主窗口走
        if (_settingsWindow is { IsLoaded: true } panel)
        {
            PlaceBesideMain(panel);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _heartbeat.Stop();
        _positionSave.Stop();
        _shutdown.Cancel();
        _shutdown.Dispose();
        _client.Dispose();

        // 关闭前把位置和设置一起落盘
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        SettingsStore.Save(_settings);
        base.OnClosed(e);
    }

    /// <summary>把设置落到界面上；设置面板每次改动都会回调这里。</summary>
    private void ApplySettings()
    {
        _settings.Clamped();

        // 贴着右上角，所以改宽度时让右边缘不动；位置还没恢复时无从锚定
        var anchorRight = _positionRestored ? Left + Width : double.NaN;

        RootBorder.Width = _settings.ContentWidth;
        RootScale.ScaleX = _settings.Scale;
        RootScale.ScaleY = _settings.Scale;
        Width = _settings.ContentWidth * _settings.Scale;

        if (!double.IsNaN(anchorRight))
        {
            Left = anchorRight - Width;
        }

        Topmost = _settings.AlwaysOnTop;

        _refreshInterval = TimeSpan.FromSeconds(_settings.RefreshSeconds);
        _retryInterval = _refreshInterval;

        GlassTint = _settings.GlassTint;
        if (IsSourceReady)
        {
            ApplyGlass();
        }

        Theme.SetAccent(_settings.AccentColor);
        StatusDot.Fill = Theme.Accent;
        foreach (var window in _windows)
        {
            window.RefreshAccent();
        }
    }

    private async Task OnHeartbeatAsync()
    {
        // 倒计时是本地推进的，不依赖请求成功
        foreach (var window in _windows)
        {
            window.RefreshCountdown();
        }

        if (DateTime.UtcNow - _lastAttemptUtc >= _retryInterval)
        {
            await RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        // 上一拍还没回来就跳过，请求不会堆叠
        if (_fetching || _shutdown.IsCancellationRequested)
        {
            return;
        }

        _fetching = true;
        _lastAttemptUtc = DateTime.UtcNow;
        try
        {
            var snapshot = await _client.FetchAsync(_shutdown.Token);
            if (snapshot?.Windows is null or { Count: 0 })
            {
                BackOff();
                SetStatus($"找不到本地代理 · {(int)_retryInterval.TotalSeconds}s 后重试", Theme.Critical);
                return;
            }

            _retryInterval = _refreshInterval;
            Merge(snapshot.Windows);
            SetStatus(DescribeState(snapshot), StateBrush(snapshot));
        }
        catch (OperationCanceledException)
        {
            // 窗口正在关闭
        }
        catch (Exception ex)
        {
            BackOff();
            SetStatus($"读取失败：{ex.GetType().Name} · {(int)_retryInterval.TotalSeconds}s 后重试", Theme.Critical);
        }
        finally
        {
            _fetching = false;
        }
    }

    /// <summary>连不上就逐步拉长间隔，成功后由调用方恢复成设置里的间隔。</summary>
    private void BackOff()
        => _retryInterval = TimeSpan.FromSeconds(
            Math.Min(_retryInterval.TotalSeconds * 2d, MaxRetryInterval.TotalSeconds));

    /// <summary>手动刷新：清掉退避，立刻重来一次。</summary>
    private async Task ForceRefreshAsync()
    {
        _retryInterval = _refreshInterval;
        _lastAttemptUtc = DateTime.MinValue;
        await RefreshAsync();
    }

    /// <summary>窗口构成没变就原地更新，避免每次刷新都重建界面导致闪动。</summary>
    private void Merge(IReadOnlyList<UsageWindowDto> incoming)
    {
        var sameShape = _windows.Count == incoming.Count
            && _windows.Zip(incoming).All(pair => pair.First.Name == pair.Second.Name);

        if (sameShape)
        {
            foreach (var (viewModel, dto) in _windows.Zip(incoming))
            {
                viewModel.Update(dto);
            }

            return;
        }

        _windows.Clear();
        foreach (var dto in incoming)
        {
            _windows.Add(new UsageWindowViewModel(dto));
        }
    }

    private static string DescribeState(LimitsDto snapshot)
    {
        var stamp = $"{DateTime.Now:HH:mm:ss} 更新";
        if (snapshot.Suspended)
        {
            return $"账号已暂停 · {stamp}";
        }

        if (snapshot.Unmetered)
        {
            return $"不计量账号 · {stamp}";
        }

        return snapshot.Degraded ? $"服务降级中 · {stamp}" : stamp;
    }

    private static Brush StateBrush(LimitsDto snapshot) => snapshot switch
    {
        { Suspended: true } => Theme.Critical,
        { Degraded: true } => Theme.Warning,
        _ => Theme.Accent
    };

    private void SetStatus(string text, Brush dot)
    {
        StatusText.Text = text;
        StatusDot.Fill = dot;
    }

    /// <summary>放到主屏工作区右上角，留出一点边距。</summary>
    private void MoveToTopRightCorner()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 24;
        Top = workArea.Top + 24;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        // 已经开着就收回去，按钮当开关用
        if (_settingsWindow is not null)
        {
            _settingsWindow.CloseAnimated();
            return;
        }

        var panel = new SettingsWindow(_settings, ApplySettings)
        {
            Owner = this,
            Topmost = true
        };

        panel.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = panel;

        // 必须在 Show 之前定位，否则会先在默认的 (0,0) 渲染一帧再跳过来
        PlaceBesideMain(panel);
        panel.Show();
    }

    /// <summary>优先放在主窗口左侧，左边放不下就放到正下方。</summary>
    private void PlaceBesideMain(Window panel)
    {
        const double gap = 8d;

        // 用 Width 而不是 ActualWidth：定位发生在显示之前，那时还没布局过
        var panelWidth = double.IsNaN(panel.Width) ? panel.ActualWidth : panel.Width;
        var workArea = SystemParameters.WorkArea;
        var left = Left - panelWidth - gap;

        if (left < workArea.Left)
        {
            panel.Left = Math.Min(Left, workArea.Right - panelWidth);
            panel.Top = Top + ActualHeight + gap;
            return;
        }

        panel.Left = left;
        panel.Top = Top;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await ForceRefreshAsync();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // Esc 关闭由基类处理
        switch (e.Key)
        {
            case Key.F5:
                await ForceRefreshAsync();
                break;
            case Key.F2:
                OnSettingsClick(this, new RoutedEventArgs());
                break;
        }
    }
}
