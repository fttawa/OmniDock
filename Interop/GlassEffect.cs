using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OmniDock.Interop;

/// <summary>
/// 给无边框 WPF 窗口套上系统级毛玻璃背景。
///
/// 这里刻意没有使用 Windows 11 的 DWMWA_SYSTEMBACKDROP_TYPE：
/// 实测无边框窗口（WindowStyle=None，没有 WS_CAPTION）设置它会返回 S_OK 却什么都不画，
/// 窗口只会露出一片纯色。真正对无边框窗口有效的是 SetWindowCompositionAttribute 的
/// 亚克力模糊，Windows 10 1803 起一直可用。
/// </summary>
internal static class GlassEffect
{
    /// <summary>本次实际生效的方案，便于界面提示或排查。</summary>
    internal enum Backdrop
    {
        None,
        /// <summary>亚克力模糊（首选）。</summary>
        Acrylic,
        /// <summary>旧版 Aero 模糊，仅老系统会走到。</summary>
        Blur
    }

    /// <summary>
    /// 玻璃着色，格式为 0xAABBGGRR（注意是 BGR 顺序，不是常见的 RGB）。
    /// alpha 决定玻璃厚度：0x18 通透、0x33 偏厚，低于 0x10 玻璃感基本消失，
    /// 0x99 以上会糊成一片实色。后三位是玻璃色调，加大 BB 位偏冷蓝。
    /// </summary>
    internal const uint DefaultTint = 0x18202028;

    // ---- DWM 窗口属性 ----
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19; // Win10 1809/1903 用的旧编号
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaSystemBackdropType = 38;

    private const int DwmwcpRound = 2;               // 圆角
    private const int DwmsbtNone = 1;                // 明确关掉系统 backdrop，避免和亚克力叠加
    private const uint DwmwaColorNone = 0xFFFFFFFE;  // 不画边框线

    // ---- SetWindowCompositionAttribute ----
    private const int WcaAccentPolicy = 19;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const int AccentEnableBlurBehind = 3;
    private const int AccentFlagsAllBorders = 2;

    /// <summary>
    /// 对窗口应用毛玻璃。必须在窗口句柄创建之后调用（OnSourceInitialized 或更晚）。
    /// </summary>
    /// <param name="window">目标窗口，需为 AllowsTransparency=False 的无边框窗口。</param>
    /// <param name="darkMode">是否使用暗色玻璃。</param>
    /// <param name="tint">玻璃着色，格式 0xAABBGGRR。</param>
    internal static Backdrop Apply(Window window, bool darkMode = true, uint tint = DefaultTint)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("窗口句柄尚未创建，请在 OnSourceInitialized 之后调用。");
        }

        // 关键一步：让 WPF 不再绘制不透明的客户区底色，
        // 否则 DWM 在窗口背后画的玻璃层会被整块盖住。
        if (PresentationSource.FromVisual(window) is HwndSource source && source.CompositionTarget is not null)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }

        // 把窗口框架延伸到整个客户区，让 DWM 把整块区域都当成可合成的玻璃面
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(handle, ref margins);

        if (darkMode && !TrySetAttribute(handle, DwmwaUseImmersiveDarkMode, 1))
        {
            TrySetAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, 1);
        }

        TrySetAttribute(handle, DwmwaSystemBackdropType, DwmsbtNone);
        TrySetAttribute(handle, DwmwaWindowCornerPreference, DwmwcpRound);
        TrySetAttribute(handle, DwmwaBorderColor, unchecked((int)DwmwaColorNone));

        // 亚克力优先；只有 Win10 1803 之前的系统才会退到旧版 Aero 模糊
        if (TrySetAccent(handle, AccentEnableAcrylicBlurBehind, tint))
        {
            return Backdrop.Acrylic;
        }

        return TrySetAccent(handle, AccentEnableBlurBehind, tint) ? Backdrop.Blur : Backdrop.None;
    }

    private static bool TrySetAccent(IntPtr handle, int accentState, uint tint)
    {
        var policy = new AccentPolicy
        {
            AccentState = accentState,
            AccentFlags = AccentFlagsAllBorders,
            GradientColor = tint,
            AnimationId = 0
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, buffer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = buffer,
                SizeOfData = size
            };

            return SetWindowCompositionAttribute(handle, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TrySetAttribute(IntPtr handle, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)) == 0;
        }
        catch (DllNotFoundException)
        {
            return false; // 理论上不会发生，dwmapi.dll 从 Vista 起就有
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }
}
