using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OmniDock.Controls;

/// <summary>
/// 数字滚动文本（里程表效果）：字符串里的每个数字都是一条 0-9 的竖条，
/// 在只露出一个字高的窗口里滑到目标数字；其余字符（逗号、%、汉字）按普通文字排。
/// 只有真正变化的那一位会滚，且从个位起依次延迟出发，滚动看起来是连带的。
///
/// 竖条用 Canvas 绝对定位，格高由 FormattedText 实测字体行高得出：
/// 定位和位移必须用同一个数，否则数字会被推出可见区。
/// 颜色和字号靠 WPF 的属性继承传给内部文本。
/// </summary>
public sealed class RollingNumberText : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(RollingNumberText),
        new PropertyMetadata(string.Empty, OnTextChanged));

    private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal };

    public RollingNumberText()
    {
        Content = _panel;
        Focusable = false;

        // 构造时还拿不到真实 DPI，等进了视觉树再按实际缩放重算一次格高
        Loaded += (_, _) => Rebuild(Text);
    }

    /// <summary>拖到另一块不同缩放的屏幕上时，格高要跟着重算。</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) => Rebuild(Text);

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RollingNumberText)d).Sync((string?)e.NewValue ?? string.Empty);

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // 字体一变，格高就得重算。XAML 里 Text 绑定可能比 FontSize 先生效，
        // 所以这里必须重建，不能只在 Text 变化时算一次。
        if (e.Property == FontSizeProperty
            || e.Property == FontFamilyProperty
            || e.Property == FontWeightProperty
            || e.Property == FontStyleProperty
            || e.Property == FontStretchProperty)
        {
            Rebuild(Text);
        }
    }

    private void Sync(string text)
    {
        if (!StructureMatches(text))
        {
            Rebuild(text);
            return;
        }

        // 从个位往高位走，越靠右的位越早出发，滚动有前后次序
        var stagger = 0;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (_panel.Children[i] is not DigitRoller roller)
            {
                continue;
            }

            if (roller.RollTo(text[i], TimeSpan.FromMilliseconds(stagger * 26)))
            {
                stagger = Math.Min(stagger + 1, 4); // 位数多时别拖太久
            }
        }
    }

    private bool StructureMatches(string text)
    {
        if (_panel.Children.Count != text.Length)
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var isDigit = char.IsDigit(text[i]);
            switch (_panel.Children[i])
            {
                case DigitRoller when !isDigit:
                case TextBlock when isDigit:
                    return false;
                // 非数字位还要求字符一致，否则「小时」变「分」这类会错位
                case TextBlock plain when plain.Text != text[i].ToString():
                    return false;
            }
        }

        return true;
    }

    private void Rebuild(string text)
    {
        _panel.Children.Clear();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var (cellWidth, cellHeight) = MeasureDigitCell();
        foreach (var c in text)
        {
            if (char.IsDigit(c))
            {
                _panel.Children.Add(new DigitRoller(c, cellWidth, cellHeight));
            }
            else
            {
                _panel.Children.Add(new TextBlock
                {
                    Text = c.ToString(),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = c is ',' or '.' ? new Thickness(0) : new Thickness(1.5, 0, 0, 0)
                });
            }
        }
    }

    /// <summary>
    /// 按当前字体实测一格的尺寸：宽取 0-9 里最宽的，高取字体行高。
    ///
    /// 尺寸会向上取到整数个「物理」像素。位移量是格高的整数倍，
    /// 如果格高换算成物理像素带小数（例如 125% 缩放下的 14 DIP = 17.5px），
    /// 奇数格就会停在半像素上，看起来上下错开一点且发虚。
    /// </summary>
    private (double Width, double Height) MeasureDigitCell()
    {
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var dpi = new DpiScale(1d, 1d);
        try
        {
            dpi = VisualTreeHelper.GetDpi(this);
        }
        catch (InvalidOperationException)
        {
            // 还没进视觉树，先按 1:1 算，Loaded 之后会重建
        }

        var width = 0d;
        var height = 0d;
        for (var c = '0'; c <= '9'; c++)
        {
            var measured = new FormattedText(
                c.ToString(),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                Brushes.White,
                dpi.PixelsPerDip);

            width = Math.Max(width, measured.WidthIncludingTrailingWhitespace);
            height = Math.Max(height, measured.Height);
        }

        return (SnapUp(width + 1d, dpi.DpiScaleX), SnapUp(height, dpi.DpiScaleY));
    }

    /// <summary>向上取到整数个物理像素，再换回 DIP。</summary>
    private static double SnapUp(double dipLength, double scale)
    {
        if (scale <= 0d)
        {
            return Math.Ceiling(dipLength);
        }

        return Math.Ceiling(dipLength * scale) / scale;
    }

    /// <summary>
    /// 一位数字的滚轮：0-9 竖排在 Canvas 上，靠平移露出目标数字。
    ///
    /// 竖条铺了两遍 0-9，这样可以走「环形最短路径」：9→0 只往上滚一格（进位），
    /// 0→9 只往下滚一格（借位），而不是横穿九格倒回去。
    /// 越过第一遍之后再无动画折回等价位置，看不出接缝。
    /// </summary>
    private sealed class DigitRoller : Grid
    {
        private static readonly Duration RollDuration = new(TimeSpan.FromMilliseconds(320));

        /// <summary>0-9 铺两遍。</summary>
        private const int Laps = 2;

        private readonly TranslateTransform _offset = new();
        private readonly double _cellHeight;

        /// <summary>当前停在第几格（0..19），显示的数字是它模 10。</summary>
        private int _index;

        /// <summary>动画的批次号，用来判断折回时是否已被新的滚动接管。</summary>
        private int _sequence;

        internal DigitRoller(char digit, double cellWidth, double cellHeight)
        {
            _index = digit - '0';
            _cellHeight = cellHeight;

            // Canvas 绝对定位，不受测量/排列影响，格间距就是 cellHeight
            var strip = new Canvas { RenderTransform = _offset };
            for (var n = 0; n < 10 * Laps; n++)
            {
                // 格高向上取整过，比字形略高一点，所以套一层让数字在格内居中，
                // 否则每个数字都会贴着格子上沿，整排看着偏上
                var cell = new Border
                {
                    Width = cellWidth,
                    Height = cellHeight,
                    Child = new TextBlock
                    {
                        Text = (n % 10).ToString(),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };

                Canvas.SetLeft(cell, 0d);
                Canvas.SetTop(cell, n * cellHeight);
                strip.Children.Add(cell);
            }

            Children.Add(strip);
            Width = cellWidth;
            Height = cellHeight;   // 只露出一位，其余靠下面的裁剪藏起来
            ClipToBounds = true;
            SnapsToDevicePixels = true;
            _offset.Y = -_index * cellHeight;
        }

        /// <summary>滚到目标数字；返回是否真的动了，用来决定后一位要不要错开。</summary>
        internal bool RollTo(char digit, TimeSpan delay)
        {
            var target = digit - '0';
            var current = _index % 10;
            if (target == current)
            {
                return false;
            }

            // 环形距离：往上和往下哪边近走哪边，相等时优先往上（数值增加的直觉）
            var up = (target - current + 10) % 10;
            var down = (current - target + 10) % 10;
            _index = up <= down ? _index + up : _index - down;

            var batch = ++_sequence;

            // 不指定 From，动画从当前位置接着走，连续变化时不会跳
            var roll = new DoubleAnimation(-_index * _cellHeight, RollDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                BeginTime = delay
            };

            roll.Completed += (_, _) =>
            {
                // 期间又滚过就交给后来的那次去折回，避免打断它
                if (batch == _sequence)
                {
                    FoldBack();
                }
            };

            _offset.BeginAnimation(TranslateTransform.YProperty, roll);
            return true;
        }

        /// <summary>把索引折回第一遍 0-9。位置等价，所以看不出跳变。</summary>
        private void FoldBack()
        {
            var folded = ((_index % 10) + 10) % 10;
            if (folded == _index)
            {
                return;
            }

            _index = folded;
            _offset.BeginAnimation(TranslateTransform.YProperty, null);
            _offset.Y = -_index * _cellHeight;
        }
    }
}
