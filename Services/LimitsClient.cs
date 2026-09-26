using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniDock.Interop;

namespace OmniDock.Services;

/// <summary>一个额度窗口，例如 5 小时窗口或 7 天窗口。</summary>
internal sealed record UsageWindowDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("used")] double Used,
    [property: JsonPropertyName("budget")] double Budget,
    [property: JsonPropertyName("reset_at")] long ResetAt);

/// <summary>/v1/limits 的响应。</summary>
internal sealed record LimitsDto(
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("suspended")] bool Suspended,
    [property: JsonPropertyName("unmetered")] bool Unmetered,
    [property: JsonPropertyName("degraded")] bool Degraded,
    [property: JsonPropertyName("windows")] IReadOnlyList<UsageWindowDto>? Windows);

/// <summary>拉取的结果：区分「没找到代理」和「找到了但认证不通过」。</summary>
internal enum FetchStatus
{
    Ok,
    NotReachable,
    Unauthorized
}

/// <summary>这一次的数据是从哪来的：直连上游，还是本地代理。</summary>
internal enum FetchSource
{
    /// <summary>拿账号 token 直接请求上游，不经过本机代理。</summary>
    Upstream,

    /// <summary>走 Mirasim 本地代理（上游直连不可用时的回退）。</summary>
    Proxy
}

internal readonly record struct FetchResult(FetchStatus Status, LimitsDto? Data, FetchSource Source = FetchSource.Proxy);

/// <summary>
/// 读取额度：**优先用账号 token 直连上游**，拿不到再回退到本机的 Mirasim 本地代理。
///
/// 直连这条路是主路径：token 从 <see cref="MirasimCredentials"/> 解出来（Mirasim 自己续期后会
/// 写回同一份配置），所以它既不受代理重启影响，也不需要会话环境变量。上游地址默认是
/// <see cref="DefaultUpstream"/>，可以在设置里改。
///
/// 代理那条路是回退，原因见下：代理要求带入口 token（早期版本对回环请求免认证，后来改了），
/// token 由代理每次启动新生成并注入会话环境、不落盘；端口和 URL 路径凭据也每次都换。
/// 因此这里按「环境变量 → 上次成功的端点 → 代理进程监听的端口 → 扫命令行」依次尝试。
/// </summary>
internal sealed class LimitsClient : IDisposable
{
    private const string ProxyProcessName = "Mirasim";

    /// <summary>默认上游：Mirasim 中转的额度端点（<c>GET /v1/limits</c>）。</summary>
    internal const string DefaultUpstream = "https://relay.mirasim.ai";

    /// <summary>用户手填或上次自动存下的 token；会话环境里有值时优先用环境里的。</summary>
    internal string? StoredToken { get; set; }

    /// <summary>同上，但是完整入口地址（含路径凭据）。</summary>
    internal string? StoredBaseUrl { get; set; }

    /// <summary>上游根地址；为空表示不用直连，直接走代理。</summary>
    internal string UpstreamBaseUrl { get; set; } = DefaultUpstream;

    /// <summary>
    /// 这次成功用的入口和存档里的不是一个——调用方读到之后应该落盘。
    /// 读一次就清掉，避免反复写同一个值。
    /// </summary>
    internal string? DiscoveredEntry { get; set; }

    /// <summary>最近一次成功的来源，界面用它说明「数据是怎么来的」。</summary>
    internal FetchSource? LastSource { get; private set; }

    /// <summary>直连上游不可用时的原因（用于故障提示）；上游可用时为空。</summary>
    internal string? UpstreamNotice { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // 禁用系统代理，否则发往 127.0.0.1 的请求可能被转到外部代理
    // 超时压得比刷新间隔宽松一点就够：本地请求正常只要几十毫秒
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(2.5)
    };

    // 试探未知端口时用更短的超时，避免卡在不相干的服务上
    private readonly HttpClient _probe = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromMilliseconds(800)
    };

    // 直连上游是公网请求：走系统代理（该有的网络环境要能通），超时给得比本地宽
    private readonly HttpClient _upstream = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    private Uri? _knownEndpoint;

    /// <summary>命令行扫描要一秒多，失败重试时别每次都来一遍。</summary>
    private static readonly TimeSpan ScanCooldown = TimeSpan.FromSeconds(30);
    private DateTime _lastScan = DateTime.MinValue;

    /// <summary>当前生效的代理端点，没走过代理时为 null。</summary>
    internal Uri? Endpoint => _knownEndpoint;

    internal async Task<FetchResult> FetchAsync(CancellationToken ct)
    {
        // 1) 主路径：账号 token 直连上游。成功就结束，不碰本地代理。
        var upstream = await TryFetchUpstreamAsync(ct).ConfigureAwait(false);
        if (upstream.Status == FetchStatus.Ok)
        {
            UpstreamNotice = null;
            LastSource = FetchSource.Upstream;
            return upstream with { Source = FetchSource.Upstream };
        }

        // 2) 回退：本地代理。上游失败的原因留着，两个都失败时一起报出来。
        var proxied = await FetchViaProxyAsync(ct).ConfigureAwait(false);
        if (proxied.Status == FetchStatus.Ok)
        {
            LastSource = FetchSource.Proxy;
        }

        return proxied;
    }

    /// <summary>直连上游：token 缺失/过期、网络不通、上游拒绝，都只当作「这条路暂时不行」。</summary>
    private async Task<FetchResult> TryFetchUpstreamAsync(CancellationToken ct)
    {
        if (!Uri.TryCreate(UpstreamBaseUrl, UriKind.Absolute, out var upstream))
        {
            UpstreamNotice = "上游地址无效";
            return new FetchResult(FetchStatus.NotReachable, null);
        }

        var token = ResolveUpstreamToken(out var why);
        if (token is null)
        {
            UpstreamNotice = why ?? "没有可用的账号 token";
            return new FetchResult(FetchStatus.NotReachable, null);
        }

        var result = await TryFetchAsync(upstream, _upstream, token, ct).ConfigureAwait(false);
        if (result.Status != FetchStatus.Ok)
        {
            UpstreamNotice = result.Status == FetchStatus.Unauthorized
                ? "上游拒绝了这个 token"
                : "上游连不上";

            // 401 可能是 Mirasim 刚续过期、配置也换了：丢掉缓存，下一拍重新解
            if (result.Status == FetchStatus.Unauthorized)
            {
                MirasimCredentials.Invalidate();
            }
        }

        return result;
    }

    /// <summary>
    /// 上游用的 token：优先解 Mirasim 配置（最新，Mirasim 续期后会写回），
    /// 其次会话环境变量，最后才是设置里存的（可能是用户手填的 JWT）。
    /// 代理的入口 token 是 43 位随机串、不是 JWT，这里会跳过它。
    /// </summary>
    private string? ResolveUpstreamToken(out string? reason)
    {
        var fromConfig = MirasimCredentials.TryReadToken(out var configFailure);
        if (fromConfig is not null)
        {
            reason = null;
            return fromConfig;
        }

        if (Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN") is { Length: > 0 } fromEnv && LooksLikeJwt(fromEnv))
        {
            reason = null;
            return fromEnv;
        }

        if (LooksLikeJwt(StoredToken))
        {
            reason = null;
            return StoredToken;
        }

        reason = configFailure ?? "设置里也没有可用的账号 token";
        return null;
    }

    private static bool LooksLikeJwt(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var first = token.IndexOf('.');
        return first > 0 && token.IndexOf('.', first + 1) > first + 1;
    }

    /// <summary>走本地代理的老路径。</summary>
    private async Task<FetchResult> FetchViaProxyAsync(CancellationToken ct)
    {
        var sawAuthFailure = false;

        // 已知端点直接用；一旦不是成功就清掉、重新完整扫描。
        // 不对 401 短路：同一进程可能开多个端口，只有部分是 limits 端点（另一些
        // 对同一 token 返回 401），或者机器上有多个代理实例，记住的端点恰好是死的。
        // 短路会永远卡在坏端点，发现不了那个真正能用的。
        if (_knownEndpoint is not null)
        {
            var known = await TryFetchAsync(_knownEndpoint, _http, ResolveToken(), ct).ConfigureAwait(false);
            if (known.Status == FetchStatus.Ok)
            {
                return known with { Source = FetchSource.Proxy };
            }

            _knownEndpoint = null;
        }

        foreach (var candidate in EnumerateCandidates().Distinct())
        {
            var attempt = await TryFetchAsync(candidate, _probe, ResolveToken(), ct).ConfigureAwait(false);

            if (attempt.Status == FetchStatus.Unauthorized)
            {
                // 401 说明这个端口是代理，但少了路径凭据。既不记成已知端点也不缓存：
                // 它永远不会成功，留着只会在下次抢在真正可用的候选前面白跑一趟。
                // 只记一笔，用来把「认证不过」和「压根找不到」分开报
                sawAuthFailure = true;
                continue;
            }

            if (attempt.Status != FetchStatus.Ok)
            {
                continue;
            }

            _knownEndpoint = candidate;
            SaveCachedEndpoint(candidate);

            // 环境变量和存档之外的来源（缓存、扫描）也值得记进设置，
            // 这样下次冷启动不必再扫一遍
            if (candidate.AbsoluteUri.TrimEnd('/') != (StoredBaseUrl ?? string.Empty).TrimEnd('/'))
            {
                DiscoveredEntry = candidate.AbsoluteUri;
            }

            return attempt with { Source = FetchSource.Proxy };
        }

        return new FetchResult(
            sawAuthFailure ? FetchStatus.Unauthorized : FetchStatus.NotReachable,
            null);
    }

    /// <summary>会话环境里的最新鲜，其次才是存下来的。</summary>
    private string? ResolveToken()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN");
        return !string.IsNullOrWhiteSpace(fromEnv) ? fromEnv : StoredToken;
    }

    /// <summary>拿不到数据时，进一步说明是哪种情况——休眠会自动恢复，其它才需要用户处理。</summary>
    internal enum Absence
    {
        /// <summary>Mirasim 在，但代理端口关了：休眠中，唤醒后自动恢复。</summary>
        Sleeping,
        /// <summary>Mirasim 进程都不在了。</summary>
        NotRunning,
        /// <summary>有端口却连不上或没数据，罕见。</summary>
        Unreachable
    }

    /// <summary>NotReachable 时调用，区分休眠 / 未运行 / 真连不上。</summary>
    internal static Absence DiagnoseAbsence()
    {
        if (!LocalPorts.ProcessRunning(ProxyProcessName))
        {
            return Absence.NotRunning;
        }

        // 进程在、却没有监听端口，就是休眠把端口关了
        return LocalPorts.LoopbackListenersOf(ProxyProcessName).Count > 0
            ? Absence.Unreachable
            : Absence.Sleeping;
    }

    private IEnumerable<Uri> EnumerateCandidates()
    {
        // 1) 从带环境的会话里启动时，完整入口（端口 + 路径凭据）就在环境变量里
        if (Uri.TryCreate(Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL"), UriKind.Absolute, out var fromEnv))
        {
            yield return fromEnv;
        }

        // 2) 上次从会话里存下的入口，双击启动时全靠它
        if (Uri.TryCreate(StoredBaseUrl, UriKind.Absolute, out var stored))
        {
            yield return stored;
        }

        // 3) 上次成功的端点，同一次代理运行期间仍然有效
        if (ReadCachedEndpoint() is { } cached)
        {
            yield return cached;
        }

        // 4) 反查代理进程监听的回环端口。新版代理把凭据放进了路径，
        //    光有端口是拼不出可用地址的，但留着不亏——早期版本对回环免认证，
        //    而且这能把「代理确实在跑」和「进程都没了」区分开
        foreach (var port in LocalPorts.LoopbackListenersOf(ProxyProcessName))
        {
            yield return new Uri($"http://127.0.0.1:{port}");
        }

        // 5) 最后一招：从别的 CLI 会话的命令行里捞入口。上面几条都靠「之前见过」，
        //    只有这条能在代理重启后凭当下的机器状态重新发现，代价是要一秒多，
        //    所以摆在最末尾、且下面还压了节流
        if (_lastScan + ScanCooldown <= DateTime.UtcNow)
        {
            _lastScan = DateTime.UtcNow;
            foreach (var scanned in ProxyEntryScan.Candidates())
            {
                yield return scanned;
            }
        }
    }

    private async Task<FetchResult> TryFetchAsync(Uri baseUri, HttpClient http, string? token, CancellationToken ct)
    {
        try
        {
            // baseUri 可能带路径前缀，所以按字符串拼而不是用 Uri 的相对解析
            var url = baseUri.AbsoluteUri.TrimEnd('/') + "/v1/limits";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // 两种头都带：实测都能通过，将来它只认其中一种也不会挂
            if (token is { Length: > 0 })
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                request.Headers.TryAddWithoutValidation("x-api-key", token);
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new FetchResult(FetchStatus.Unauthorized, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new FetchResult(FetchStatus.NotReachable, null);
            }

            var dto = await response.Content.ReadFromJsonAsync<LimitsDto>(JsonOptions, ct).ConfigureAwait(false);

            // 必须真的带回窗口数据，否则说明这个端口是别的服务
            return dto?.Windows is { Count: > 0 }
                ? new FetchResult(FetchStatus.Ok, dto)
                : new FetchResult(FetchStatus.NotReachable, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            // 端口不是代理、超时、返回的不是预期 JSON，都只当作这个候选不可用
            return new FetchResult(FetchStatus.NotReachable, null);
        }
    }

    private static string CacheFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmniDock",
        "endpoint.txt");

    private static Uri? ReadCachedEndpoint()
    {
        try
        {
            var path = CacheFilePath;
            if (!File.Exists(path))
            {
                return null;
            }

            return Uri.TryCreate(File.ReadAllText(path).Trim(), UriKind.Absolute, out var uri) ? uri : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void SaveCachedEndpoint(Uri endpoint)
    {
        try
        {
            var path = CacheFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, endpoint.AbsoluteUri);
        }
        catch (IOException)
        {
            // 缓存只是加速，写不进去不影响功能
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _probe.Dispose();
        _upstream.Dispose();
    }
}
