using System.IO;
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

/// <summary>
/// 从本机的 Mirasim 本地代理读取额度。
///
/// 代理只监听回环地址，对本机请求不校验凭据，所以这里不需要、也不去碰任何 token
/// （若环境里正好有 ANTHROPIC_AUTH_TOKEN 会顺带带上，纯粹为了兼容将来开启校验的情况）。
/// 端口每次启动都变，因此按「环境变量 → 上次成功的端点 → 代理进程监听的端口」依次尝试。
/// </summary>
internal sealed class LimitsClient : IDisposable
{
    private const string ProxyProcessName = "Mirasim";

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

    private Uri? _knownEndpoint;

    /// <summary>当前生效的端点，拿不到数据时为 null。</summary>
    internal Uri? Endpoint => _knownEndpoint;

    internal async Task<LimitsDto?> FetchAsync(CancellationToken ct)
    {
        // 已知端点直接用，失败了才重新发现（代理重启换端口时会走到这里）
        if (_knownEndpoint is not null)
        {
            var snapshot = await TryFetchAsync(_knownEndpoint, _http, ct).ConfigureAwait(false);
            if (snapshot is not null)
            {
                return snapshot;
            }

            _knownEndpoint = null;
        }

        foreach (var candidate in EnumerateCandidates().Distinct())
        {
            var snapshot = await TryFetchAsync(candidate, _probe, ct).ConfigureAwait(false);
            if (snapshot is null)
            {
                continue;
            }

            _knownEndpoint = candidate;
            SaveCachedEndpoint(candidate);
            return snapshot;
        }

        return null;
    }

    private static IEnumerable<Uri> EnumerateCandidates()
    {
        // 1) 从带环境的会话里启动时，代理地址就在环境变量里
        if (Uri.TryCreate(Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL"), UriKind.Absolute, out var fromEnv))
        {
            yield return fromEnv;
        }

        // 2) 上次成功的端点，多数情况下仍然有效
        if (ReadCachedEndpoint() is { } cached)
        {
            yield return cached;
        }

        // 3) 兜底：反查代理进程正在监听的回环端口
        foreach (var port in LocalPorts.LoopbackListenersOf(ProxyProcessName))
        {
            yield return new Uri($"http://127.0.0.1:{port}");
        }
    }

    private static async Task<LimitsDto?> TryFetchAsync(Uri baseUri, HttpClient http, CancellationToken ct)
    {
        try
        {
            // baseUri 可能带路径前缀，所以按字符串拼而不是用 Uri 的相对解析
            var url = baseUri.AbsoluteUri.TrimEnd('/') + "/v1/limits";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            var token = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN");
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var dto = await response.Content.ReadFromJsonAsync<LimitsDto>(JsonOptions, ct).ConfigureAwait(false);

            // 必须真的带回窗口数据，否则说明这个端口是别的服务
            return dto?.Windows is { Count: > 0 } ? dto : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return null; // 端口不是代理、超时、返回的不是预期 JSON，都只当作这个候选不可用
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
    }
}
