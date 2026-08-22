using System.IO;
using Microsoft.Win32;

namespace OmniDock.Services;

/// <summary>
/// 开机自启：往当前用户的 Run 键里写一条。
/// 只动 HKCU，不需要管理员权限；任何一步失败都当作不可用，不抛给界面。
/// </summary>
internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OmniDock";

    /// <summary>当前是否已登记，且登记的还是这个 exe。</summary>
    internal static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key?.GetValue(ValueName) is not string registered)
            {
                return false;
            }

            return string.Equals(registered.Trim('"'), ExecutablePath(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>写入或删除自启项，返回操作后的实际状态。</summary>
    internal static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                // 路径可能带空格，加引号
                key.SetValue(ValueName, $"\"{ExecutablePath()}\"");
                return true;
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return false;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return IsEnabled();
        }
    }

    /// <summary>单文件发布下 Assembly.Location 是空的，用进程路径才靠得住。</summary>
    private static string ExecutablePath() => Environment.ProcessPath ?? string.Empty;
}
