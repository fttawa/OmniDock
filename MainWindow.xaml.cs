using System.Collections.ObjectModel;
using System.IO;
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
    private readonly UsageHistory _history = new();
    private readonly ObservableCollection<UsageWindowViewModel> _windows = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatcherTimer _heartbeat;
    private readonly AppSettings _settings = SettingsStore.Load();

    private readonly DispatcherTimer _positionSave;
    private readonly TrayIcon _tray = new();

    private SettingsWindow? _settingsWindow;
    private FileSystemWatcher? _settingsWatcher;
    private bool _alerting;
    private bool _exiting;
    private TimeSpan _refreshInterval;
    private TimeSpan _retryInterval;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private bool _fetching;

    /// <summary>当前的故障说明（不含倒计时）；为 null 表示一切正常。</summary>
    private string? _errorNotice;

    /// <summary>故障是否需要用户处理：休眠会自动恢复，就用中性色不报警。</summary>
    private bool _errorActionable;

    /// <summary>位置恢复完成前不记录移动，否则会把本次启动的默认位置覆盖掉存档。</summary>
    private bool _positionRestored;

    public MainWindow()
    {
        Theme.SetAccent(_settings.AccentColor);
        _refreshInterval = TimeSpan.FromSeconds(_settings.RefreshSeconds);
        _retryInterval = _refreshInterval;

        AdoptCredentialsFromEnvironment();
        _client.StoredToken = _settings.AuthToken;
        _client.StoredBaseUrl = _settings.AuthBaseUrl;
        _client.UpstreamBaseUrl = _settings.UpstreamBaseUrl;

        InitializeComponent();
        WindowsList.ItemsSource = _windows;

        // 心跳管两件事：推进倒计时、按当前间隔拉数据。
        // 它的周期决定了刷新间隔的上限，所以要比最快的间隔更细（250ms 正好整除 0.5 秒）。
        _heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _heartbeat.Tick += async (_, _) => await OnHeartbeatAsync();

        // 拖动过程中会连续触发位置变化，攒一下再写盘
        _positionSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _positionSave.Tick += (_, _) =>
        {
            _positionSave.Stop();
            SettingsStore.Save(_settings);
        };

        _tray.ToggleRequested += ToggleVisibility;
        _tray.SettingsRequested += () => OnSettingsClick(this, new RoutedEventArgs());
        _tray.ExitRequested += ExitApplication;
        _tray.AutoStartChanged += _ => { }; // 托盘里改自启只影响注册表，无需落盘设置
        _tray.SyncAutoStart(AutoStart.IsEnabled());
    }

    /// <summary>
    /// 代理的入口（地址 + token）只注入会话环境、不落盘，每次代理重启还会换新。
    /// 从会话里启动时顺手存一份，这样之后双击或开机自启也能用。
    ///
    /// 地址比 token 更要紧：新版把凭据放进了 URL 路径，光靠反查端口拼不出来。
    /// </summary>
    private void AdoptCredentialsFromEnvironment()
    {
        var changed = false;

        if (Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN") is { Length: > 0 } token
            && token != _settings.AuthToken)
        {
            _settings.AuthToken = token;
            changed = true;
        }

        if (Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL") is { Length: > 0 } baseUrl
            && baseUrl != _settings.AuthBaseUrl
            && Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            _settings.AuthBaseUrl = baseUrl;
            changed = true;
        }

        if (changed)
        {
            SettingsStore.Save(_settings);
        }
    }

    /// <summary>
    /// 这次成功用的入口如果不是存档里那个（来自端点缓存或命令行扫描），记下来。
    /// 下次冷启动就能直接用，不必再扫一遍。
    /// </summary>
    private void PersistDiscoveredEntry()
    {
        if (_client.DiscoveredEntry is not { Length: > 0 } entry)
        {
            return;
        }

        _client.DiscoveredEntry = null;
        _settings.AuthBaseUrl = entry;
        _client.StoredBaseUrl = entry;
        SettingsStore.Save(_settings);
    }

    /// <summary>
    /// 会话 hook 会在代理换新入口后改写设置文件。盯着它，一有改动立刻重试——
    /// 否则失联时正卡在退避里，最长要等 20 秒才会去看一眼。
    /// </summary>
    private void WatchSettingsFile()
    {
        try
        {
            var path = SettingsStore.DirectoryPath;
            Directory.CreateDirectory(path);

            _settingsWatcher = new FileSystemWatcher(path, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _settingsWatcher.Changed += (_, _) => Dispatcher.BeginInvoke(async () =>
            {
                // 只在失联时才管：正常运行时这个文件是我们自己在写
                if (_errorNotice is null || !ReloadStoredCredentials())
                {
                    return;
                }

                _errorNotice = "入口已更新，重连中";
                _errorActionable = false;
                await ForceRefreshAsync();
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 监听不了就退回原来的行为：退避到点了自然会重读
        }
    }

    /// <summary>托盘点一下：藏起来的显示出来，显示中的藏起来。</summary>
    private void ToggleVisibility()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Show();
        Activate();
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    protected override async void OnSourceInitialized(EventArgs e)
    {
        GlassTint = _settings.GlassTint;
        base.OnSourceInitialized(e); // 基类在这里贴玻璃

        ApplySettings();
        RestorePosition();

        RootBorder.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0d, 1d, new Duration(TimeSpan.FromMilliseconds(220))));

        WatchSettingsFile();
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

    /// <summary>
    /// 尺寸变化不会触发 OnLocationChanged，但设置面板落在下方时需要跟着主窗口的高度走，
    /// 否则缩放一改就被压住或留出空档。放在左侧时算出来是同一个位置，不会动。
    /// </summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);

        if (_settingsWindow is { IsLoaded: true } panel)
        {
            PlaceBesideMain(panel);
        }
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

    /// <summary>
    /// 开了「关闭到托盘」就只是藏起来。必须同时有托盘图标，
    /// 否则窗口藏了又没入口，等于把自己锁在外面。
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting && _settings.CloseToTray && _settings.ShowTrayIcon)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _tray.Dispose();
        _settingsWatcher?.Dispose();
        _heartbeat.Stop();
        _positionSave.Stop();
        _shutdown.Cancel();
        _shutdown.Dispose();
        _client.Dispose();

        // 关闭前把位置和设置一起落盘
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        SettingsStore.Save(_settings);
        _history.Flush();
        base.OnClosed(e);
    }

    /// <summary>把设置落到界面上；设置面板每次改动都会回调这里。</summary>
    private void ApplySettings()
    {
        _settings.Clamped();

        RootBorder.Width = _settings.ContentWidth;
        RootScale.ScaleX = _settings.Scale;
        RootScale.ScaleY = _settings.Scale;
        Width = _settings.ContentWidth * _settings.Scale;

        // 左上角保持不动、向右下扩展：设置面板在左侧，若改成让右边缘不动，
        // 窗口就会往左长过去把面板顶开——用户正在拖的滑块自己在跑。
        // 只有右边缘顶出工作区时才把窗口拉回来。
        if (_positionRestored)
        {
            var workArea = SystemParameters.WorkArea;
            if (Left + Width > workArea.Right)
            {
                Left = Math.Max(workArea.Left, workArea.Right - Width);
            }
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

        _tray.Visible = _settings.ShowTrayIcon;
        _client.StoredToken = _settings.AuthToken;
        _client.StoredBaseUrl = _settings.AuthBaseUrl;
        _client.UpstreamBaseUrl = _settings.UpstreamBaseUrl;
    }

    /// <summary>
    /// 水位过高或预计撞墙时闪一下边框和状态灯。
    /// 只在「刚进入」这个状态时闪，否则每秒都会抖。
    /// </summary>
    private void EvaluateAlert()
    {
        var shouldAlert = _windows.Any(window => window.NeedsAttention);

        if (shouldAlert && !_alerting && _settings.EnableAlerts)
        {
            PulseAttention();
        }

        _alerting = shouldAlert;
    }

    private void PulseAttention()
    {
        var warn = ((SolidColorBrush)Theme.Critical).Color;

        RootBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
        {
            To = warn,
            Duration = new Duration(TimeSpan.FromMilliseconds(420)),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3d),
            EasingFunction = new SineEase()
        });

        StatusDot.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = 0.2d,
            Duration = new Duration(TimeSpan.FromMilliseconds(420)),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3d),
            EasingFunction = new SineEase()
        });
    }

    private async Task OnHeartbeatAsync()
    {
        // 关掉内置计时器后，倒计时不在这里推进，只由收到的数据驱动
        if (_settings.UseLocalTicker)
        {
            foreach (var window in _windows)
            {
                window.RefreshCountdown();
            }
        }

        // 故障状态下的重试倒计时也要每拍推进（正常时这个调用什么都不做）
        UpdateRetryCountdown();

        // 倒计时在走，同一个速率的撞墙结论会随时间翻转，所以每拍都要判一次
        EvaluateAlert();

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
            var result = await _client.FetchAsync(_shutdown.Token);
            if (result is not { Status: FetchStatus.Ok, Data: { Windows: { Count: > 0 } windows } snapshot })
            {
                ClassifyFailure(result.Status);
                BackOff();
                UpdateRetryCountdown();
                return;
            }

            _errorNotice = null;
            _retryInterval = _refreshInterval;
            PersistDiscoveredEntry();
            Merge(windows);

            // 先记账再取用：这一拍的数据也该算进长期节奏里
            var now = DateTimeOffset.Now;
            _history.Record(now, windows);
            foreach (var window in _windows)
            {
                window.SetHistory(_history.RateOf(window.Name, now));
            }

            SetStatus(NoticeFor(snapshot), $"{DateTime.Now:HH:mm:ss} 更新{SourceSuffix()}", StateBrush(snapshot));
            EvaluateAlert();
            UpdateTray();
        }
        catch (OperationCanceledException)
        {
            // 窗口正在关闭
        }
        catch (Exception ex)
        {
            _errorNotice = $"读取失败：{ex.GetType().Name}";
            BackOff();
            UpdateRetryCountdown();
        }
        finally
        {
            _fetching = false;
        }
    }

    /// <summary>
    /// 数据是从哪来的：直连上游（拿账号 token 打 <c>/v1/limits</c>）还是本地代理。
    /// 显示在刷新时间后面，一眼能看出走的是哪条路。
    /// </summary>
    private string SourceSuffix() => _client.LastSource switch
    {
        FetchSource.Upstream => " · 直连",
        FetchSource.Proxy => " · 代理",
        _ => string.Empty
    };

    /// <summary>
    /// 把失败原因分成两类：休眠会自动恢复，用中性色、说明会自动重连；
    /// token 失效或没开需要用户处理，用告警色。
    /// </summary>
    private void ClassifyFailure(FetchStatus status)
    {
        if (status == FetchStatus.Unauthorized)
        {
            // 会话 hook 可能已把新入口写进配置文件，重读一次看看——
            // 正在运行的实例把它们存在内存里，不会自己重读
            if (ReloadStoredCredentials())
            {
                _errorNotice = "入口已更新，重连中";
                _errorActionable = false;
                return;
            }

            _errorNotice = "入口已失效，需要新地址";
            _errorActionable = true;
            return;
        }

        // 直连上游是主路径：它给出的原因（没登录 / token 过期 / 网络不通）比
        // 「找不到本地代理」更接近根因，优先报它
        if (_client.UpstreamNotice is { Length: > 0 } upstream)
        {
            var absence = LimitsClient.DiagnoseAbsence();
            _errorNotice = absence == LimitsClient.Absence.NotRunning
                ? upstream
                : $"{upstream} · 代理也没数据";
            _errorActionable = true;
            return;
        }

        switch (LimitsClient.DiagnoseAbsence())
        {
            case LimitsClient.Absence.Sleeping:
                _errorNotice = "代理休眠中";
                _errorActionable = false;
                break;
            case LimitsClient.Absence.NotRunning:
                _errorNotice = "Mirasim 未运行";
                _errorActionable = true;
                break;
            default:
                _errorNotice = "找不到本地代理";
                _errorActionable = true;
                break;
        }
    }

    /// <summary>
    /// 故障状态下那行「N 秒后重试」得自己走。只在失败瞬间写一次的话，

    /// 显示的是退避间隔有多长，而不是还剩多久重试。
    /// </summary>
    private void UpdateRetryCountdown()
    {
        if (_errorNotice is null)
        {
            return;
        }

        var left = _retryInterval - (DateTime.UtcNow - _lastAttemptUtc);
        var seconds = (int)Math.Ceiling(Math.Max(0d, left.TotalSeconds));

        // 休眠这类会自动恢复的，说「N 秒后重连」；需要用户处理的才说「重试」并报警色
        var verb = _errorActionable ? "重试" : "重连";
        SetStatus(
            seconds > 0 ? $"{_errorNotice} · {seconds}s 后{verb}" : $"{_errorNotice} · 正在{verb}",
            string.Empty,
            _errorActionable ? Theme.Critical : Theme.Accent);
    }

    /// <summary>从磁盘重读 token（会话 hook 可能刚更新过）；确实变了就装载并返回 true。</summary>
    private bool ReloadStoredCredentials()
    {
        var latest = SettingsStore.Load();
        var changed = false;

        if (!string.IsNullOrEmpty(latest.AuthBaseUrl) && latest.AuthBaseUrl != _settings.AuthBaseUrl)
        {
            _settings.AuthBaseUrl = latest.AuthBaseUrl;
            _client.StoredBaseUrl = latest.AuthBaseUrl;
            changed = true;
        }

        if (!string.IsNullOrEmpty(latest.AuthToken) && latest.AuthToken != _settings.AuthToken)
        {
            _settings.AuthToken = latest.AuthToken;
            _client.StoredToken = latest.AuthToken;
            changed = true;
        }

        if (changed)
        {
            _retryInterval = _refreshInterval; // 有新入口，别退避，尽快重试
        }

        return changed;
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

    /// <summary>需要盖掉时间去提示的特殊状态；一切正常时为 null。</summary>
    private static string? NoticeFor(LimitsDto snapshot) => snapshot switch
    {
        { Suspended: true } => "账号已暂停",
        { Unmetered: true } => "不计量账号",
        { Degraded: true } => "服务降级中",
        _ => null
    };

    private static Brush StateBrush(LimitsDto snapshot) => snapshot switch
    {
        { Suspended: true } => Theme.Critical,
        { Degraded: true } => Theme.Warning,
        _ => Theme.Accent
    };

    /// <summary>
    /// 标题栏那一小块只放得下一样东西：没有特殊状态就滚动显示刷新时间，
    /// 有状态或出错则让文字顶上去——那时用户关心的是状态，不是刷新了几秒。
    /// </summary>
    private void SetStatus(string? notice, string clock, Brush dot)
    {
        var showNotice = !string.IsNullOrEmpty(notice);

        StatusText.Text = notice ?? string.Empty;
        StatusText.Visibility = showNotice ? Visibility.Visible : Visibility.Collapsed;

        StatusClock.Text = showNotice ? string.Empty : clock;
        StatusClock.Visibility = showNotice ? Visibility.Collapsed : Visibility.Visible;

        StatusDot.Fill = dot;
    }

    /// <summary>托盘图标跟着水位最高的那个窗口走。</summary>
    private void UpdateTray()
    {
        if (!_settings.ShowTrayIcon || _windows.Count == 0)
        {
            return;
        }

        var hottest = _windows.MaxBy(window => window.Percent)!;
        var tooltip = string.Join(
            Environment.NewLine,
            _windows.Select(window => $"{window.Title} {window.PercentText}  {window.UsedText} {window.BudgetText}"));

        _tray.Update(hottest.Percent, tooltip, ((SolidColorBrush)hottest.BarBrush).Color);
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
