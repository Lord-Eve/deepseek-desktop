using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeepSeekDesktop.Core;

public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AccessToken { get; set; } = "";
    public long TokenExpiresAt { get; set; }
    public string Provider { get; set; } = Providers.DeepSeek;
    public string RefreshToken { get; set; } = "";
    public string BuddyUserId { get; set; } = "";
    public string BuddyDomain { get; set; } = "";
    public string BuddyEnterpriseId { get; set; } = "";
    public string BuddyAccountType { get; set; } = "personal";
    public long BuddyRefreshExpiresAt { get; set; }
    public string Name { get; set; } = "我的 DeepSeek";
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public override string ToString() => $"{(Enabled ? "●" : "○")}  {Name}" + (Provider == Providers.DeepSeek ? $"  ·  {Login}" : "  ·  " + Providers.DisplayName(Provider));
}

public static class Providers
{
    public const string DeepSeek = "deepseek", Qingyan = "qingyan", Grok = "grok", CodeBuddy = "codebuddy", WorkBuddy = "workbuddy";
    private static readonly HashSet<string> CodeBuddyLoginHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "copilot.tencent.com", "www.codebuddy.cn", "codebuddy.cn", "www.workbuddy.cn", "workbuddy.cn"
    };
    private static readonly HashSet<string> WorkBuddyLoginHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "www.workbuddy.ai", "workbuddy.ai", "auth.workbuddy.ai", "www.codebuddy.ai", "codebuddy.ai"
    };
    // A token realm may be an API host without being a browser authorization destination.
    private static readonly HashSet<string> WorkBuddyAdditionalCredentialDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.workbuddy.ai"
    };
    public static bool IsBuddy(string provider) => provider is CodeBuddy or WorkBuddy;
    public static bool IsSupported(string provider) => provider is DeepSeek or Qingyan or Grok or CodeBuddy or WorkBuddy;
    public static bool IsBuddyLoginHost(string provider, string host) => provider switch
    {
        CodeBuddy => CodeBuddyLoginHosts.Contains(host), WorkBuddy => WorkBuddyLoginHosts.Contains(host), _ => false
    };
    public static bool IsBuddyCredentialDomain(string provider, string host) => IsBuddyLoginHost(provider, host)
        || provider == WorkBuddy && WorkBuddyAdditionalCredentialDomains.Contains(host);
    public static string DisplayName(string provider) => provider switch
    {
        Qingyan => "智谱清言", Grok => "Grok OAuth", CodeBuddy => "CodeBuddy（国内版）", WorkBuddy => "WorkBuddy（国际版）", _ => "DeepSeek"
    };
}

public sealed class Settings
{
    public string Provider { get; set; } = Providers.DeepSeek;
    public string GrokModel { get; set; } = "grok-build";
    public string CodeBuddyModel { get; set; } = "deepseek-v4.1-flash";
    public string WorkBuddyModel { get; set; } = "deepseek-v4.1-flash";
    public string ProviderName => Providers.DisplayName(Provider);
    public string Model => Provider switch { Providers.Qingyan => "qingyan-default", Providers.Grok => GrokModel, Providers.CodeBuddy => CodeBuddyModel, Providers.WorkBuddy => WorkBuddyModel, _ => "deepseek-default" };
    public int Port { get; set; } = 22217;
    public string ApiKey { get; set; } = NewKey();
    public int HourlyQuota { get; set; } = 10;
    public bool SearchEnabled { get; set; }
    public string ProxyUrl { get; set; } = "";
    public List<Account> Accounts { get; set; } = [];
    // True after the user starts the service, false after the user stops it; quitting and shutdown leave it as is.
    public bool? ServiceDesired { get; set; }
    public string BaseUrl => $"http://127.0.0.1:{Port}/v1";
    public static string NewKey() => "sk-local-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public void Validate(bool requireAccount = true)
    {
        if (Port is < 1024 or > 65535) throw new ArgumentException("端口必须在 1024–65535 之间。");
        if (ApiKey.Length < 24 || ApiKey.Any(c => c > 126 || c < 33)) throw new ArgumentException("API Key 至少 24 位，且只允许不含空格的 ASCII 字符。");
        if (HourlyQuota is < 1 or > 1000) throw new ArgumentException("每账号每小时请求上限须在 1–1000 之间。");
        if (ProxyUrl.Length > 0 && (!Uri.TryCreate(ProxyUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "socks5"))) throw new ArgumentException("代理地址应以 http://、https:// 或 socks5:// 开头。");
        if (!Providers.IsSupported(Provider) || Accounts.Any(a => !Providers.IsSupported(a.Provider))) throw new ArgumentException("不支持的服务平台。");
        if (string.IsNullOrWhiteSpace(GrokModel) || GrokModel.Length > 80 || !GrokModel.StartsWith("grok-", StringComparison.Ordinal) || GrokModel.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.'))) throw new ArgumentException("Grok 模型名格式无效。");
        foreach (var model in new[] { CodeBuddyModel, WorkBuddyModel })
            if (string.IsNullOrWhiteSpace(model) || model.Length > 160 || model.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_' or '/' or ':'))) throw new ArgumentException("CodeBuddy / WorkBuddy 模型名格式无效。");
        var enabled = Accounts.Where(a => a.Enabled && a.Provider == Provider).ToList();
        if (requireAccount && enabled.Count == 0) throw new ArgumentException($"请先添加并启用一个{ProviderName}账号。");
        if (Providers.IsBuddy(Provider))
        {
            if (enabled.Count > 1) throw new ArgumentException($"{ProviderName}目前每次启用一个账号，请先停用其他同平台账号。");
            foreach (var a in enabled)
            {
                if (string.IsNullOrWhiteSpace(a.Id) || a.RefreshToken.Length < 16 || a.RefreshToken.Any(c => c < 33 || c > 126) || a.AccessToken.Any(c => c < 33 || c > 126) || a.TokenExpiresAt < 0 || a.BuddyRefreshExpiresAt < 0)
                    throw new ArgumentException($"账号「{a.Name}」尚未完成有效的{ProviderName}浏览器授权。");
                if (new[] { a.BuddyUserId, a.BuddyDomain, a.BuddyEnterpriseId, a.BuddyAccountType }.Any(value => value.Length > 4096 || value.Any(c => c < 33 || c > 126))) throw new ArgumentException("CodeBuddy / WorkBuddy 账号信息格式无效，请重新登录。");
                if (a.BuddyDomain.Length > 0 && !Providers.IsBuddyCredentialDomain(Provider, a.BuddyDomain)) throw new ArgumentException("账号授权所属地区与所选平台不符，请在当前平台重新登录。");
            }
            return;
        }
        if (Provider == Providers.Grok)
        {
            if (enabled.Count > 1) throw new ArgumentException("Grok 试用适配目前每次启用一个账号，请先停用其他 Grok 账号。");
            foreach (var a in enabled)
                if (string.IsNullOrWhiteSpace(a.Id) || a.RefreshToken.Length < 16 || a.RefreshToken.Any(c => c < 33 || c > 126) || a.AccessToken.Any(c => c < 33 || c > 126) || a.TokenExpiresAt < 0) throw new ArgumentException($"账号「{a.Name}」尚未完成有效的 Grok 浏览器授权。");
            return;
        }
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
        var secrets = settings.Accounts.SelectMany(a => new[] { a.Password, a.DeviceId, a.Login, a.RefreshToken, a.AccessToken, a.BuddyUserId, a.BuddyEnterpriseId })
            .Concat(new[] { settings.ApiKey, settings.ProxyUrl, adminSecret }).Where(s => s.Length > 0).OrderByDescending(s => s.Length);
        foreach (var secret in secrets) line = line.Replace(secret, "[已隐藏]", StringComparison.Ordinal);
        line = System.Text.RegularExpressions.Regex.Replace(line, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        // Avoid retaining upstream authorization material not present in the saved settings.
        if (System.Text.RegularExpressions.Regex.IsMatch(line, @"(?i)(bearer\s|access_token|refresh_token|authorization\s*[:=]|jwt_secret)")) return "[后端日志含认证字段，已隐藏整行]";
        return line;
    }
}
