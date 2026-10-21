using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DeepSeekDesktop.Core;

// A harmless fake desktop, used only in the temporary installation below.
if (args.Length == 2 && args[0] == "--update-session")
{
    File.WriteAllText(Path.Combine(args[1], "ready"), "fixture-ready");
    File.WriteAllText(Path.Combine(args[1], "fixture-pid"), Environment.ProcessId.ToString());
    await Task.Delay(TimeSpan.FromSeconds(30));
    return;
}

int checks = 0;
void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
void Reject(Action action, string why)
{
    bool rejected = false; try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException) { rejected = true; }
    Check(rejected, why);
}
string root = Path.Combine(Path.GetTempPath(), "DeepSeekDesktop-update-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
const string package = "DeepSeekDesktop-0.4.0-win-x64";
string zip = Path.Combine(root, package + ".zip"), checksum = zip + ".sha256";
void Archive(params (string Path, string Content)[] entries)
{
    using (var stream = File.Create(zip))
    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        foreach (var item in entries) { using var writer = new StreamWriter(archive.CreateEntry(item.Path).Open()); writer.Write(item.Content); }
    using var file = File.OpenRead(zip);
    File.WriteAllText(checksum, Convert.ToHexString(SHA256.HashData(file)) + "  " + package + ".zip\n");
}
(string, string)[] Required() => new[] { (package + "/DeepSeekDesktop.exe", "new-app"), (package + "/DeepSeekDesktop.Updater.exe", "new-helper"), (package + "/engine/ds-free-api.exe", "new-engine"), (package + "/engine-lock.json", "fixture-lock") };
try
{
    string staged = Path.Combine(root, "stage");
    Archive(Required());
    UpdatePackage.ExtractVerified(zip, checksum, new Version(0, 4, 0), staged);
    Check(File.ReadAllText(Path.Combine(staged, UpdatePackage.Executable)) == "new-app", "A matching ZIP and checksum must extract the expected release layout.");
    File.AppendAllText(zip, "tamper");
    Reject(() => UpdatePackage.ExtractVerified(zip, checksum, new Version(0, 4, 0), Path.Combine(root, "bad-hash")), "A modified archive must fail checksum verification before extraction.");
    foreach (string bad in new[] { package + "/../../escape.txt", package + "/C:/evil", package + "/source/CON.txt", "other-release/file.txt", package + "/file:stream", package + "/dot./file" })
    {
        Archive(Required().Append((bad, "bad")).ToArray());
        string destination = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Reject(() => UpdatePackage.ExtractVerified(zip, checksum, new Version(0, 4, 0), destination), "Unsafe archive paths must be rejected: " + bad);
        Check(!Directory.Exists(destination), "Unsafe archives must be rejected before any output is created.");
    }
    Archive(Required().Append((package + "/deepseekdesktop.EXE", "duplicate")).ToArray());
    Reject(() => UpdatePackage.ExtractVerified(zip, checksum, new Version(0, 4, 0), Path.Combine(root, "duplicate")), "Windows case-insensitive duplicate file names must be rejected.");
    Archive((package + "/README.md", "incomplete"));
    Reject(() => UpdatePackage.ExtractVerified(zip, checksum, new Version(0, 4, 0), Path.Combine(root, "incomplete")), "Incomplete releases must not be installed.");
    string install = Path.Combine(root, "install"), session = Path.Combine(root, "session");
    Directory.CreateDirectory(install); Directory.CreateDirectory(session);
    File.WriteAllText(Path.Combine(install, UpdatePackage.Executable), "old-app");
    File.WriteAllText(Path.Combine(install, "my-notes.txt"), "keep-me");
    string data = Path.Combine(root, "data"); Directory.CreateDirectory(data);
    File.WriteAllText(Path.Combine(data, "settings.dpapi"), "fixture-encrypted-settings");
    string replacement = Path.Combine(session, "staged"); Directory.Move(staged, replacement);
    bool restarted = false;
    await UpdatePackage.InstallAsync(replacement, install, Path.Combine(session, "backup"), () => { restarted = true; return Task.CompletedTask; });
    Check(restarted && File.ReadAllText(Path.Combine(install, UpdatePackage.Executable)) == "new-app" && File.ReadAllText(Path.Combine(install, "my-notes.txt")) == "keep-me", "Installation must replace managed files and preserve unrelated user files.");
    Check(File.ReadAllText(Path.Combine(data, "settings.dpapi")) == "fixture-encrypted-settings", "Installation must not touch the external settings directory.");
    File.WriteAllText(Path.Combine(replacement, UpdatePackage.Executable), "broken-new-app");
    File.WriteAllText(Path.Combine(replacement, "new-file.txt"), "new");
    bool failed = false;
    try { await UpdatePackage.InstallAsync(replacement, install, Path.Combine(session, "backup-failure"), () => throw new IOException("fixture initialization failure")); } catch (IOException) { failed = true; }
    Check(failed && File.ReadAllText(Path.Combine(install, UpdatePackage.Executable)) == "new-app" && !File.Exists(Path.Combine(install, "new-file.txt")), "A failed restart must restore overwritten files and remove newly installed files.");
    string lockedStage = Path.Combine(session, "locked-stage"); Directory.CreateDirectory(lockedStage);
    File.WriteAllText(Path.Combine(lockedStage, "first.txt"), "new-first"); File.WriteAllText(Path.Combine(install, "first.txt"), "old-first");
    File.WriteAllText(Path.Combine(lockedStage, "locked.txt"), "new-locked"); File.WriteAllText(Path.Combine(install, "locked.txt"), "old-locked");
    using (var locked = new FileStream(Path.Combine(install, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        bool blocked = false;
        try { await UpdatePackage.InstallAsync(lockedStage, install, Path.Combine(session, "backup-locked"), () => throw new Exception("Must not restart")); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { blocked = true; }
        Check(blocked && File.ReadAllText(Path.Combine(install, "first.txt")) == "old-first" && File.ReadAllText(Path.Combine(install, "locked.txt")) == "old-locked", "A locked target must leave its original intact and restore any earlier replacements.");
    }
    Directory.CreateDirectory(Path.Combine(install, ".git"));
    Reject(() => UpdatePackage.ValidateInstall(install, session), "Self-update must refuse a Git workspace."); Directory.Delete(Path.Combine(install, ".git"));
    Reject(() => UpdatePackage.ValidateInstall(install, Path.Combine(install, "cache")), "The update cache must stay outside the installation.");

    // Run the actual updater process against a fake desktop and a disposable parent process.
    string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
    string helperBin = Path.Combine(repo, "src/DeepSeekDesktop.Updater/bin", configuration, "net8.0-windows");
    string liveSession = Path.Combine(root, "live-session"), liveInstall = Path.Combine(root, "live-install");
    Directory.CreateDirectory(liveSession); Directory.CreateDirectory(liveInstall);
    foreach (string file in Directory.EnumerateFiles(helperBin)) File.Copy(file, Path.Combine(liveSession, Path.GetFileName(file)));
    string liveStage = Path.Combine(liveSession, "staged"); Directory.CreateDirectory(liveStage);
    foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(liveStage, Path.GetFileName(file)));
    File.Copy(Path.Combine(liveStage, "DeepSeekDesktop.UpdateChecks.exe"), Path.Combine(liveStage, UpdatePackage.Executable));
    File.WriteAllText(Path.Combine(liveInstall, UpdatePackage.Executable), "old-fixture");
    using var parent = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
    var plan = new UpdatePlan(parent.Id, parent.StartTime.ToUniversalTime().Ticks, liveInstall, liveSession, "0.4.0", false);
    string planFile = Path.Combine(liveSession, "plan.json"); File.WriteAllText(planFile, JsonSerializer.Serialize(plan));
    var helperInfo = new ProcessStartInfo(Path.Combine(liveSession, UpdatePackage.Updater)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = liveSession };
    helperInfo.ArgumentList.Add(planFile);
    using var helper = Process.Start(helperInfo)!;
    try
    {
        await Task.Delay(500);
        Check(File.ReadAllText(Path.Combine(liveInstall, UpdatePackage.Executable)) == "old-fixture", "The updater must wait for the original desktop process to exit before replacing files.");
        parent.Kill(true); await parent.WaitForExitAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await helper.WaitForExitAsync(deadline.Token);
        Check(helper.ExitCode == 0 && File.Exists(Path.Combine(liveSession, "complete")) && File.Exists(Path.Combine(liveSession, "ready")), "The actual updater must launch the new desktop, observe readiness and complete successfully.");
    }
    finally
    {
        if (!parent.HasExited) { parent.Kill(true); await parent.WaitForExitAsync(); }
        if (!helper.HasExited) { helper.Kill(true); await helper.WaitForExitAsync(); }
        string pid = Path.Combine(liveSession, "fixture-pid");
        if (File.Exists(pid)) try { using var fake = Process.GetProcessById(int.Parse(File.ReadAllText(pid))); fake.Kill(true); await fake.WaitForExitAsync(); } catch (ArgumentException) { }
    }
    Console.WriteLine($"PASS: {checks} update package, rollback and real updater process checks (temporary installation only)");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
