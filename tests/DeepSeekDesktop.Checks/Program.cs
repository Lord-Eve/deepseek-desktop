using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeepSeekDesktop.Core;

int assertions = 0;
void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
void Invalid(Action action, string message) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, message); }

var s = new Settings();
Check(s.ApiKey.Length >= 48 && s.ApiKey != Settings.NewKey(), "Generated keys must be independent.");
Invalid(() => s.Validate(), "Starting with no enabled accounts must fail.");
s.Validate(false);
s.Accounts.Add(new Account { Name = "Fixture", Login = "fixture@example.test", Password = "test-quote\"\\\n密码🐳", DeviceId = "fixture-device-only-not-real" });
s.Validate();
var config = EngineConfig.Render(s, "fixture-admin-secret");
Check(config.Contains("host = \"127.0.0.1\""), "Only loopback may be exposed.");
Check(config.Contains("cors_origins = []"), "Untrusted browser origins must not be enabled.");
Check(config.Contains("password_hash = \"!desktop-managed-disabled\""), "Web setup must be locked before listening.");
Check(config.Contains("密码🐳"), "Unicode scalars must remain valid TOML.");
Check(EngineConfig.Quote("\"\n[server]\nhost=\"0.0.0.0\"\\") == "\"\\\"\\n[server]\\nhost=\\\"0.0.0.0\\\"\\\\\"", "Strings must not inject TOML tables.");
Check(EngineConfig.Quote("\0\u007f\b\t") == "\"\\u0000\\u007F\\u0008\\t\"", "Control characters must be escaped.");
var originalPort = s.Port; s.Port = 80; Invalid(() => s.Validate(), "Privileged ports must be rejected."); s.Port = originalPort;
s.Accounts.Add(new Account { Name = "Duplicate", Login = "FIXTURE@EXAMPLE.TEST", Password = "different", DeviceId = "other-device" });
Invalid(() => s.Validate(), "Duplicate enabled logins must fail."); s.Accounts.RemoveAt(1);
s.Accounts.Add(new Account { Login = "13800138000", Password = "other-password", DeviceId = s.Accounts[0].DeviceId });
Invalid(() => s.Validate(), "Reusing an enabled device ID must fail.");
s.Accounts[1].Enabled = false; s.Validate();
Check(!EngineConfig.Render(s, "fixture-admin-secret").Contains("13800138000"), "Disabled accounts must not reach the engine.");
var before = s.ApiKey; s.ApiKey = "x\n"; Invalid(() => s.Validate(), "API keys must not inject headers."); s.ApiKey = before;
s.ProxyUrl = "file:///tmp/test"; Invalid(() => s.Validate(), "Non-network proxies must fail."); s.ProxyUrl = "";
var redacted = LogRedactor.Redact($"{s.Accounts[0].Password} {s.Accounts[0].Login} {s.ApiKey} fixture-admin-secret", s, "fixture-admin-secret");
Check(!redacted.Contains(s.ApiKey) && !redacted.Contains(s.Accounts[0].Password) && !redacted.Contains("fixture@example.test"), "Logs must hide saved credentials.");
Check(LogRedactor.Redact("authorization: Bearer not-in-settings", s, "fixture-admin-secret").Contains("隐藏整行"), "Unknown upstream tokens must also be hidden.");
string jwt = AdminToken.Create("fixture-admin-secret");
var parts = jwt.Split('.');
string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
Check(parts[2] == B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes("fixture-admin-secret"), Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]))), "Admin JWT signatures must match engine HMAC verification.");
var bytes = Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/').PadRight((parts[1].Length + 3) / 4 * 4, '='));
using var payload = JsonDocument.Parse(bytes);
Check(payload.RootElement.GetProperty("exp").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "Admin JWT must be current.");
if (args.Length == 2 && args[0] == "--fixture")
{
    s.ApiKey = "sk-local-fixture-only-01234567890123456789";
    File.WriteAllText(args[1], EngineConfig.Render(s, "fixture-admin-secret"), new UTF8Encoding(false));
}
Check(JsonSerializer.Deserialize<Settings>("""{"Port":22217}""")!.ServiceDesired == null, "Settings saved by older versions must load with no recorded service state.");
Check(JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { ServiceDesired = true }))!.ServiceDesired == true, "The remembered service state must survive saving.");
Check(ServiceLaunch.ShouldStart(true, false, false), "A remembered running service must start on launch.");
Check(!ServiceLaunch.ShouldStart(false, false, false) && !ServiceLaunch.ShouldStart(null, false, false), "A stopped or never-started service must stay stopped.");
Check(ServiceLaunch.ShouldStart(false, true, false) && ServiceLaunch.ShouldStart(null, true, false), "--start-service must override the saved state.");
Check(ServiceLaunch.ShouldStart(false, false, true), "An update must restore a service that was running.");
Check(ServiceLaunch.AfterUpdateResume(null, true) == true && ServiceLaunch.AfterUpdateResume(null, false) == null, "Updating from a version without the field must record only a running service.");
Check(ServiceLaunch.AfterUpdateResume(false, true) == false && ServiceLaunch.AfterUpdateResume(true, false) == true, "An update must not overwrite a recorded choice.");
Check(ServiceLaunch.Has(["--tray", "--START-SERVICE"], ServiceLaunch.StartServiceArgument) && !ServiceLaunch.Has(["--tray"], ServiceLaunch.StartServiceArgument), "Launch arguments must match exactly, ignoring case.");
using var unavailable = JsonDocument.Parse("""{"idle":0,"busy":0,"error":0,"invalid":1}""");
Check(!PoolStatus.Parse(unavailable.RootElement).HasUsableAccount, "Listening with an invalid account must not imply chat readiness.");
using var busyPool = JsonDocument.Parse("""{"idle":0,"busy":1,"error":0,"invalid":0}""");
Check(PoolStatus.Parse(busyPool.RootElement).HasUsableAccount, "Busy accounts must not be classified as failed.");
using var partialPool = JsonDocument.Parse("""{"idle":1,"busy":0,"error":1,"invalid":0}""");
Check(PoolStatus.Parse(partialPool.RootElement).HasFailures && PoolStatus.Parse(partialPool.RootElement).HasUsableAccount, "Partial account failures must remain visible.");
bool rejectedMissing = false;
using var missingPool = JsonDocument.Parse("{}");
try { PoolStatus.Parse(missingPool.RootElement); } catch (KeyNotFoundException) { rejectedMissing = true; }
Check(rejectedMissing, "Missing status data must not be silently interpreted as zero.");
string Release(string tag, bool draft = false, bool package = true) => JsonSerializer.Serialize(new
{
    tag_name = tag, draft, prerelease = true, html_url = "https://untrusted.example/",
    assets = package ? new[] { new { name = $"DeepSeekDesktop-{tag[1..]}-win-x64.zip" } } : []
});
var latest = UpdateFeed.Latest("[" + string.Join(',', Release("v0.2.9"), Release("v0.2.10"), Release("v9.0.0", draft: true), Release("v8.0.0", package: false), Release("vbad")) + "]");
Check(latest.Version == new Version(0, 2, 10), "Update checks must compare numeric versions and skip drafts, missing packages and malformed tags.");
Check(latest.PageUrl == UpdateFeed.ReleasesUrl + "/tag/v0.2.10", "Update links must stay on the project's trusted GitHub release page.");
Check(UpdateFeed.Latest("[" + Release("v0.1.3") + "]").Version == new Version(0, 1, 3), "Published preview releases must be discoverable.");
bool missingRelease = false;
try { _ = UpdateFeed.Latest("[" + Release("v9.0.0", draft: true) + "]"); } catch (InvalidDataException) { missingRelease = true; }
Check(missingRelease, "An empty compatible release feed must not claim the app is up to date.");
bool invalidFeed = false;
try { _ = UpdateFeed.Latest("{}"); } catch (InvalidDataException) { invalidFeed = true; }
Check(invalidFeed, "An API error object must not be interpreted as a release list.");
var migrated = JsonSerializer.Deserialize<Settings>("""{"Port":22217,"ApiKey":"sk-local-previous-0123456789012345","Accounts":[{"Login":"old@example.test","Password":"old-password","DeviceId":"old-device"}]}""")!;
Check(migrated.Provider == Providers.DeepSeek && migrated.Accounts[0].Provider == Providers.DeepSeek, "Old settings without provider fields must remain DeepSeek settings.");
migrated.Accounts.Add(new Account { Provider = Providers.Qingyan, RefreshToken = "fixture-qingyan-refresh-0123456789" });
var previousKey = migrated.ApiKey;
migrated.Provider = Providers.Qingyan; migrated.Validate();
Check(migrated.ApiKey == previousKey && migrated.Accounts[0].Password == "old-password", "Switching platform must preserve the shared key and other platform credentials.");
Check(!EngineConfig.Render(migrated, "fixture-admin-secret").Contains("fixture-qingyan-refresh"), "Qingyan tokens must never enter the Rust engine configuration.");
Check(!LogRedactor.Redact("credential=" + migrated.Accounts[1].RefreshToken, migrated, "").Contains("fixture-qingyan-refresh"), "Qingyan refresh tokens must be redacted even without an auth field name.");
migrated.Accounts[1].RefreshToken = "token\r\ninjection";
Invalid(() => migrated.Validate(), "Qingyan credentials must not inject HTTP headers.");
var grok = new Settings { Provider = Providers.Grok, Accounts = [new Account { Provider = Providers.Grok, AccessToken = "fixture-access-0123456789", RefreshToken = "fixture-refresh-0123456789", TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }] };
grok.Validate();
Check(grok.ProviderName == "Grok OAuth" && grok.Model == "grok-build", "The Grok platform must expose its own display name and default model.");
Check(!EngineConfig.Render(grok, "fixture-admin").Contains("fixture-refresh") && !EngineConfig.Render(grok, "fixture-admin").Contains("fixture-access"), "Grok credentials must never enter the Rust engine config.");
Check(!LogRedactor.Redact("credential=" + grok.Accounts[0].AccessToken, grok, "").Contains("fixture-access"), "Grok OAuth access tokens must be redacted even without an auth label.");
var serialized = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(grok))!;
Check(serialized.Accounts[0].Id == grok.Accounts[0].Id && serialized.Accounts[0].TokenExpiresAt == grok.Accounts[0].TokenExpiresAt, "Account IDs and OAuth expiry must remain stable through settings serialization.");
grok.Accounts.Add(new Account { Provider = Providers.Grok, RefreshToken = "fixture-other-refresh-0123456789" });
Invalid(() => grok.Validate(), "The initial Grok adapter must not mix concurrent active accounts and replay contexts.");
grok.Accounts[1].Enabled = false; grok.Validate();
grok.Accounts[0].AccessToken = "injected\r\nheader"; Invalid(() => grok.Validate(), "Access tokens must not inject headers.");
foreach (string provider in new[] { Providers.CodeBuddy, Providers.WorkBuddy })
{
    var buddy = new Settings { Provider = provider, Accounts = [new Account { Provider = provider, Name = "Buddy fixture", AccessToken = "fixture-buddy-access-0123456789", RefreshToken = "fixture-buddy-refresh-0123456789", TokenExpiresAt = 1234567890, BuddyUserId = "fixture-buddy-user", BuddyDomain = provider == Providers.CodeBuddy ? "www.codebuddy.cn" : "www.workbuddy.ai", BuddyAccountType = "personal", BuddyRefreshExpiresAt = 2234567890 }] };
    buddy.Validate();
    Check(buddy.Model == "deepseek-v4.1-flash" && buddy.ProviderName.Contains(provider == Providers.CodeBuddy ? "国内" : "国际"), "Buddy regions must have distinct identities and their own default models.");
    var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(buddy))!;
    Check(restored.Accounts[0].BuddyUserId == "fixture-buddy-user" && restored.Accounts[0].BuddyRefreshExpiresAt == 2234567890 && restored.Accounts[0].Id == buddy.Accounts[0].Id, "Buddy account metadata and expiry must survive settings serialization.");
    Check(!EngineConfig.Render(buddy, "fixture-admin").Contains("fixture-buddy"), "Buddy credentials must not enter the Rust engine configuration.");
    Check(!LogRedactor.Redact("credential=fixture-buddy-access-0123456789 user=fixture-buddy-user", buddy, "").Contains("fixture-buddy"), "Buddy access tokens and user IDs must be hidden in logs.");
    buddy.Accounts.Add(new Account { Provider = provider, RefreshToken = "fixture-second-refresh-0123456789" });
    Invalid(() => buddy.Validate(), "The Buddy adapter must not mix simultaneously enabled accounts.");
    buddy.Accounts[1].Enabled = false; buddy.Validate();
    buddy.Accounts[0].BuddyDomain = provider == Providers.CodeBuddy ? "www.workbuddy.ai" : "www.codebuddy.cn";
    Invalid(() => buddy.Validate(), "Buddy authorization metadata must not cross regions.");
    buddy.Accounts[0].BuddyDomain = ""; buddy.Accounts[0].BuddyUserId = "injected\r\nheader";
    Invalid(() => buddy.Validate(), "Buddy metadata must not inject HTTP headers.");
}
var regional = new Settings { Provider = Providers.CodeBuddy, CodeBuddyModel = "deepseek-v4.1-flash", WorkBuddyModel = "deepseek-v4.1-flash-sg" };
foreach (var domainCase in new[]
{
    (Provider: Providers.CodeBuddy, Host: "copilot.tencent.com", Login: true, Credential: true),
    (Provider: Providers.CodeBuddy, Host: "www.codebuddy.cn", Login: true, Credential: true),
    (Provider: Providers.CodeBuddy, Host: "codebuddy.cn", Login: true, Credential: true),
    (Provider: Providers.CodeBuddy, Host: "www.workbuddy.cn", Login: true, Credential: true),
    (Provider: Providers.CodeBuddy, Host: "WORKBUDDY.CN", Login: true, Credential: true),
    (Provider: Providers.WorkBuddy, Host: "www.workbuddy.ai", Login: true, Credential: true),
    (Provider: Providers.WorkBuddy, Host: "WORKBUDDY.AI", Login: true, Credential: true),
    (Provider: Providers.WorkBuddy, Host: "www.codebuddy.ai", Login: true, Credential: true),
    (Provider: Providers.WorkBuddy, Host: "codebuddy.ai", Login: true, Credential: true),
    (Provider: Providers.WorkBuddy, Host: "auth.workbuddy.ai", Login: true, Credential: true),
    (Provider: Providers.WorkBuddy, Host: "API.WORKBUDDY.AI", Login: false, Credential: true),
    (Provider: Providers.CodeBuddy, Host: "www.workbuddy.ai", Login: false, Credential: false),
    (Provider: Providers.WorkBuddy, Host: "www.codebuddy.cn", Login: false, Credential: false),
    (Provider: Providers.WorkBuddy, Host: "www.workbuddy.ai.example.test", Login: false, Credential: false),
    (Provider: Providers.CodeBuddy, Host: "evilcodebuddy.cn", Login: false, Credential: false),
    (Provider: Providers.WorkBuddy, Host: "https://www.workbuddy.ai", Login: false, Credential: false),
    (Provider: Providers.Grok, Host: "www.workbuddy.ai", Login: false, Credential: false)
})
{
    Check(Providers.IsBuddyLoginHost(domainCase.Provider, domainCase.Host) == domainCase.Login, "Buddy browser authorization hosts must use exact, regional, case-insensitive allowlists.");
    Check(Providers.IsBuddyCredentialDomain(domainCase.Provider, domainCase.Host) == domainCase.Credential, "Buddy credential realms must remain distinct from browser destinations.");
    if (!Providers.IsBuddy(domainCase.Provider)) continue;
    var domainSettings = new Settings { Provider = domainCase.Provider, Accounts = [new Account { Provider = domainCase.Provider, RefreshToken = "fixture-domain-refresh-0123456789", BuddyDomain = domainCase.Host }] };
    if (domainCase.Credential) domainSettings.Validate();
    else Invalid(() => domainSettings.Validate(), "Settings validation must use the same regional credential-realm rules as authentication.");
}
regional.Validate(false); regional.Provider = Providers.WorkBuddy;
Check(regional.Model == "deepseek-v4.1-flash-sg" && regional.CodeBuddyModel == "deepseek-v4.1-flash", "Changing Buddy regions must preserve separate default models.");
regional.WorkBuddyModel = "model\r\ninvalid"; Invalid(() => regional.Validate(false), "Buddy model IDs must reject header and request injection.");
Console.WriteLine($"PASS: {assertions} core checks");
