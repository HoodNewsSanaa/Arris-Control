using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArrisControl;

internal sealed record AndroidUpdate(long Version, string Tag, string Notes, string ApkUrl, string ChecksumUrl, long Size, string? Digest);
internal static class UpdateProtocol
{
    public const string Package = "com.hoodnewssanaa.arriscontrol";
    public const string Feed = "https://api.github.com/repos/HoodNewsSanaa/Arris-Control/releases?per_page=30";
    public const string ApkName = "Arris-Control-android.apk";
    public const long MaxApkBytes = 128L * 1024 * 1024;
    const string ReleaseRoot = "https://github.com/HoodNewsSanaa/Arris-Control/releases/download/";
    public static AndroidUpdate? Select(string json, long installed)
    {
        if (json.Length > 2 * 1024 * 1024) throw new RouterException("The update feed exceeded its size limit.");
        var list = JsonNode.Parse(json) as JsonArray ?? throw new RouterException("The update feed was not a release list.");
        var candidates = new List<AndroidUpdate>();
        foreach (var item in list.OfType<JsonObject>())
        {
            var tag = Catalog.Text(item["tag_name"]);
            var match = Regex.Match(tag, "^android-preview-([1-9][0-9]{0,8})$");
            if (item["draft"]?.GetValue<bool>() == true || !match.Success ||
                !long.TryParse(match.Groups[1].Value, out var version) || version <= installed) continue;
            var assets = item["assets"] as JsonArray;
            if (assets is null) continue;
            var apk = assets.OfType<JsonObject>().SingleOrDefault(x => Catalog.Text(x["name"]) == ApkName);
            var sums = assets.OfType<JsonObject>().SingleOrDefault(x => Catalog.Text(x["name"]) == "SHA256SUMS-android.txt");
            if (apk is null || sums is null) continue;
            var apkUrl = Catalog.Text(apk["browser_download_url"]);
            var checksumUrl = Catalog.Text(sums["browser_download_url"]);
            if (apkUrl != ReleaseRoot + tag + "/" + ApkName || checksumUrl != ReleaseRoot + tag + "/SHA256SUMS-android.txt") continue;
            if (!long.TryParse(Catalog.Text(apk["size"]), out var size) || size <= 0 || size > MaxApkBytes) continue;
            var digest = Catalog.Text(apk["digest"]);
            if (digest.Length != 0 && !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$")) continue;
            var notes = Catalog.Text(item["body"]);
            candidates.Add(new(version, tag, notes.Length > 6000 ? notes[..6000] : notes, apkUrl, checksumUrl, size,
                digest.Length == 0 ? null : digest[7..].ToUpperInvariant()));
        }
        return candidates.OrderByDescending(x => x.Version).FirstOrDefault();
    }
    public static string ParseChecksum(string text, AndroidUpdate release)
    {
        var lines = text.Trim().Split('\n');
        if (lines.Length != 1) throw new RouterException("The update checksum file was not recognized.");
        var match = Regex.Match(lines[0].Trim(), "^([A-Fa-f0-9]{64})[ \\t]+\\*?Arris-Control-android\\.apk$");
        if (!match.Success) throw new RouterException("The update checksum was not recognized.");
        var hash = match.Groups[1].Value.ToUpperInvariant();
        if (release.Digest is not null && hash != release.Digest) throw new RouterException("The release metadata and checksum disagree.");
        return hash;
    }
    public static void CheckHash(string path, string expected, long expectedSize)
    {
        if (!Regex.IsMatch(expected, "^[A-Fa-f0-9]{64}$") || new FileInfo(path).Length != expectedSize)
            throw new RouterException("The APK download is incomplete or has an invalid size.");
        using var input = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(input));
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected)))
            throw new RouterException("The APK checksum did not match. The download was discarded.");
    }
    public static bool AllowedDownload(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return false;
        return uri.Host is "api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com";
    }
}
