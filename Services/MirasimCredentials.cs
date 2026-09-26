using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OmniDock.Services;

/// <summary>
/// 从本机 Mirasim 的配置里取出账号 token（JWT），用来直连上游拿额度。
///
/// Mirasim 把账号 token 加密存在 <c>~/.mirasim/setting.json</c> 的 <c>auth.token</c>：
/// <c>"mrs1:" + base64( iv(12) ‖ tag(16) ‖ 密文 )</c>，算法 AES-256-GCM；
/// 主密钥在 <c>~/.mirasim/secret.key</c>，是 DPAPI（当前用户）保护过的十六进制串。
///
/// 两者都能被当前用户解开，所以拿 token 既不需要会话环境、也不需要本地代理在跑——
/// 这正是「直连上游」这条路比走代理更稳的原因：代理的端口和路径凭据每次重启都换，
/// 而这份配置一直在，且 Mirasim 自己续期后会把它写回同一个位置。
/// </summary>
internal static class MirasimCredentials
{
    /// <summary>密文前缀。带这个前缀的字段才是加密的，其余原样使用。</summary>
    private const string CipherPrefix = "mrs1:";

    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>Mirasim 数据目录：<c>MIRASIM_HOME</c> 优先，其次 <c>~/.mirasim</c>。</summary>
    private static string HomeDirectory
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("MIRASIM_HOME");
            return !string.IsNullOrWhiteSpace(custom)
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mirasim");
        }
    }

    private static string SettingsPath => Path.Combine(HomeDirectory, "setting.json");

    private static string SecretKeyPath => Path.Combine(HomeDirectory, "secret.key");

    // setting.json 有几十 KB，每秒刷新都重读重解太亏；按「mtime + 长度」判新旧。
    // 加锁是因为心跳和设置窗口可能同时来读。
    private static readonly Lock Gate = new();
    private static string? _cachedToken;
    private static string? _cachedStamp;
    private static string? _cachedFailure;

    /// <summary>
    /// 读账号 token（JWT）。成功返回 token；失败返回 null 并把原因写进 <paramref name="reason"/>，
    /// 调用方据此决定是继续走本地代理还是报错。
    /// </summary>
    internal static string? TryReadToken(out string? reason)
    {
        lock (Gate)
        {
            var stamp = DescribeStamp();
            if (stamp is not null && stamp == _cachedStamp)
            {
                reason = _cachedFailure;
                return _cachedToken;
            }

            _cachedStamp = stamp;
            _cachedToken = null;
            _cachedFailure = null;

            var token = ReadFresh(out var failure);
            _cachedToken = token;
            _cachedFailure = token is null ? failure : null;

            reason = _cachedFailure;
            return token;
        }
    }

    /// <summary>丢掉缓存，下次调用重新解密（token 刚过期或换了账号时用）。</summary>
    internal static void Invalidate()
    {
        lock (Gate)
        {
            _cachedStamp = null;
            _cachedToken = null;
            _cachedFailure = null;
        }
    }

    /// <summary>两个输入文件都读不到时返回 null，表示「这台机器上没有 Mirasim 配置」。</summary>
    private static string? DescribeStamp()
    {
        try
        {
            var settings = new FileInfo(SettingsPath);
            var secret = new FileInfo(SecretKeyPath);
            if (!settings.Exists || !secret.Exists)
            {
                return null;
            }

            return $"{settings.LastWriteTimeUtc.Ticks}:{settings.Length}:{secret.LastWriteTimeUtc.Ticks}:{secret.Length}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadFresh(out string? failure)
    {
        failure = null;

        try
        {
            if (!File.Exists(SettingsPath))
            {
                failure = "没有找到 Mirasim 配置";
                return null;
            }

            if (!File.Exists(SecretKeyPath))
            {
                failure = "没有找到 Mirasim 主密钥";
                return null;
            }

            var masterKey = ReadMasterKey();
            if (masterKey is null)
            {
                failure = "主密钥无法解开（换过用户或系统）";
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (!document.RootElement.TryGetProperty("auth", out var auth) ||
                auth.ValueKind != JsonValueKind.Object ||
                !auth.TryGetProperty("token", out var tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String)
            {
                failure = "配置里没有账号 token";
                return null;
            }

            var stored = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(stored))
            {
                failure = "账号 token 为空";
                return null;
            }

            var token = stored.StartsWith(CipherPrefix, StringComparison.Ordinal)
                ? Decrypt(stored, masterKey)
                : stored;

            if (string.IsNullOrWhiteSpace(token))
            {
                failure = "账号 token 解不开";
                return null;
            }

            if (!LooksLikeJwt(token))
            {
                failure = "账号 token 不是预期的形状";
                return null;
            }

            if (IsExpired(token))
            {
                failure = "账号 token 已过期";
                return null;
            }

            return token;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or CryptographicException)
        {
            failure = $"读取账号 token 失败：{e.GetType().Name}";
            return null;
        }
    }

    /// <summary>secret.key 是 DPAPI 保护的十六进制串，解开后得到主密钥的十六进制文本。</summary>
    private static byte[]? ReadMasterKey()
    {
        var protectedHex = File.ReadAllText(SecretKeyPath).Trim();
        if (protectedHex.Length == 0)
        {
            return null;
        }

        var cipher = Convert.FromHexString(protectedHex);
        var plain = Unprotect(cipher);
        if (plain is null)
        {
            return null;
        }

        // 明文是 UTF-16LE 的十六进制串（PowerShell ConvertFrom-SecureString 的形状）
        var hex = Encoding.Unicode.GetString(plain).Trim();
        if (hex.Length != 64)
        {
            return null;
        }

        var key = Convert.FromHexString(hex);
        return key.Length == 32 ? key : null;
    }

    /// <summary>DPAPI 解密，作用域是当前用户——和 Mirasim 自己调用的范围一致。</summary>
    private static byte[]? Unprotect(byte[] cipher)
    {
        var input = new DataBlob();
        var output = new DataBlob();
        var inputPtr = Marshal.AllocHGlobal(cipher.Length);

        try
        {
            Marshal.Copy(cipher, 0, inputPtr, cipher.Length);
            input.cbData = cipher.Length;
            input.pbData = inputPtr;

            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
            {
                return null;
            }

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(inputPtr);
            if (output.pbData != IntPtr.Zero)
            {
                LocalFree(output.pbData);
            }
        }
    }

    private static string? Decrypt(string stored, byte[] masterKey)
    {
        var blob = Convert.FromBase64String(stored[CipherPrefix.Length..]);
        if (blob.Length <= NonceSize + TagSize)
        {
            return null;
        }

        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(masterKey, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>三段点分的 JWT 才算数：Mirasim 的账号 token 就是这个形状。</summary>
    private static bool LooksLikeJwt(string token)
    {
        var first = token.IndexOf('.');
        return first > 0 && token.IndexOf('.', first + 1) > first + 1;
    }

    /// <summary>读 JWT 的 exp；读不出来就当没过期，交给上游判。</summary>
    private static bool IsExpired(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
            {
                return false;
            }

            var payload = Base64UrlDecode(parts[1]);
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("exp", out var exp) || exp.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            return DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()) <= DateTimeOffset.UtcNow.AddSeconds(30);
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>base64url 解码，补齐省略的填充。</summary>
    private static byte[] Base64UrlDecode(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(text.PadRight(text.Length + (4 - text.Length % 4) % 4, '='));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr dataDescr,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        ref DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);
}
