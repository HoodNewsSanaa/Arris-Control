using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Java.Net;
using System.Security.Cryptography;
using System.Text;
using Path = System.IO.Path;

namespace ArrisControl;

// Uses Android's system-default Internet network directly. It does not change
// the process binding used by the local router connection, or bypass a VPN by
// selecting an underlying Wi-Fi/mobile network.
internal sealed class AndroidUpdateTransport
{
    readonly Network network;
    public AndroidUpdateTransport(Context context)
    {
        var manager = (ConnectivityManager)context.GetSystemService(Context.ConnectivityService)!;
        network = manager.ActiveNetwork ?? throw new RouterException("Updates need an Internet connection. Local router control still works offline.");
        var caps = manager.GetNetworkCapabilities(network);
        if (caps?.HasCapability(NetCapability.Validated) != true) throw new RouterException("No Internet connection is available for update checks.");
    }
    public Task<string> TextAsync(string url, int limit, CancellationToken token) => Task.Run(() =>
    {
        using var output = new MemoryStream(); Copy(url, output, limit, null, token); return Encoding.UTF8.GetString(output.ToArray());
    }, token);
    public Task DownloadAsync(string url, string path, long limit, IProgress<long>? progress, CancellationToken token) => Task.Run(() =>
    {
        using var output = File.Create(path); Copy(url, output, limit, progress, token);
    }, token);
    void Copy(string original, Stream output, long limit, IProgress<long>? progress, CancellationToken token)
    {
        var url = original;
        for (var redirect = 0; redirect < 6; redirect++)
        {
            token.ThrowIfCancellationRequested();
            if (!UpdateProtocol.AllowedDownload(url)) throw new RouterException("The update download used an unexpected destination.");
            using var address = new URL(url);
            using var connection = network.OpenConnection(address) as HttpURLConnection ?? throw new RouterException("Could not open the update connection.");
            connection.InstanceFollowRedirects = false; connection.ConnectTimeout = 15000; connection.ReadTimeout = 30000;
            connection.SetRequestProperty("User-Agent", "Arris-Control-Android");
            connection.SetRequestProperty("Accept", "application/vnd.github+json");
            using var registration = token.Register(() => connection.Disconnect());
            try
            {
                var code = (int)connection.ResponseCode;
                if (code is 301 or 302 or 303 or 307 or 308)
                {
                    var location = connection.GetHeaderField("Location") ?? throw new RouterException("The update redirect was empty.");
                    url = new System.Uri(new System.Uri(url), location).AbsoluteUri; continue;
                }
                if (code != 200) throw new RouterException(code == 403 || code == 429 ? "GitHub temporarily limited update checks. Try again later." : "The update server returned HTTP " + code + ".");
                using var input = connection.InputStream ?? throw new RouterException("The update response was empty.");
                var buffer = new byte[65536]; int count; long total = 0, lastProgress = 0;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested(); total += count;
                    if (total > limit) throw new RouterException("The update download exceeded its expected size.");
                    output.Write(buffer, 0, count);
                    if (total - lastProgress > 1024 * 1024) { progress?.Report(total); lastProgress = total; }
                }
                progress?.Report(total); return;
            }
            catch (Exception) when (token.IsCancellationRequested) { throw new System.OperationCanceledException(token); }
            finally { connection.Disconnect(); }
        }
        throw new RouterException("The update server redirected too many times.");
    }
}

internal static class AndroidUpdater
{
    public static string Folder(Context context) => Path.Combine(context.CacheDir!.AbsolutePath, "updates");
    public static string ApkPath(Context context) => Path.Combine(Folder(context), "candidate.apk");
    public static long Code(PackageInfo info) => Build.VERSION.SdkInt >= BuildVersionCodes.P ? info.LongVersionCode : info.VersionCode;
    public static long InstalledVersion(Context context) => Code(context.PackageManager!.GetPackageInfo(context.PackageName!, 0)!);
    public static async Task<AndroidUpdate?> CheckAsync(Context context, CancellationToken token)
    {
        var transport = new AndroidUpdateTransport(context);
        return UpdateProtocol.Select(await transport.TextAsync(UpdateProtocol.Feed, 2 * 1024 * 1024, token), InstalledVersion(context));
    }
    public static async Task DownloadAsync(Context context, AndroidUpdate release, IProgress<long>? progress, CancellationToken token)
    {
        var transport = new AndroidUpdateTransport(context);
        var checksum = UpdateProtocol.ParseChecksum(await transport.TextAsync(release.ChecksumUrl, 4096, token), release);
        Directory.CreateDirectory(Folder(context)); var temporary = Path.Combine(Folder(context), "download.part.apk");
        try
        {
            await transport.DownloadAsync(release.ApkUrl, temporary, release.Size, progress, token);
            UpdateProtocol.CheckHash(temporary, checksum, release.Size);
            ValidateApk(context, temporary, release.Version);
            File.Move(temporary, ApkPath(context), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void ValidateApk(Context context, string path, long expectedVersion)
    {
        var flags = Build.VERSION.SdkInt >= BuildVersionCodes.P ? PackageInfoFlags.SigningCertificates : PackageInfoFlags.Signatures;
        using var archive = context.PackageManager!.GetPackageArchiveInfo(path, flags) ?? throw new RouterException("Android could not verify the downloaded APK.");
        using var installed = context.PackageManager.GetPackageInfo(context.PackageName!, flags)!;
        if (archive.PackageName != UpdateProtocol.Package || Code(archive) != expectedVersion || expectedVersion <= Code(installed))
            throw new RouterException($"The APK is not the expected newer Arris Control Android build (package {archive.PackageName}; downloaded {Code(archive)}, expected {expectedVersion}, installed {Code(installed)}).");
        if (archive.ApplicationInfo?.MinSdkVersion > (int)Build.VERSION.SdkInt) throw new RouterException("This update requires a newer Android version.");
        static string[] Signers(PackageInfo package)
        {
            var signatures = Build.VERSION.SdkInt >= BuildVersionCodes.P ? package.SigningInfo?.GetApkContentsSigners() : package.Signatures;
            return signatures?.Select(x => Convert.ToHexString(SHA256.HashData(x.ToByteArray()!))).Order().ToArray() ?? [];
        }
        var actual = Signers(archive); var current = Signers(installed);
        if (actual.Length == 0 || !actual.SequenceEqual(current))
            throw new RouterException("This APK has a different signing key. It cannot update this installation. A permanent signing-key setup is needed; your saved router logins have not been changed.");
    }
    public static Intent InstallIntent(Context context) => new Intent(Intent.ActionView)
        .SetDataAndType(Android.Net.Uri.Parse("content://" + ApkUpdateProvider.Authority + "/apk"), ApkUpdateProvider.Mime)!
        .AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
}
