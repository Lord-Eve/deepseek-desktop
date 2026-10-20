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
Console.WriteLine($"PASS: {assertions} core checks");
