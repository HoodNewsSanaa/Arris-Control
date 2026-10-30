using Android.Content;
using System.Security.Cryptography;
using Path = System.IO.Path;

namespace ArrisControl;

internal static class AndroidUpdateSelfTests
{
    static string RecordPath(Context context) => Path.Combine(context.CacheDir!.AbsolutePath, "update-retention-path");
    public static void VerifyCandidate(Context context)
    {
        var expected = AndroidUpdater.InstalledVersion(context) + 1;
        var candidate = AndroidUpdater.ApkPath(context);
        AndroidUpdater.ValidateApk(context, candidate, expected);
        var refused = false;
        try { AndroidUpdater.ValidateApk(context, Path.Combine(AndroidUpdater.Folder(context), "foreign.apk"), expected); }
        catch (RouterException ex) when (ex.Message.Contains("different signing key", StringComparison.Ordinal)) { refused = true; }
        if (!refused) throw new InvalidOperationException("A differently signed update APK was accepted.");
        var intent = AndroidUpdater.InstallIntent(context);
        if (intent.ResolveActivity(context.PackageManager!) is null) throw new InvalidOperationException("No Android APK installer resolves the update intent.");
        using (var stream = context.ContentResolver!.OpenInputStream(intent.Data!)!)
        using (var source = File.OpenRead(candidate))
            if (!SHA256.HashData(stream).SequenceEqual(SHA256.HashData(source))) throw new InvalidOperationException("The installation URI returned different APK bytes.");
        var traversalRefused = false;
        try { using var invalid = context.ContentResolver.OpenInputStream(Android.Net.Uri.Parse("content://" + ApkUpdateProvider.Authority + "/other")!); }
        catch (Exception) { traversalRefused = true; }
        if (!traversalRefused) throw new InvalidOperationException("The APK provider allowed a different file path.");
        var provider = context.PackageManager!.ResolveContentProvider(ApkUpdateProvider.Authority, 0)!;
        if (provider.Exported || !provider.GrantUriPermissions) throw new InvalidOperationException("The APK provider permissions are incorrect.");
        var record = OfflineSetupStore.Save(new(new("Fixture", "TESTSERIAL123", "001122334455"), new("fixture@local", "Fictional-Update-Retention!"), "Fixture"));
        File.WriteAllText(RecordPath(context), record);
        Console.WriteLine("PASS: Android update package/version/signing verification, foreign-key refusal and private installation URI.");
    }
    public static void VerifyRetained(Context context)
    {
        var path = File.ReadAllText(RecordPath(context));
        var record = OfflineSetupStore.Load(path);
        if (record.Credential.Password != "Fictional-Update-Retention!") throw new InvalidOperationException("The encrypted login did not survive the APK update.");
        File.Delete(path); File.Delete(RecordPath(context));
        foreach (var file in Directory.EnumerateFiles(AndroidUpdater.Folder(context))) File.Delete(file);
        Console.WriteLine("PASS: Android APK update retained the encrypted local login and Android Keystore key.");
    }
}
