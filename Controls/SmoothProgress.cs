using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace OmniDock.Controls;

/// <summary>
/// 让 ProgressBar 的数值平滑过渡，跟滚动数字的节奏配起来。
/// 直接绑 Value 会让水位跳变，所以绑这个附加属性，由它去动画真正的 Value。
/// </summary>
public static class SmoothProgress
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target",
        typeof(double),
        typeof(SmoothProgress),
        new PropertyMetadata(double.NaN, OnTargetChanged));

    public static void SetTarget(DependencyObject element, double value)
        => element.SetValue(TargetProperty, value);

    public static double GetTarget(DependencyObject element)
        => (double)element.GetValue(TargetProperty);

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ProgressBar bar || e.NewValue is not double target || double.IsNaN(target))
        {
            return;
        }

        // 第一次直接落位，不然启动时会从 0 爬上来
        if (e.OldValue is not double previous || double.IsNaN(previous))
        {
            bar.Value = target;
            return;
        }

        var slide = new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(420)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        // 不指定 From，从当前水位接着走，连续变化不会跳
        bar.BeginAnimation(RangeBase.ValueProperty, slide);
    }
}
