using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeepSeekDesktop.Core;

namespace DeepSeekDesktop;

public static class UpdateChecker
{
    public static async Task<DesktopRelease> CheckAsync(CancellationToken cancellationToken = default, string proxyUrl = "")
    {
        string json = await RunGhAsync(new[] { "api", "--hostname", "github.com", "repos/Lord-Eve/deepseek-desktop/releases?per_page=100" }, TimeSpan.FromSeconds(15), cancellationToken, proxyUrl);
        return UpdateFeed.Latest(json);
    }
    // gh honours the standard proxy variables; the configured proxy replaces whatever the app inherited,
    // so a transparent proxy that stalls GitHub downloads can be bypassed from the settings page.
    internal static void ApplyProxy(ProcessStartInfo info, string proxyUrl)
    {
        if (proxyUrl.Length == 0) return;
        foreach (string name in new[] { "HTTPS_PROXY", "HTTP_PROXY", "https_proxy", "http_proxy" }) info.Environment[name] = proxyUrl;
        info.Environment.Remove("ALL_PROXY"); info.Environment.Remove("all_proxy");
        info.Environment["NO_PROXY"] = "localhost,127.0.0.1,::1";
    }
    internal static async Task<string> RunGhAsync(string[] arguments, TimeSpan limit, CancellationToken cancellationToken, string proxyUrl = "")
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo("gh.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        info.Environment["GH_PROMPT_DISABLED"] = "1";
        ApplyProxy(info, proxyUrl);
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("私有仓库更新需要本机已安装并登录 GitHub CLI；也可打开发布页手动更新。");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("更新操作失败，请确认网络及 GitHub CLI 的仓库访问权限，稍后重试。");
            return await output.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("更新操作超时，请稍后重试。");
        }
        finally
        {
            if (!process.HasExited)
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
        }
    }
}
