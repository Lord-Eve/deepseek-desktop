using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeepSeekDesktop.Core;

public sealed class Account
{
    public string Provider { get; set; } = Providers.DeepSeek;
    public string RefreshToken { get; set; } = "";
    public string Name { get; set; } = "我的 DeepSeek";
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public override string ToString() => $"{(Enabled ? "●" : "○")}  {Name}" + (Provider == Providers.DeepSeek ? $"  ·  {Login}" : "  ·  清言");
}

public static class Providers
{
    public const string DeepSeek = "deepseek", Qingyan = "qingyan";
}

public sealed class Settings
{
    public string Provider { get; set; } = Providers.DeepSeek;
    public string ProviderName => Provider == Providers.Qingyan ? "智谱清言" : "DeepSeek";
    public string Model => Provider == Providers.Qingyan ? "qingyan-default" : "deepseek-default";
    public int Port { get; set; } = 22217;
    public string ApiKey { get; set; } = NewKey();
    public int HourlyQuota { get; set; } = 10;
    public bool SearchEnabled { get; set; }
    public string ProxyUrl { get; set; } = "";
    public List<Account> Accounts { get; set; } = [];
    public string BaseUrl => $"http://127.0.0.1:{Port}/v1";
    public static string NewKey() => "sk-local-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public void Validate(bool requireAccount = true)
    {
        if (Port is < 1024 or > 65535) throw new ArgumentException("端口必须在 1024–65535 之间。");
        if (ApiKey.Length < 24 || ApiKey.Any(c => c > 126 || c < 33)) throw new ArgumentException("API Key 至少 24 位，且只允许不含空格的 ASCII 字符。");
        if (HourlyQuota is < 1 or > 1000) throw new ArgumentException("每账号每小时请求上限须在 1–1000 之间。");
        if (ProxyUrl.Length > 0 && (!Uri.TryCreate(ProxyUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "socks5"))) throw new ArgumentException("代理地址应以 http://、https:// 或 socks5:// 开头。");
        if (Provider is not (Providers.DeepSeek or Providers.Qingyan) || Accounts.Any(a => a.Provider is not (Providers.DeepSeek or Providers.Qingyan))) throw new ArgumentException("不支持的服务平台。");
        var enabled = Accounts.Where(a => a.Enabled && a.Provider == Provider).ToList();
        if (requireAccount && enabled.Count == 0) throw new ArgumentException($"请先添加并启用一个{ProviderName}账号。");
        if (Provider == Providers.Qingyan)
        {
            foreach (var a in enabled)
                if (a.RefreshToken.Length < 16 || a.RefreshToken.Any(c => c < 33 || c > 126)) throw new ArgumentException($"账号「{a.Name}」需要有效的清言 refresh token（不含空格的 ASCII 字符）。");
            if (enabled.Select(a => a.RefreshToken).Distinct().Count() != enabled.Count) throw new ArgumentException("启用的清言账号中存在重复 Token。");
            return;
        }
        foreach (var a in enabled)
        {
            if (string.IsNullOrWhiteSpace(a.Login) || string.IsNullOrWhiteSpace(a.Password) || string.IsNullOrWhiteSpace(a.DeviceId)) throw new ArgumentException($"账号「{a.Name}」需要登录名、密码和真实 device_id。");
            if (!a.Login.Contains('@') && (a.Login.Trim().Length != 11 || !a.Login.Trim().All(char.IsAsciiDigit))) throw new ArgumentException("手机号请填写不含 +86 的 11 位中国大陆手机号；也可使用邮箱登录。");
        }
        if (enabled.Select(a => a.Login.Trim().ToLowerInvariant()).Distinct().Count() != enabled.Count) throw new ArgumentException("启用的账号中存在重复登录名。");
        if (enabled.Select(a => a.DeviceId).Distinct().Count() != enabled.Count) throw new ArgumentException("每个启用账号须使用独立的真实 device_id。");
    }
}

public static class EngineConfig
{
    // TOML basic strings, including literal Unicode; JSON surrogate escapes are not TOML Unicode scalars.
    public static string Quote(string value)
    {
        var b = new StringBuilder("\"");
        foreach (var c in value)
            b.Append(c switch { '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ when c < 32 || c == 127 => "\\u" + ((int)c).ToString("X4"), _ => c.ToString() });
        return b.Append('"').ToString();
    }

    public static string Render(Settings settings, string adminSecret)
    {
        settings.Validate(false);
        var b = new StringBuilder();
        b.AppendLine("[server]\nhost = \"127.0.0.1\"");
        b.AppendLine($"port = {settings.Port}\ncors_origins = []");
        b.AppendLine($"\n[[api_keys]]\nkey = {Quote(settings.ApiKey)}\ndescription = \"DeepSeek Desktop\"");
        // Disable web password login and first-run setup. Native UI uses a per-launch random JWT secret.
        b.AppendLine($"\n[admin]\npassword_hash = \"!desktop-managed-disabled\"\njwt_secret = {Quote(adminSecret)}\njwt_issued_at = 0");
        b.AppendLine("\n[ds_core]\nmodel_types = [\"default\"]\nmodel_aliases = [\"deepseek-chat\"]");
        b.AppendLine($"hourly_request_quota = {settings.HourlyQuota}\ndefault_search_enabled = {settings.SearchEnabled.ToString().ToLowerInvariant()}");
        foreach (var a in settings.Accounts.Where(a => a.Enabled && a.Provider == Providers.DeepSeek))
        {
            var isEmail = a.Login.Contains('@');
            b.AppendLine("\n[[ds_core.accounts]]");
            b.AppendLine($"email = {Quote(isEmail ? a.Login.Trim() : "")}\nmobile = {Quote(isEmail ? "" : a.Login.Trim())}\narea_code = {Quote(isEmail ? "" : "+86")}");
            b.AppendLine($"password = {Quote(a.Password)}\ndevice_id = {Quote(a.DeviceId)}");
        }
        if (settings.ProxyUrl.Length > 0) b.AppendLine($"\n[proxy]\nurl = {Quote(settings.ProxyUrl)}");
        return b.ToString();
    }
}

public static class AdminToken
{
    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Create(string secret)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var head = B64(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var body = B64(JsonSerializer.SerializeToUtf8Bytes(new { sub = "admin", iat = now, exp = now + 3600 }));
        var message = head + "." + body;
        return message + "." + B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message)));
    }
}

public static class LogRedactor
{
    public static string Redact(string line, Settings settings, string adminSecret)
    {
        var secrets = settings.Accounts.SelectMany(a => new[] { a.Password, a.DeviceId, a.Login, a.RefreshToken })
            .Concat(new[] { settings.ApiKey, settings.ProxyUrl, adminSecret }).Where(s => s.Length > 0).OrderByDescending(s => s.Length);
        foreach (var secret in secrets) line = line.Replace(secret, "[已隐藏]", StringComparison.Ordinal);
        line = System.Text.RegularExpressions.Regex.Replace(line, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        // Avoid retaining upstream authorization material not present in the saved settings.
        if (System.Text.RegularExpressions.Regex.IsMatch(line, @"(?i)(bearer\s|access_token|refresh_token|authorization\s*[:=]|jwt_secret)")) return "[后端日志含认证字段，已隐藏整行]";
        return line;
    }
}
