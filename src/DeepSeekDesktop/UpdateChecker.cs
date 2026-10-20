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
    public static async Task<DesktopRelease> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo("gh.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in new[] { "api", "--hostname", "github.com", "repos/Lord-Eve/deepseek-desktop/releases?per_page=100" })
            info.ArgumentList.Add(argument);
        info.Environment["GH_PROMPT_DISABLED"] = "1";
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("自动检查需要安装并登录 GitHub CLI；也可点击“前往下载页”查看新版。");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("未能检查更新，请确认网络及 GitHub CLI 的仓库访问权限，或前往下载页查看。");
            return UpdateFeed.Latest(await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("检查更新超时，请稍后重试或前往下载页查看。");
        }
        finally
        {
            if (!process.HasExited)
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
        }
    }
}
