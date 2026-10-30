using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ArrisControl;

internal static class UpdateProtocolSelfTests
{
    public static Task RunAsync()
    {
        static void Assert(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); }
        var bytes = "fictional APK contents"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        JsonObject Release(string tag, int size) => new()
        {
            ["tag_name"] = tag, ["draft"] = false, ["body"] = "Fictional release",
            ["assets"] = new JsonArray(
                new JsonObject { ["name"] = UpdateProtocol.ApkName, ["size"] = size, ["digest"] = "sha256:" + hash,
                    ["browser_download_url"] = $"https://github.com/HoodNewsSanaa/Arris-Control/releases/download/{tag}/Arris-Control-android.apk" },
                new JsonObject { ["name"] = "SHA256SUMS-android.txt",
                    ["browser_download_url"] = $"https://github.com/HoodNewsSanaa/Arris-Control/releases/download/{tag}/SHA256SUMS-android.txt" })
        };
        var feed = new JsonArray(Release("v99.0", 1), Release("android-preview-9", bytes.Length), Release("android-preview-11", bytes.Length), Release("android-preview-10", bytes.Length));
        var chosen = UpdateProtocol.Select(feed.ToJsonString(), 9)!;
        Assert(chosen.Version == 11, "Update selection followed ordering or a Windows release.");
        Assert(UpdateProtocol.Select(feed.ToJsonString(), 11) is null, "Updater offered an installed version or downgrade.");
        var hostile = Release("android-preview-12", bytes.Length); hostile["assets"]![0]!["browser_download_url"] = "https://example.com/malicious.apk";
        Assert(UpdateProtocol.Select(new JsonArray(hostile).ToJsonString(), 0) is null, "Updater accepted an unrelated APK host.");
        Assert(UpdateProtocol.Select(new JsonArray(Release("android-preview-13", int.MaxValue)).ToJsonString(), 0) is null, "Oversized update accepted.");
        Assert(UpdateProtocol.ParseChecksum(hash.ToLowerInvariant() + "  Arris-Control-android.apk\n", chosen) == hash, "Release checksum mapping failed.");
        foreach (var wrong in new[] { hash + " other.apk", new string('0', 64) + "  Arris-Control-android.apk", hash + "  Arris-Control-android.apk\nextra" })
        {
            var refused = false; try { UpdateProtocol.ParseChecksum(wrong, chosen); } catch (RouterException) { refused = true; }
            Assert(refused, "Bad or conflicting checksum accepted.");
        }
        foreach (var url in new[] { "http://github.com/file", "https://github.com.evil.test/file", "https://github.com:444/file", "https://user@github.com/file", "https://127.0.0.1/file" })
            Assert(!UpdateProtocol.AllowedDownload(url), "Unexpected update redirect accepted.");
        Assert(UpdateProtocol.AllowedDownload("https://release-assets.githubusercontent.com/asset?signature=fictional"), "Official release CDN rejected.");
        var path = Path.Combine(Path.GetTempPath(), "update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path, bytes); UpdateProtocol.CheckHash(path, hash, bytes.Length);
            bytes[0] ^= 1; File.WriteAllBytes(path, bytes);
            var refused = false; try { UpdateProtocol.CheckHash(path, hash, bytes.Length); } catch (RouterException) { refused = true; }
            Assert(refused, "A modified APK passed checksum verification.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        Console.WriteLine("PASS: Android update release selection, downgrade/host/size refusal and APK checksum tamper checks.");
        return Task.CompletedTask;
    }
}
