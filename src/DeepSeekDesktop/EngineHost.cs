using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeepSeekDesktop.Core;
using DeepSeekDesktop.Gateway;

namespace DeepSeekDesktop;

internal sealed class EngineHost : IDisposable
{
    private const string EngineHash = "70a4dbe9a4d168c58f95e13b12e3c9bf7cb79801fec49b0b9d64f89cb4deb580";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
    private Process? process;
    private bool spawned;
    private ProcessJob? job;
    private CancellationTokenSource? starting;
    private string? runDirectory;
    private string secret = "";
    private Settings? active;
    private QingyanGateway? qingyan;
    private GrokGateway? grok;
    private BuddyGateway? buddy;
    public bool IsRunning => buddy?.IsRunning == true || grok?.IsRunning == true || qingyan?.IsRunning == true || spawned && process is { HasExited: false };
    public bool IsReady { get; private set; }
    public string? BaseUrl => active?.BaseUrl;
    public event Action<string>? Log;
    public event Action<string, string>? RefreshTokenChanged;
    public event Action<string, GrokTokens>? GrokTokensChanged;
    public event Action<string, BuddyTokens>? BuddyTokensChanged;

    public async Task StartAsync(Settings settings)
    {
        await gate.WaitAsync();
        try
        {
            if (IsRunning) throw new InvalidOperationException("服务已在运行。");
            await CleanupAsync();
            settings.Validate();
            if (settings.Provider is Providers.CodeBuddy or Providers.WorkBuddy)
            {
                active = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
                starting = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                buddy = new BuddyGateway(active, (id, tokens) => BuddyTokensChanged?.Invoke(id, tokens), line => Log?.Invoke(line));
                await buddy.StartAsync(starting.Token); IsReady = true; return;
            }
            if (settings.Provider == Providers.Grok)
            {
                active = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
                starting = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                grok = new GrokGateway(active, (id, tokens) => GrokTokensChanged?.Invoke(id, tokens), line => Log?.Invoke(line));
                await grok.StartAsync(starting.Token); IsReady = true; return;
            }
            if (settings.Provider == Providers.Qingyan)
            {
                active = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
                starting = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                qingyan = new QingyanGateway(active, (old, next) => RefreshTokenChanged?.Invoke(old, next), line => Log?.Invoke(line));
                await qingyan.StartAsync(starting.Token);
                IsReady = true;
                return;
            }
            var path = Path.Combine(AppContext.BaseDirectory, "engine", "ds-free-api.exe");
            if (!File.Exists(path)) throw new FileNotFoundException("缺少协议引擎，请使用完整发布包，或运行 tools/prepare-engine.ps1。", path);
            using (var stream = File.OpenRead(path))
                if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(EngineHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("协议引擎校验失败，请重新解压完整发布包。");
            // Fail before writing credentials or logging into upstream when the selected port is occupied.
            var listener = new TcpListener(IPAddress.Loopback, settings.Port);
            try { listener.Start(); } catch (SocketException) { throw new IOException($"端口 {settings.Port} 已被占用，请在设置中换一个端口。"); }
            finally { listener.Stop(); }
            active = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
            secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            starting = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            runDirectory = Path.Combine(PrivateStorage.Root, "runtime", Guid.NewGuid().ToString("N"));
            PrivateStorage.SecureDirectory(runDirectory);
            var configPath = Path.Combine(runDirectory, "config.toml");
            await File.WriteAllTextAsync(configPath, EngineConfig.Render(active, secret), new UTF8Encoding(false));
            var info = new ProcessStartInfo(path) { WorkingDirectory = runDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            info.ArgumentList.Add("-c"); info.ArgumentList.Add(configPath);
            info.Environment["DS_DATA_DIR"] = runDirectory;
            info.Environment["RUST_LOG"] = "info";
            job = new ProcessJob();
            process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) => OnEngineLog(e.Data);
            process.ErrorDataReceived += (_, e) => OnEngineLog(e.Data);
            if (!process.Start()) throw new IOException("无法启动协议引擎。");
            spawned = true;
            job.Assign(process);
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            Log?.Invoke("协议引擎启动中：将登录启用账号，并由上游引擎发送健康检查消息。");
            while (!starting.IsCancellationRequested)
            {
                if (process.HasExited) throw new IOException($"协议引擎已退出（代码 {process.ExitCode}）。请查看日志中的登录或网络错误。");
                try
                {
                    using var request = Request(HttpMethod.Get, "/v1/models");
                    using var response = await http.SendAsync(request, starting.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(starting.Token));
                        if (body.RootElement.TryGetProperty("data", out var models) && models.ValueKind == JsonValueKind.Array && models.GetArrayLength() > 0)
                        { IsReady = true; Log?.Invoke($"本地 API 已就绪：{active.BaseUrl}"); return; }
                    }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!starting.IsCancellationRequested) { }
                await Task.Delay(500, starting.Token);
            }
            starting.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { await CleanupAsync(); throw new IOException("启动已取消或超过 3 分钟，请检查账号登录信息和网络后重试。"); }
        catch { await CleanupAsync(); throw; }
        finally { starting?.Dispose(); starting = null; gate.Release(); }
    }

    public async Task StopAsync()
    {
        starting?.Cancel();
        await gate.WaitAsync();
        try { await CleanupAsync(); } finally { gate.Release(); }
    }

    private async Task CleanupAsync()
    {
        IsReady = false;
        if (buddy != null) { await buddy.DisposeAsync(); buddy = null; }
        if (grok != null) { await grok.DisposeAsync(); grok = null; }
        if (qingyan != null) { await qingyan.DisposeAsync(); qingyan = null; }
        if (process != null)
        {
            try
            {
                if (spawned && !process.HasExited)
                {
                    process.Kill(true);
                    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    try { await process.WaitForExitAsync(wait.Token); } catch (OperationCanceledException) { }
                }
            }
            catch (System.ComponentModel.Win32Exception) { Log?.Invoke("直接停止进程失败，将通过 Windows 托管对象结束后端。"); }
            finally { job?.Dispose(); job = null; process.Dispose(); process = null; spawned = false; }
        }
        job?.Dispose(); job = null;
        try { PrivateStorage.CleanRuntime(runDirectory); runDirectory = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log?.Invoke("运行文件暂未清除，将在下次启动时清理。目录已限制为当前 Windows 用户可访问。"); }
        active = null;
        secret = "";
    }

    private void OnEngineLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || active == null) return;
        if (line.Contains("admin panel:", StringComparison.OrdinalIgnoreCase)) return;
        Log?.Invoke(LogRedactor.Redact(line, active, secret));
    }

    private HttpRequestMessage Request(HttpMethod method, string path, bool admin = false)
    {
        if (active == null) throw new InvalidOperationException("请先启动本地服务。");
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{active.Port}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin ? AdminToken.Create(secret) : active.ApiKey);
        return request;
    }

    public async Task<string[]> ModelsAsync()
    {
        using var request = Request(HttpMethod.Get, "/v1/models");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).ToArray();
    }

    public async Task<JsonElement> AdminAsync(string resource)
    {
        if (resource is not ("stats" or "status")) throw new ArgumentException("不支持的状态资源。");
        if (buddy != null) return buddy.Status(resource);
        if (grok != null) return grok.Status(resource);
        if (qingyan != null) return qingyan.Status(resource);
        using var request = Request(HttpMethod.Get, "/admin/api/" + resource, true);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    public async Task<string> SendTestAsync(CancellationToken cancellation)
    {
        using var request = Request(HttpMethod.Post, "/v1/chat/completions");
        request.Content = JsonContent.Create(TestRequest(active!));
        // The short timeout on status requests must not cut off an actual model completion.
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };
        using var response = await client.SendAsync(request, cancellation);
        string json = await response.Content.ReadAsStringAsync(cancellation);
        if (!response.IsSuccessStatusCode)
        {
            string detail = "请查看运行日志。";
            if (active!.Provider is Providers.Grok or Providers.CodeBuddy or Providers.WorkBuddy)
            {
                try
                {
                    using var error = JsonDocument.Parse(json);
                    if (error.RootElement.TryGetProperty("error", out var value) && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                        detail = message.GetString()![..Math.Min(message.GetString()!.Length, 500)];
                }
                catch (JsonException) { }
            }
            throw new IOException($"测试失败：HTTP {(int)response.StatusCode}。{detail}");
        }
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "（返回内容为空）";
    }

    internal static object TestRequest(Settings settings)
    {
        var request = new Dictionary<string, object>
        {
            ["model"] = settings.Model, ["messages"] = new[] { new { role = "user", content = "请仅回复：连接成功" } },
            ["stream"] = false, ["web_search_options"] = new { search_context_size = "none" }
        };
        // Buddy's catalog decides the default: some non-DeepSeek models cannot disable reasoning.
        if (!Providers.IsBuddy(settings.Provider)) request["reasoning_effort"] = settings.Provider == Providers.Grok ? "low" : "none";
        return request;
    }

    public void Dispose()
    {
        starting?.Cancel();
        job?.Dispose();
        process?.Dispose();
        http.Dispose();
    }
}
