using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OmniDock.Interop;

namespace OmniDock;

/// <summary>
/// 无边框毛玻璃窗口：可整体拖动、Esc 关闭，并带展开/收起动画。
/// 主窗口和设置窗口共用这一套，玻璃的具体做法见 <see cref="GlassEffect"/>。
/// </summary>
public abstract class GlassWindow : Window
{
    protected GlassWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");

        MouseLeftButtonDown += (_, _) => TryDragMove();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                RequestClose();
            }
        };
    }

    /// <summary>玻璃着色，子类可在应用之前改。</summary>
    protected uint GlassTint { get; set; } = GlassEffect.DefaultTint;

    /// <summary>窗口句柄是否已创建。贴玻璃、按屏幕坐标定位都得等它为 true。</summary>
    protected bool IsSourceReady { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IsSourceReady = true;
        ApplyGlass();
    }

    /// <summary>重新贴一次玻璃，改了厚度之后调用即可生效。</summary>
    protected void ApplyGlass() => GlassEffect.Apply(this, tint: GlassTint);

    /// <summary>Esc 走这里，子类可以改成带动画的关闭。</summary>
    protected virtual void RequestClose() => Close();

    /// <summary>
    /// 从折叠高度长到完整高度，内容同时淡入。
    /// 窗口此刻被压在折叠高度上，内容的 DesiredSize 会被约束住，
    /// 所以先单独量一次不受限的高度当作目标。
    /// </summary>
    protected void AnimateExpand(FrameworkElement content, double collapsedHeight)
    {
        content.Measure(new Size(Width, double.PositiveInfinity));
        var target = Math.Ceiling(content.DesiredSize.Height);

        if (target <= collapsedHeight)
        {
            content.Opacity = 1d;
            SizeToContent = SizeToContent.Height;
            return;
        }

        var grow = new DoubleAnimation(collapsedHeight, target, new Duration(TimeSpan.FromMilliseconds(200)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        grow.Completed += (_, _) =>
        {
            // 交还属性控制权，之后高度重新由内容决定
            BeginAnimation(HeightProperty, null);
            Height = target;
            SizeToContent = SizeToContent.Height;
        };

        BeginAnimation(HeightProperty, grow);

        content.BeginAnimation(OpacityProperty, new DoubleAnimation(0d, 1d, new Duration(TimeSpan.FromMilliseconds(240)))
        {
            BeginTime = TimeSpan.FromMilliseconds(50)
        });
    }

    /// <summary>收回到折叠高度，内容先淡出，收完再执行 <paramref name="done"/>。</summary>
    protected void AnimateCollapse(FrameworkElement content, double collapsedHeight, Action done)
    {
        SizeToContent = SizeToContent.Manual;

        var from = ActualHeight;
        var to = Math.Min(collapsedHeight, from);

        content.BeginAnimation(OpacityProperty, new DoubleAnimation(0d, new Duration(TimeSpan.FromMilliseconds(110))));

        var shrink = new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(150)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        shrink.Completed += (_, _) => done();
        BeginAnimation(HeightProperty, shrink);
    }

    /// <summary>按在按钮上时事件已被按钮处理，不会走到这里。</summary>
    private void TryDragMove()
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 极少数情况下鼠标已提前释放，忽略即可
        }
    }
}
