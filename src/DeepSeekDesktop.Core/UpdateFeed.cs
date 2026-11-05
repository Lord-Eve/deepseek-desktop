using System.Text.Json;

namespace DeepSeekDesktop.Core;

public sealed record DesktopRelease(Version Version, string PageUrl, long PackageSize = 0)
{
    public string PackageName => $"DeepSeekDesktop-{Version}-win-x64.zip";
}

public sealed record UpdateProgress(string Message, double? Fraction = null);

// Tracks a download by the size of its destination file; the GitHub CLI reports no progress when redirected.
public sealed class DownloadWatch(TimeSpan stallLimit)
{
    private long last = -1;
    private DateTimeOffset since;

    public bool Stalled(long bytes, DateTimeOffset now)
    {
        if (bytes != last) { last = bytes; since = now; return false; }
        return now - since >= stallLimit;
    }

    public static UpdateProgress Report(long bytes, long total)
    {
        static string Megabytes(long value) => (value / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        if (total <= 0) return new($"正在下载更新包：已下载 {Megabytes(bytes)} MB，可取消；完成后将退出并重启程序…");
        double fraction = Math.Clamp((double)bytes / total, 0, 1);
        return new($"正在下载更新包：{Megabytes(bytes)} / {Megabytes(total)} MB（{fraction:0%}），可取消；完成后将退出并重启程序…", fraction);
    }
}

public static class UpdateFeed
{
    public const string ReleasesUrl = "https://github.com/Lord-Eve/deepseek-desktop/releases";

    public static DesktopRelease Latest(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("更新信息格式不正确，请稍后重试。");
        DesktopRelease? latest = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object ||
                !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
                !release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
                continue;
            string value = tag.GetString()!;
            if (!value.StartsWith('v') || !Version.TryParse(value[1..], out var version) || version.Build < 0 || version.Revision >= 0)
                continue;
            // The project's published previews have numeric tags and prerelease=true.
            // Only advertise releases containing the matching Windows desktop package.
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                continue;
            var package = assets.EnumerateArray().FirstOrDefault(a => a.ValueKind == JsonValueKind.Object &&
                a.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                name.GetString() == $"DeepSeekDesktop-{version}-win-x64.zip");
            if (package.ValueKind != JsonValueKind.Object)
                continue;
            long size = package.TryGetProperty("size", out var sizeValue) && sizeValue.ValueKind == JsonValueKind.Number && sizeValue.TryGetInt64(out long bytes) && bytes > 0 ? bytes : 0;
            if (latest == null || version > latest.Version)
                latest = new DesktopRelease(version, $"{ReleasesUrl}/tag/v{version}", size);
        }
        return latest ?? throw new InvalidDataException("暂未找到可下载的 Windows 版本，请前往下载页查看。");
    }
}
