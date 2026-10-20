using System.Text.Json;

namespace DeepSeekDesktop.Core;

public sealed record DesktopRelease(Version Version, string PageUrl);

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
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array ||
                !assets.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.Object &&
                    a.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                    name.GetString() == $"DeepSeekDesktop-{version}-win-x64.zip"))
                continue;
            if (latest == null || version > latest.Version)
                latest = new DesktopRelease(version, $"{ReleasesUrl}/tag/v{version}");
        }
        return latest ?? throw new InvalidDataException("暂未找到可下载的 Windows 版本，请前往下载页查看。");
    }
}
