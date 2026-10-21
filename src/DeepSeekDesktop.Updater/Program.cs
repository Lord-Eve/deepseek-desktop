using System.Diagnostics;
using System.Text.Json;
using DeepSeekDesktop.Core;

UpdatePlan? plan = null;
bool mayRestart = false;
try
{
    if (args.Length != 1) throw new IOException("更新参数缺失，请从主程序启动更新。");
    string planFile = Path.GetFullPath(args[0]);
    plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(planFile)) ?? throw new IOException("更新信息无效。");
    if (!string.Equals(Path.GetDirectoryName(planFile), Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(Path.GetDirectoryName(planFile), plan.SessionDirectory, StringComparison.OrdinalIgnoreCase) ||
        !System.Version.TryParse(plan.Version, out _)) throw new IOException("更新暂存位置无效。");
    UpdatePackage.ValidateInstall(plan.InstallDirectory, plan.SessionDirectory);
    // A failed or slow shutdown must leave the installed files untouched.
    Process? parent = null;
    try { parent = Process.GetProcessById(plan.ParentId); } catch (ArgumentException) { }
    if (parent != null)
    {
        using (parent)
        {
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStarted) throw new IOException("更新进程标识已失效。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await parent.WaitForExitAsync(timeout.Token);
        }
    }
    string targetExe = Path.Combine(plan.InstallDirectory, UpdatePackage.Executable);
    mayRestart = true;
    Process? restarted = null;
    try
    {
        await UpdatePackage.InstallAsync(Path.Combine(plan.SessionDirectory, "staged"), plan.InstallDirectory, Path.Combine(plan.SessionDirectory, "backup"), async () =>
        {
            var start = new ProcessStartInfo(targetExe) { UseShellExecute = false, WorkingDirectory = plan.InstallDirectory };
            start.ArgumentList.Add("--update-session"); start.ArgumentList.Add(plan.SessionDirectory);
            restarted = Process.Start(start) ?? throw new IOException("更新后无法重新启动程序。");
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (!File.Exists(Path.Combine(plan.SessionDirectory, "ready")))
            {
                if (restarted.HasExited || DateTime.UtcNow >= deadline)
                {
                    if (!restarted.HasExited) { restarted.Kill(true); await restarted.WaitForExitAsync(); }
                    throw new IOException("新版本未能正常初始化，已尝试恢复旧版本。");
                }
                await Task.Delay(250);
            }
        });
    }
    finally { restarted?.Dispose(); }
    File.WriteAllText(Path.Combine(plan.SessionDirectory, "complete"), plan.Version);
    // The small running updater is removed by the application on the next normal startup.
    foreach (string directory in new[] { "staged", "backup" })
        try { Directory.Delete(Path.Combine(plan.SessionDirectory, directory), true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    foreach (string file in Directory.EnumerateFiles(plan.SessionDirectory, "*.zip*"))
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}
catch (Exception ex)
{
    if (plan != null)
    {
        // Keep backup and downloaded files for recovery on failure; never reset user settings.
        try { File.WriteAllText(Path.Combine(plan.SessionDirectory, "failed"), "更新未完成，保留旧文件供恢复。"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (mayRestart && ex is not AggregateException && ex is not OperationCanceledException)
            try { Process.Start(new ProcessStartInfo(Path.Combine(plan.InstallDirectory, UpdatePackage.Executable)) { UseShellExecute = false, WorkingDirectory = plan.InstallDirectory }); } catch (Exception) { }
    }
    MessageBox.Show("更新未完成。" + (ex is AggregateException ? "恢复也遇到问题，请保留备份并手动恢复。" : "原版本和设置已保留，可稍后重试。") +
        (plan == null ? "" : "\n\n更新备份位置：" + plan.SessionDirectory), "DeepSeek Desktop 更新", MessageBoxButtons.OK, MessageBoxIcon.Error);
    Environment.ExitCode = 1;
}
