using Android.Content;
using Android.Database;
using Android.OS;

namespace ArrisControl;

// Exactly one app-private APK, never arbitrary file paths. Only the system
// installer receives a temporary read grant; the provider is not exported.
[ContentProvider(new[] { "com.hoodnewssanaa.arriscontrol.updates" }, Exported = false, GrantUriPermissions = true)]
public sealed class ApkUpdateProvider : ContentProvider
{
    public const string Authority = "com.hoodnewssanaa.arriscontrol.updates";
    public const string Mime = "application/vnd.android.package-archive";
    public override bool OnCreate() => true;
    static void Check(Android.Net.Uri uri)
    {
        if (uri.Authority != Authority || uri.Path != "/apk" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new Java.Lang.SecurityException("Unsupported update URI.");
    }
    public override string? GetType(Android.Net.Uri uri) { Check(uri); return Mime; }
    public override ParcelFileDescriptor? OpenFile(Android.Net.Uri uri, string mode)
    {
        Check(uri);
        if (mode != "r") throw new Java.Lang.SecurityException("The update APK is read-only.");
        return ParcelFileDescriptor.Open(new Java.IO.File(AndroidUpdater.ApkPath(Context!)), ParcelFileMode.ReadOnly);
    }
    public override ICursor? Query(Android.Net.Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder)
    {
        Check(uri);
        var cursor = new MatrixCursor(["_display_name", "_size"]);
        cursor.AddRow(new Java.Lang.Object[] { new Java.Lang.String(UpdateProtocol.ApkName), new Java.Lang.Long(new FileInfo(AndroidUpdater.ApkPath(Context!)).Length) });
        return cursor;
    }
    public override Android.Net.Uri? Insert(Android.Net.Uri uri, ContentValues? values) => throw new Java.Lang.UnsupportedOperationException();
    public override int Delete(Android.Net.Uri uri, string? selection, string[]? selectionArgs) => throw new Java.Lang.UnsupportedOperationException();
    public override int Update(Android.Net.Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => throw new Java.Lang.UnsupportedOperationException();
}
