using System.Diagnostics;
using System.Security.AccessControl;
using System.Text;
using DeepSeekDesktop;
using DeepSeekDesktop.Core;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("These checks require Windows.");
var root = Path.Combine(Path.GetTempPath(), "DeepSeekDesktop-checks-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("DEEPSEEK_DESKTOP_DATA_DIR", root);
int checks = 0;
void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
try
{
    var s = new Settings();
    s.Accounts.Add(new Account { Name = "测试", Login = "fixture@example.test", Password = "fixture-password-only", DeviceId = "fixture-device-only" });
    PrivateStorage.Save(s);
    var loaded = PrivateStorage.Load();
    Check(loaded.ApiKey == s.ApiKey && loaded.Accounts[0].Password == s.Accounts[0].Password, "DPAPI must round-trip for the current user.");
    var disk = File.ReadAllBytes(PrivateStorage.SettingsPath);
    Check(!Encoding.UTF8.GetString(disk).Contains(s.ApiKey) && !Encoding.UTF8.GetString(disk).Contains(s.Accounts[0].Password), "Settings file must not contain plaintext credentials.");
    var acl = new DirectoryInfo(root).GetAccessControl();
    Check(acl.AreAccessRulesProtected, "Data directory must disable inherited permissions.");
    Check(acl.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)).Count == 1, "Data directory must grant only the current user an explicit access rule.");
    s.Provider = Providers.Qingyan;
    s.Accounts.Add(new Account { Provider = Providers.Qingyan, RefreshToken = "fixture-qingyan-original-token" });
    PrivateStorage.Save(s);
    s.Accounts[1].RefreshToken = "fixture-qingyan-rotated-token";
    PrivateStorage.Save(s);
    loaded = PrivateStorage.Load();
    Check(loaded.Provider == Providers.Qingyan && loaded.Accounts[1].RefreshToken == "fixture-qingyan-rotated-token" && loaded.Accounts[0].Password == "fixture-password-only" && loaded.ApiKey == s.ApiKey, "Encrypted token rotation must preserve other-platform accounts and the local key across reloads.");
    Check(!Encoding.UTF8.GetString(File.ReadAllBytes(PrivateStorage.SettingsPath)).Contains("fixture-qingyan-rotated-token"), "Qingyan tokens must remain encrypted on disk.");
    s.Provider = Providers.Grok;
    s.Accounts.Add(new Account { Provider = Providers.Grok, AccessToken = "fixture-grok-access-only", RefreshToken = "fixture-grok-refresh-only", TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() });
    PrivateStorage.Save(s);
    var grok = PrivateStorage.Load();
    Check(grok.Accounts[2].Id == s.Accounts[2].Id && grok.Accounts[2].AccessToken == s.Accounts[2].AccessToken && grok.Accounts[2].TokenExpiresAt == s.Accounts[2].TokenExpiresAt && grok.Accounts[1].RefreshToken == s.Accounts[1].RefreshToken, "Grok OAuth credentials must round-trip through DPAPI without changing Qingyan data.");
    Check(!Encoding.UTF8.GetString(File.ReadAllBytes(PrivateStorage.SettingsPath)).Contains("fixture-grok-access-only"), "Grok access tokens must not be plaintext on disk.");
    File.WriteAllBytes(PrivateStorage.SettingsPath, [0, 1, 2, 3]);
    bool failedClosed = false;
    try { _ = PrivateStorage.Load(); } catch (System.ComponentModel.Win32Exception) { failedClosed = true; }
    Check(failedClosed && File.ReadAllBytes(PrivateStorage.SettingsPath).Length == 4, "Unreadable settings must not be silently reset or overwritten.");
    using var process = new Process { StartInfo = new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true } };
    var job = new ProcessJob();
    try
    {
        process.Start(); job.Assign(process); job.Dispose();
        Check(process.WaitForExit(5000), "Closing the app job must terminate its engine process.");
    }
    finally { job.Dispose(); if (!process.HasExited) process.Kill(true); }
    Console.WriteLine($"PASS: {checks} native Windows checks");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}
