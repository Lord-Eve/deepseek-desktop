using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeepSeekDesktop.Core;

namespace DeepSeekDesktop;

internal static class UpdateService
{
    internal static string Cache => Path.Combine(PrivateStorage.Root, "updates");
    public static async Task<string> PrepareAsync(DesktopRelease release, Func<bool> resumeService, IProgress<string> progress, CancellationToken cancellation)
    {
        string install = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string session = Path.Combine(Cache, Guid.NewGuid().ToString("N"));
        UpdatePackage.ValidateInstall(install, session);
        if (UpdatePackage.Within(PrivateStorage.Root, install) || Path.GetFullPath(PrivateStorage.Root).Equals(install, StringComparison.OrdinalIgnoreCase))
            throw new IOException("用户数据目录不能位于软件目录内，请先迁移数据再更新。");
        string helper = Path.Combine(install, UpdatePackage.Updater);
        if (!File.Exists(helper)) throw new IOException("当前程序缺少更新组件，请先手动安装包含更新组件的完整版本。");
        PrivateStorage.SecureDirectory(Cache); PrivateStorage.SecureDirectory(session);
        try
        {
            // Check directory write permission before stopping a running service.
            string probe = Path.Combine(install, ".update-write-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { } File.Delete(probe);
            progress.Report("正在下载更新包，可取消；完成后将退出并重启程序…");
            await UpdateChecker.RunGhAsync(new[] { "release", "download", "v" + release.Version, "--repo", "Lord-Eve/deepseek-desktop", "--pattern", release.PackageName, "--pattern", release.PackageName + ".sha256", "--dir", session }, TimeSpan.FromMinutes(15), cancellation);
            progress.Report("下载完成，正在校验 SHA-256 并解压…");
            await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                UpdatePackage.ExtractVerified(Path.Combine(session, release.PackageName), Path.Combine(session, release.PackageName + ".sha256"), release.Version, Path.Combine(session, "staged"));
                string actual = FileVersionInfo.GetVersionInfo(Path.Combine(session, "staged", UpdatePackage.Executable)).ProductVersion?.Split('+')[0] ?? "";
                if (actual != release.Version.ToString()) throw new InvalidDataException("更新程序版本与发布版本不一致。");
            }, cancellation);
            cancellation.ThrowIfCancellationRequested();
            File.Copy(helper, Path.Combine(session, UpdatePackage.Updater));
            using var process = Process.GetCurrentProcess();
            var plan = new UpdatePlan(process.Id, process.StartTime.ToUniversalTime().Ticks, install, session, release.Version.ToString(), resumeService());
            File.WriteAllText(Path.Combine(session, "plan.json"), JsonSerializer.Serialize(plan));
            return session;
        }
        catch { try { Directory.Delete(session, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } throw; }
    }

    public static void Launch(string session)
    {
        var info = new ProcessStartInfo(Path.Combine(session, UpdatePackage.Updater)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = session };
        info.ArgumentList.Add(Path.Combine(session, "plan.json"));
        using var updater = Process.Start(info) ?? throw new IOException("未能启动更新组件。");
    }

    public static bool ConfirmRestart(string[] args)
    {
        if (args.Length != 2 || args[0] != "--update-session") return false;
        string session = Path.GetFullPath(args[1]);
        if (!string.Equals(Path.GetDirectoryName(session), Path.GetFullPath(Cache), StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(session), "N", out _)) throw new IOException("更新重启参数无效。");
        UpdatePackage.NoLinks(session);
        var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(Path.Combine(session, "plan.json"))) ?? throw new IOException("更新重启信息缺失。");
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.InstallDirectory)), Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase) || plan.Version != App.Version)
            throw new IOException("更新重启版本不匹配。");
        File.WriteAllText(Path.Combine(session, "ready"), App.Version);
        return plan.ResumeService;
    }

    public static void CleanCompleted()
    {
        if (!Directory.Exists(Cache)) return;
        UpdatePackage.NoLinks(Cache);
        foreach (string session in Directory.EnumerateDirectories(Cache))
        {
            if (!Guid.TryParseExact(Path.GetFileName(session), "N", out _) || !File.Exists(Path.Combine(session, "complete"))) continue;
            try { UpdatePackage.NoLinks(session); Directory.Delete(session, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
