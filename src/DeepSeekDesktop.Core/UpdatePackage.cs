using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DeepSeekDesktop.Core;

public sealed record UpdatePlan(int ParentId, long ParentStarted, string InstallDirectory, string SessionDirectory, string Version, bool ResumeService);

public static class UpdatePackage
{
    public const string Executable = "DeepSeekDesktop.exe";
    public const string Updater = "DeepSeekDesktop.Updater.exe";

    public static bool Within(string child, string parent) => Path.GetFullPath(child).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void NoLinks(string path)
    {
        for (var entry = new DirectoryInfo(Path.GetFullPath(path)); entry != null; entry = entry.Parent)
            if (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("更新路径不能包含目录链接。");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("更新文件不能是链接。");
    }

    public static void ValidateInstall(string target, string session)
    {
        target = Path.GetFullPath(target); session = Path.GetFullPath(session);
        if (Path.GetPathRoot(target) == Path.TrimEndingDirectorySeparator(target) ||
            Directory.Exists(Path.Combine(target, ".git")) || File.Exists(Path.Combine(target, ".git")) ||
            !File.Exists(Path.Combine(target, Executable)) ||
            target.Equals(session, StringComparison.OrdinalIgnoreCase) || Within(target, session) || Within(session, target))
            throw new IOException("请在独立软件目录运行更新；不能更新 Git 工作区、磁盘根目录或覆盖更新缓存。");
        NoLinks(target); NoLinks(session);
    }

    public static void ExtractVerified(string zip, string checksum, Version version, string destination)
    {
        string expectedName = $"DeepSeekDesktop-{version}-win-x64.zip";
        string[] hash = File.ReadAllText(checksum).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (hash.Length != 2 || hash[1] != expectedName || hash[0].Length != 64 || !hash[0].All(Uri.IsHexDigit)) throw new InvalidDataException("更新包校验文件格式不正确。");
        using (var file = File.OpenRead(zip))
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(hash[0], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包 SHA-256 校验失败，请重新下载。");
        NoLinks(destination);
        if (Directory.Exists(destination)) throw new IOException("更新暂存目录已经存在。");
        using var archive = ZipFile.OpenRead(zip);
        string prefix = $"DeepSeekDesktop-{version}-win-x64/";
        var files = new List<(ZipArchiveEntry Entry, string Relative)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        if (archive.Entries.Count > 20000) throw new InvalidDataException("更新包文件数量异常。");
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新包包含非预期目录或链接。");
            string relative = name[prefix.Length..].TrimEnd('/');
            if (relative.Length == 0) continue;
            foreach (string part in relative.Split('/'))
                if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => c < 32 || ":<>\"|?*".Contains(c)) ||
                    Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    throw new InvalidDataException("更新包包含不安全的文件路径。");
            if (name.EndsWith('/')) continue;
            if (!names.Add(relative)) throw new InvalidDataException("更新包包含重复文件。");
            expanded = checked(expanded + entry.Length);
            if (expanded > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("更新包解压大小异常。");
            files.Add((entry, relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        foreach (string required in new[] { Executable, Updater, "engine/ds-free-api.exe", "engine-lock.json" })
            if (!names.Contains(required)) throw new InvalidDataException("更新包缺少必要程序文件。");
        // Validate the whole archive before creating any extracted file.
        Directory.CreateDirectory(destination);
        try
        {
            foreach (var (entry, relative) in files)
            {
                string path = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path);
            }
        }
        catch { Directory.Delete(destination, true); throw; }
    }

    // Only replace package files; unrelated files and the external DPAPI data directory are preserved.
    // Keep originals until the restarted application confirms that initialization succeeded.
    public static async Task InstallAsync(string staged, string target, string backup, Func<Task> restart)
    {
        ValidateInstall(target, Path.GetDirectoryName(staged)!);
        NoLinks(staged); NoLinks(backup);
        if (Directory.Exists(backup)) throw new IOException("更新备份目录已经存在。");
        var files = new List<string>();
        void Scan(string directory)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                NoLinks(entry);
                if (Directory.Exists(entry)) Scan(entry); else files.Add(Path.GetRelativePath(staged, entry));
            }
        }
        Scan(staged);
        var originals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in files)
        {
            string destination = Path.Combine(target, relative); NoLinks(destination);
            if (Directory.Exists(destination)) throw new IOException("更新文件与现有目录冲突。");
            if (File.Exists(destination))
            {
                string saved = Path.Combine(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                File.Copy(destination, saved); originals.Add(relative);
            }
        }
        var touched = new List<string>();
        var createdDirectories = new List<string>();
        try
        {
            foreach (string relative in files)
            {
                string destination = Path.Combine(target, relative);
                var missing = new Stack<string>();
                for (string? dir = Path.GetDirectoryName(destination); dir != null && !Directory.Exists(dir); dir = Path.GetDirectoryName(dir)) missing.Push(dir);
                foreach (string dir in missing) { Directory.CreateDirectory(dir); createdDirectories.Add(dir); }
                string temporary = destination + ".update-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(Path.Combine(staged, relative), temporary);
                    File.Move(temporary, destination, true);
                    touched.Add(relative);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            await restart();
        }
        catch (Exception installation)
        {
            try
            {
                var failures = new List<Exception>();
                foreach (string relative in touched.AsEnumerable().Reverse())
                {
                    try
                    {
                        string destination = Path.Combine(target, relative);
                        if (originals.Contains(relative)) File.Copy(Path.Combine(backup, relative), destination, true);
                        else if (File.Exists(destination)) File.Delete(destination);
                    }
                    catch (Exception ex) { failures.Add(ex); }
                }
                foreach (string dir in createdDirectories.AsEnumerable().Reverse())
                    try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch (Exception ex) { failures.Add(ex); }
                if (failures.Count > 0) throw new AggregateException(failures);
            }
            catch (Exception rollback) { throw new AggregateException("更新及恢复失败，旧文件保留在更新备份目录。", installation, rollback); }
            throw;
        }
    }
}
