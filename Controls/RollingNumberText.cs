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
    }

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

    /// <summary>按当前字体实测一格的尺寸：宽取 0-9 里最宽的，高取字体行高。</summary>
    private (double Width, double Height) MeasureDigitCell()
    {
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var pixelsPerDip = 1d;
        try
        {
            pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        }
        catch (InvalidOperationException)
        {
            // 还没进视觉树时取不到 DPI，用 1 也能得到正确的 DIP 尺寸
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
                pixelsPerDip);

            width = Math.Max(width, measured.WidthIncludingTrailingWhitespace);
            height = Math.Max(height, measured.Height);
        }

        return (Math.Ceiling(width) + 1d, Math.Ceiling(height));
    }

    /// <summary>一位数字的滚轮：0-9 竖排在 Canvas 上，靠平移露出目标数字。</summary>
    private sealed class DigitRoller : Grid
    {
        private static readonly Duration RollDuration = new(TimeSpan.FromMilliseconds(320));

        private readonly TranslateTransform _offset = new();
        private readonly double _cellHeight;
        private int _current;

        internal DigitRoller(char digit, double cellWidth, double cellHeight)
        {
            _current = digit - '0';
            _cellHeight = cellHeight;

            // Canvas 绝对定位，不受测量/排列影响，格间距就是 cellHeight
            var strip = new Canvas { RenderTransform = _offset };
            for (var n = 0; n <= 9; n++)
            {
                var cell = new TextBlock
                {
                    Text = n.ToString(),
                    Width = cellWidth,
                    TextAlignment = TextAlignment.Center
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
            _offset.Y = -_current * cellHeight;
        }

        /// <summary>滚到目标数字；返回是否真的动了，用来决定后一位要不要错开。</summary>
        internal bool RollTo(char digit, TimeSpan delay)
        {
            var target = digit - '0';
            if (target == _current)
            {
                return false;
            }

            _current = target;

            // 不指定 From，动画从当前位置接着走，连续变化时不会跳
            var roll = new DoubleAnimation(-target * _cellHeight, RollDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                BeginTime = delay
            };

            _offset.BeginAnimation(TranslateTransform.YProperty, roll);
            return true;
        }
    }
}
