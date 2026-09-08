using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;

namespace OmniDock.Interop;

/// <summary>
/// 从其它进程的命令行里捞出代理入口。
///
/// 代理把入口（端口 + 路径凭据）注入给它启动的每个 CLI 会话，有些 CLI 会把它
/// 展开在自己的命令行上（例如 codex 的 <c>model_providers.*.base_url=</c>）。命令行是
/// 进程的公开信息，任务管理器就能看，取它不必读别人的内存——那是另一回事，不做。
///
/// 只认回环地址后面跟一段长路径的形状，别的一律不看、不留。查询要一秒多，
/// 所以只在其它来源都失效时兜这一下。
/// </summary>
internal static class ProxyEntryScan
{
    /// <summary>
    /// <c>http://127.0.0.1:63440/v3Ortb-…</c>。路径至少 20 个字符才算凭据，
    /// 免得把 <c>/mcp</c>、<c>/v1</c> 这类普通路由当成入口。
    /// </summary>
    private static readonly Regex EntryPattern = new(
        @"http://(?:127\.0\.0\.1|localhost):(\d{2,5})/([A-Za-z0-9_-]{20,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>命令行长成什么样都可能，扫描时间兜个底。</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    /// <summary>
    /// 扫出来的候选入口，去重后按出现次数从多到少排（多个会话都在用的那个最可能是活的）。
    /// 拿不到就返回空。
    /// </summary>
    internal static IReadOnlyList<Uri> Candidates()
    {
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            var watch = Stopwatch.StartNew();

            // WHERE 子句先把范围收窄到「命令行里提到回环地址」的进程，
            // 既快得多，也不至于把满机器的命令行都翻一遍
            using var searcher = new ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE CommandLine LIKE '%127.0.0.1%'");
            using var results = searcher.Get();

            foreach (var item in results)
            {
                using var process = (ManagementObject)item;
                if (watch.Elapsed > Budget)
                {
                    break;
                }

                if (process["CommandLine"] is not string commandLine)
                {
                    continue;
                }

                foreach (var match in EntryPattern.Matches(commandLine).Cast<Match>())
                {
                    var entry = $"http://127.0.0.1:{match.Groups[1].Value}/{match.Groups[2].Value}";
                    hits[entry] = hits.GetValueOrDefault(entry) + 1;
                }
            }
        }
        catch (ManagementException)
        {
            // WMI 不可用（服务停了、权限不够）就当没这个来源
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        return hits
            .OrderByDescending(pair => pair.Value)
            .Select(pair => Uri.TryCreate(pair.Key, UriKind.Absolute, out var uri) ? uri : null)
            .OfType<Uri>()
            .ToArray();
    }
}
