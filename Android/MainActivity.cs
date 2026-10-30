using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Color = Android.Graphics.Color;
using Path = System.IO.Path;

namespace ArrisControl;

[Activity(Label = "Arris Control", MainLauncher = true, Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class MainActivity : Activity
{
    LinearLayout root = null!, content = null!;
    TextView status = null!, networkStatus = null!;
    ProgressBar activityProgress = null!;
    EditText address = null!, email = null!, password = null!;
    CloudConfiguration? configuration;
    AcquiredCredential? acquired;
    RouterClient? client;
    BleManager? ble;
    Page currentPage = Catalog.Pages[0];
    CancellationTokenSource lifetime = new();
    readonly List<Button> buttons = [];
    bool busy, dirty;
    // Certificate pins seen by the last scan, so first-time verification requires the same certificate.
    readonly Dictionary<string, string> scannedPins = new(StringComparer.Ordinal);
    const int ImportReference = 41;
    const int BlePermissions = 42;
    TaskCompletionSource<bool>? permissionResult;
    TaskCompletionSource<Android.Net.Uri?>? pickedReference;
    // Where the system Back gesture/button goes from the current screen; null means leave the app.
    Func<Task>? backAction;
    bool addressEdited;

    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        // Credentials/passwords never enter screenshots, recent-task thumbnails or backups.
        Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0, new BackCallback(this));
        ShowConnect();
        if (Intent?.GetBooleanExtra("offlineSelfTest", false) != true) TryAutoConnect();
        if (Intent?.GetBooleanExtra("offlineSelfTest", false) == true)
            _ = Run(async () =>
            {
                await Task.Run(() => SelfTests.RunAsync(), lifetime.Token);
                var fixture = CredentialProtection.Protect([1, 2, 3], [4]);
                fixture[^1] ^= 1;
                try { CredentialProtection.Unprotect(fixture, [4]); throw new InvalidOperationException("Tampered credential accepted."); }
                catch (System.Security.Cryptography.CryptographicException) { }
                if (Intent?.GetBooleanExtra("networkSelfTest", false) == true)
                {
                    try { await RouterNetworkSelfTests.RunAsync(lifetime.Token); }
                    catch (Exception ex) { Android.Util.Log.Error("ArrisControlSelfTest", ex.ToString()); throw; }
                    status.Text = "PASS: Android API scan, HTTPS login and reconnect.";
                    if (root.Width > 0 && root.Height > 0)
                    {
                        var resultText = status.Text; status.Text = ""; activityProgress.Visibility = ViewStates.Gone;
                        foreach (var button in buttons) button.Enabled = true;
                        root.Measure(View.MeasureSpec.MakeMeasureSpec(root.Width, MeasureSpecMode.Exactly), View.MeasureSpec.MakeMeasureSpec(root.Height, MeasureSpecMode.Exactly));
                        root.Layout(root.Left, root.Top, root.Right, root.Bottom);
                        using var bitmap = Android.Graphics.Bitmap.CreateBitmap(root.Width, root.Height, Android.Graphics.Bitmap.Config.Argb8888!)!;
                        using var canvas = new Android.Graphics.Canvas(bitmap); root.Draw(canvas);
                        using var output = File.Create(Path.Combine(CacheDir!.AbsolutePath, "ui-preview.png"));
                        bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Png!, 100, output);
                        status.Text = resultText;
                    }
                }
                else status.Text = "PASS: offline protocol and Android Keystore checks.";
            });
    }
#pragma warning disable CA1422, CS0672, CS0618
    // Android 12 and earlier (and 13+ without predictive back) deliver Back here.
    public override void OnBackPressed() => HandleBack(fromDispatcher: false);
#pragma warning restore CA1422, CS0672, CS0618
    void HandleBack(bool fromDispatcher)
    {
        if (busy) { status.Text = "Please wait for the current step to finish."; return; }
        if (backAction is null)
        {
            // Start screen: leave the app the way the system would.
#pragma warning disable CA1422, CS0618
            if (fromDispatcher) MoveTaskToBack(true); else base.OnBackPressed();
#pragma warning restore CA1422, CS0618
            return;
        }
        var go = backAction; _ = Run(go);
    }
    // Android 13+ predictive back (the default for apps targeting Android 16) uses this dispatcher instead.
    sealed class BackCallback(MainActivity owner) : Java.Lang.Object, Android.Window.IOnBackInvokedCallback
    {
        public void OnBackInvoked() => owner.HandleBack(fromDispatcher: true);
    }
    Task BackToStart()
    {
        ble?.Dispose(); ble = null; acquired = null; ShowConnect(); return Task.CompletedTask;
    }
    protected override void OnResume()
    {
        base.OnResume();
        if (networkStatus is not null) networkStatus.Text = AndroidNetwork.Status();
    }
    protected override void OnDestroy()
    {
        lifetime.Cancel(); client?.Dispose(); ble?.Dispose(); AndroidNetwork.UseInternet();
        lifetime.Dispose(); base.OnDestroy();
    }
    int Dp(int n) => (int)(n * Resources!.DisplayMetrics!.Density);
    // A calm dark palette with one azure accent and semantic status colors.
    static class Palette
    {
        public static readonly Color Bg = Color.Rgb(0x0E, 0x10, 0x15);
        public static readonly Color Surface = Color.Rgb(0x18, 0x1B, 0x22);
        public static readonly Color SurfaceStroke = Color.Rgb(0x2C, 0x31, 0x3B);
        public static readonly Color Text = Color.Rgb(0xF4, 0xF6, 0xF8);
        public static readonly Color Muted = Color.Rgb(0x96, 0x9D, 0xAA);
        public static readonly Color Accent = Color.Rgb(0x4C, 0x8D, 0xFF);
        public static readonly Color OnAccent = Color.Rgb(0x08, 0x0B, 0x12);
        public static readonly Color Success = Color.Rgb(0x37, 0xC9, 0x7F);
        public static readonly Color Warning = Color.Rgb(0xE6, 0xA5, 0x4B);
        public static readonly Color Danger = Color.Rgb(0xE8, 0x61, 0x5B);
    }
    enum Tone { Secondary, Primary, Danger }
    TextView Label(string text, int size = 15, bool heading = false, Color? color = null)
    {
        var view = new TextView(this) { Text = text, TextSize = size };
        view.SetTextColor(color ?? (heading ? Palette.Text : size <= 13 ? Palette.Muted : Palette.Text));
        view.SetTypeface(Android.Graphics.Typeface.Create(heading ? "sans-serif-medium" : "sans-serif", Android.Graphics.TypefaceStyle.Normal), Android.Graphics.TypefaceStyle.Normal);
        view.SetLineSpacing(Dp(2), 1.15f);
        view.SetPadding(0, Dp(heading ? 4 : 3), 0, Dp(heading ? 10 : 7));
        return view;
    }
    LinearLayout Column() => new(this) { Orientation = Orientation.Vertical };
    Android.Graphics.Drawables.GradientDrawable Surface(int radius = 18, Color? fill = null, Color? stroke = null, int strokeWidth = 1)
    {
        var shape = new Android.Graphics.Drawables.GradientDrawable();
        shape.SetColor(fill ?? Palette.Surface); shape.SetCornerRadius(Dp(radius));
        shape.SetStroke(Dp(strokeWidth), stroke ?? Palette.SurfaceStroke); return shape;
    }
    // A small rounded status chip, e.g. "Connected" in green or "Offline" in muted.
    TextView Chip(string text, Color color)
    {
        var chip = new TextView(this) { Text = text, TextSize = 12 };
        chip.SetTextColor(color); chip.SetTypeface(Android.Graphics.Typeface.Create("sans-serif-medium", Android.Graphics.TypefaceStyle.Normal), Android.Graphics.TypefaceStyle.Normal);
        chip.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
        chip.Background = Surface(20, Color.Argb(28, color.R, color.G, color.B), Color.Argb(120, color.R, color.G, color.B));
        return chip;
    }
    LinearLayout Card(string? title = null, string? description = null, LinearLayout? parent = null, string? chipText = null, Color? chipColor = null)
    {
        var box = Column(); box.Background = Surface(); box.SetPadding(Dp(18), Dp(16), Dp(18), Dp(18));
        var layout = new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(14) };
        (parent ?? content).AddView(box, layout);
        if (title is not null)
        {
            if (chipText is not null)
            {
                var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                header.SetGravity(GravityFlags.CenterVertical);
                var t = Label(title, 20, true); header.AddView(t, new LinearLayout.LayoutParams(0, -2, 1));
                header.AddView(Chip(chipText, chipColor ?? Palette.Muted));
                box.AddView(header);
            }
            else box.AddView(Label(title, 20, true));
        }
        if (!string.IsNullOrEmpty(description)) box.AddView(Label(description, 14, color: Palette.Muted));
        return box;
    }
    void Shell(string subtitle)
    {
        buttons.Clear(); backAction = null;
        root = Column(); root.SetBackgroundColor(Palette.Bg); root.FocusableInTouchMode = true; root.RequestFocus();
        root.SetPadding(Dp(20), Dp(20), Dp(20), Dp(10));
        var brandRow = new LinearLayout(this) { Orientation = Orientation.Horizontal }; brandRow.SetGravity(GravityFlags.CenterVertical);
        var dot = new TextView(this) { Text = "●", TextSize = 12 }; dot.SetTextColor(Palette.Accent); dot.SetPadding(0, 0, Dp(8), 0); brandRow.AddView(dot);
        var brand = Label("ARRIS CONTROL", 11, color: Palette.Muted); brand.LetterSpacing = 0.22f; brandRow.AddView(brand);
        root.AddView(brandRow);
        var heading = Label(subtitle, 30, true); heading.SetPadding(0, Dp(2), 0, Dp(6)); root.AddView(heading);
        networkStatus = Label(AndroidNetwork.Status(), 12, color: Palette.Muted); root.AddView(networkStatus);
        status = Label("", 13, color: Palette.Muted); root.AddView(status);
        activityProgress = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Indeterminate = true, Visibility = busy ? ViewStates.Visible : ViewStates.Gone };
        activityProgress.IndeterminateTintList = Android.Content.Res.ColorStateList.ValueOf(Palette.Accent);
        root.AddView(activityProgress, new LinearLayout.LayoutParams(-1, Dp(2)) { TopMargin = Dp(6), BottomMargin = Dp(2) });
        var scroll = new ScrollView(this) { FillViewport = true };
        scroll.SetClipToPadding(false);
        content = Column(); content.SetPadding(0, Dp(12), 0, Dp(18)); scroll.AddView(content);
        root.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        var windowContent = new FrameLayout(this); windowContent.SetBackgroundColor(Palette.Bg); windowContent.SetFitsSystemWindows(true);
        windowContent.AddView(root, new FrameLayout.LayoutParams(-1, -1)); SetContentView(windowContent);
    }
    Button Action(LinearLayout parent, string text, Func<Task> action, Tone tone = Tone.Secondary)
    {
        var fill = tone == Tone.Primary ? Palette.Accent : Palette.Surface;
        var textColor = tone == Tone.Primary ? Palette.OnAccent : tone == Tone.Danger ? Palette.Danger : Palette.Text;
        var stroke = tone == Tone.Primary ? Palette.Accent : tone == Tone.Danger ? Color.Argb(140, Palette.Danger.R, Palette.Danger.G, Palette.Danger.B) : Palette.SurfaceStroke;
        var button = new Button(this) { Text = text, TextSize = 16, Gravity = GravityFlags.Center };
        button.SetAllCaps(false); button.SetTextColor(textColor);
        var ripple = tone == Tone.Primary ? Color.Argb(60, 255, 255, 255) : Color.Argb(40, 255, 255, 255);
        button.Background = new Android.Graphics.Drawables.RippleDrawable(Android.Content.Res.ColorStateList.ValueOf(ripple), Surface(14, fill, stroke, tone == Tone.Primary ? 0 : 1), null);
        button.SetTypeface(Android.Graphics.Typeface.Create("sans-serif-medium", Android.Graphics.TypefaceStyle.Normal), Android.Graphics.TypefaceStyle.Normal);
        button.SetPadding(Dp(16), Dp(10), Dp(16), Dp(10)); button.SetMinHeight(Dp(58)); button.StateListAnimator = null;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) button.TooltipText = text.Replace("\n", " · ");
        button.Click += async (_, _) => { if (!busy) await Run(action); };
        var layout = new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(8), BottomMargin = Dp(2) };
        buttons.Add(button); parent.AddView(button, layout); return button;
    }
    EditText Input(LinearLayout parent, string label, string text = "", bool secret = false)
    {
        parent.AddView(Label(label, 13, color: Palette.Muted));
        var field = new EditText(this) { Text = text, TextSize = 17,
            InputType = secret ? InputTypes.ClassText | InputTypes.TextVariationPassword : InputTypes.ClassText | InputTypes.TextFlagNoSuggestions };
        field.SetSingleLine(true); field.SetTextColor(Palette.Text); field.SetHintTextColor(Palette.Muted);
        field.Background = Surface(12, Color.Rgb(0x10, 0x12, 0x18)); field.SetPadding(Dp(16), Dp(14), Dp(16), Dp(14)); field.SetMinHeight(Dp(58));
        field.SetSelectAllOnFocus(true); field.SaveEnabled = false;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) field.ImportantForAutofill = ImportantForAutofill.NoExcludeDescendants;
        var layout = new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(14) };
        parent.AddView(field, layout); return field;
    }
    async Task<int> Choose(string title, string[] items)
    {
        var answer = new TaskCompletionSource<int>();
        var dialog = new AlertDialog.Builder(this)!.SetTitle(title)!
            .SetAdapter(new TouchChoices(this, items), (_, args) => answer.TrySetResult(args.Which))!
            .SetNegativeButton("Cancel", (_, _) => answer.TrySetResult(-1))!.Create()!;
        dialog.CancelEvent += (_, _) => answer.TrySetResult(-1); dialog.Show(); return await answer.Task;
    }
    sealed class TouchChoices(Activity owner, string[] items) : ArrayAdapter<string>(owner, Android.Resource.Layout.SimpleListItem1, items)
    {
        public override View GetView(int position, View? convertView, ViewGroup parent)
        {
            var view = (TextView)base.GetView(position, convertView, parent);
            var density = owner.Resources!.DisplayMetrics!.Density;
            view.SetMinHeight((int)(64 * density)); view.SetPadding((int)(24 * density), (int)(14 * density), (int)(24 * density), (int)(14 * density));
            view.TextSize = 17; view.SetTextColor(MainActivity.Palette.Text); view.SetSingleLine(false); return view;
        }
    }
    async Task Run(Func<Task> action, bool quiet = false)
    {
        if (busy) return;
        busy = true; activityProgress.Visibility = ViewStates.Visible; foreach (var b in buttons) b.Enabled = false;
        try { await action(); }
        catch (System.OperationCanceledException) { if (!IsDestroyed) status.Text = "Operation cancelled."; }
        catch (Exception ex)
        {
            if (IsDestroyed) return;
            if (Intent?.GetBooleanExtra("offlineSelfTest", false) == true) Android.Util.Log.Error("ArrisControlSelfTest", ex.ToString());
            status.Text = ex is RouterException ? ex.Message :
                ex is System.Net.Http.HttpRequestException requestFailure ? RouterDiscovery.ConnectionFailure(requestFailure, address?.Text ?? "the selected router") :
                "The operation could not complete. No success was assumed. Check your connection and try again.";
            if (quiet) status.Text = "Couldn't reconnect automatically: " + status.Text;
            else await Message("Operation result", status.Text ?? "Operation failed.");
        }
        finally
        {
            busy = false;
            if (!IsDestroyed) { activityProgress.Visibility = ViewStates.Gone; foreach (var b in buttons) b.Enabled = true; networkStatus.Text = AndroidNetwork.Status(); }
        }
    }
    Task<bool> Confirm(string title, string body, string accept = "Apply change")
    {
        var answer = new TaskCompletionSource<bool>();
        var dialog = new AlertDialog.Builder(this)!.SetTitle(title)!.SetMessage(body)!
            .SetPositiveButton(accept, (_, _) => answer.TrySetResult(true))!
            .SetNegativeButton("Cancel", (_, _) => answer.TrySetResult(false))!.Create()!;
        dialog.CancelEvent += (_, _) => answer.TrySetResult(false); dialog.Show(); return answer.Task;
    }
    Task Message(string title, string text)
    {
        var done = new TaskCompletionSource();
        var dialog = new AlertDialog.Builder(this)!.SetTitle(title)!.SetMessage(text)!
            .SetPositiveButton("OK", (_, _) => done.TrySetResult())!.Create()!;
        dialog.CancelEvent += (_, _) => done.TrySetResult(); dialog.Show(); return done.Task;
    }
    async Task<bool> Discard() => !dirty || await Confirm("Unsaved changes", "Discard the edits on this page?", "Discard");
    void ShowConnect()
    {
        Shell("Arris Control"); dirty = false; addressEdited = false;
        var prefs = GetSharedPreferences("connection", FileCreationMode.Private)!;
        var records = CredentialStore.Find();
        var lastAddress = prefs.GetString("address", records.FirstOrDefault()?.Address ?? "192.168.7.1")!;
        if (records.Count > 0)
        {
            var saved = Card("Your routers", "Connects over your Wi-Fi. No sign-in needed.",
                chipText: records.Count == 1 ? "Saved" : records.Count + " saved", chipColor: Palette.Success);
            foreach (var record in records) Action(saved, "Connect\n" + record.Serial, () => Connect(record, AddressOverride()), Tone.Primary);
        }
        var pendingSetups = OfflineSetupStore.Find();
        if (pendingSetups.Count > 0)
        {
            var pending = Card("Pending offline setup", "Join the router's Wi-Fi, then verify the proposed login locally. No ARRIS sign-in needed.");
            foreach (var entry in pendingSetups.DistinctBy(x => x.Draft.Device.Serial))
                Action(pending, "Verify local login\n" + entry.Draft.Device.Serial, () => { ShowOfflineVerification(entry.Path, entry.Draft); return Task.CompletedTask; }, Tone.Primary);
        }
        var account = Card(records.Count > 0 ? "Add another router" : "Sign in to get started",
            "Use your ARRIS / SURFboard Central account. The app finds your router on this Wi-Fi and connects. Your password isn't saved.");
        email = Input(account, "Account email", prefs.GetString("email", "") ?? "");
        email.InputType = InputTypes.ClassText | InputTypes.TextVariationEmailAddress;
        password = Input(account, "Account password", secret: true);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            // Account fields (not router credentials) may be filled by the user's password manager.
            email.ImportantForAutofill = ImportantForAutofill.Yes; email.SetAutofillHints(View.AutofillHintEmailAddress);
            password.ImportantForAutofill = ImportantForAutofill.Yes; password.SetAutofillHints(View.AutofillHintPassword);
        }
        Action(account, "Sign in", EasySignIn, records.Count > 0 ? Tone.Secondary : Tone.Primary);

        var more = Card("More options");
        var advanced = Column(); advanced.Visibility = ViewStates.Gone;
        Action(more, "Show more options  ⌄", () => { advanced.Visibility = advanced.Visibility == ViewStates.Gone ? ViewStates.Visible : ViewStates.Gone; return Task.CompletedTask; });
        more.AddView(advanced);
        address = Input(advanced, "Router address (only if it changed)", lastAddress);
        address.TextChanged += (_, _) => addressEdited = true;
        Action(advanced, "Scan this Wi-Fi for ARRIS routers", ScanRouters);
        Action(advanced, "Wi-Fi settings", () => { StartActivity(new Intent(Android.Provider.Settings.ActionWifiSettings)); return Task.CompletedTask; });
        Action(advanced, "Choose a SURFboard Central APK file", PickReferenceFile);
        Action(advanced, "Use previously acquired credential", async () =>
        {
            if (!Directory.Exists(AcquisitionStore.Folder)) throw new RouterException("No account credential is saved on this installation yet.");
            var file = Directory.EnumerateFiles(AcquisitionStore.Folder, "account-*.json").OrderDescending().FirstOrDefault()
                ?? throw new RouterException("No saved account credential was found.");
            acquired = await Task.Run(() => AcquisitionStore.Load(file), lifetime.Token); ShowDevices();
        });
        Action(advanced, "Enter a local management login", async () =>
        {
            var form = Column(); form.SetPadding(Dp(20), Dp(4), Dp(20), Dp(4));
            var formScroll = new ScrollView(this); formScroll.AddView(form);
            var serial = Input(form, "Router serial number"); var mac = Input(form, "Router certificate MAC address");
            var user = Input(form, "Local management username / email"); var pass = Input(form, "Local management password (not Wi-Fi password)", secret: true);
            var done = new TaskCompletionSource<bool>();
            var dialog = new AlertDialog.Builder(this)!.SetTitle("Existing local credential")!.SetView(formScroll)!
                .SetPositiveButton("Verify", (_, _) => done.TrySetResult(true))!.SetNegativeButton("Cancel", (_, _) => done.TrySetResult(false))!.Create()!;
            dialog.CancelEvent += (_, _) => done.TrySetResult(false); dialog.Show();
            if (!await done.Task) return;
            var id = serial.Text!.Trim().ToUpperInvariant(); var hardware = Regex.Replace(mac.Text!.ToUpperInvariant(), "[^A-F0-9]", "");
            if (!Regex.IsMatch(id, "^[A-Z0-9]{12,24}$") || !Regex.IsMatch(hardware, "^[A-F0-9]{12}$")) throw new RouterException("Enter the complete router serial and 12-digit certificate MAC address.");
            if (string.IsNullOrEmpty(user.Text) || string.IsNullOrEmpty(pass.Text)) throw new RouterException("Enter the local management username and password.");
            var credential = new RouterCredential(user.Text, pass.Text); pass.Text = "";
            var device = new CloudDevice("ARRIS mAX", id, hardware); acquired = new(credential, [device]);
            await Verify(device);
        });
        Action(advanced, "Set up a new router over Bluetooth", BleScanAndPick);
        Action(advanced, "Setup and mesh diagnostics (read-only)", SetupDiagnostics);
        content.AddView(Label("Local control · credentials encrypted on this phone", 12, color: Palette.Muted));
    }
    async Task ScanRouters()
    {
        AndroidNetwork.UseLocal();
        status.Text = "Scanning your local network…";
        var candidates = AndroidNetwork.Candidates().Distinct().Take(1024).ToArray();
        var progress = new Progress<ScanProgress>(p => status.Text = $"Checking ARRIS APIs: {p.Checked}/{p.Total} addresses…");
        var results = await RouterDiscovery.DiscoverApisAsync(candidates, progress, lifetime.Token);
        if (results.Count == 0)
        {
            await Message("No ARRIS API found", "No matching ARRIS HTTPS API responded on this connected subnet. Check the Wi-Fi connection, client isolation, and the unit's power/cabling. The scan sends no credentials.");
            return;
        }
        scannedPins.Clear();
        foreach (var result in results) if (CertificatePin.IsValid(result.Pin)) scannedPins[result.Address] = result.Pin;
        var selected = await Choose("ARRIS routers found", results.Select(r => r.Address + "\nAPI responds · " + r.Mac + "\nKey " + r.Fingerprint).ToArray());
        if (selected >= 0)
        {
            address.Text = results[selected].Address;
            addressEdited = true;
            status.Text = "Router address selected (key " + results[selected].Fingerprint + "). Choose a saved router to connect. Saved routers only accept their pinned key.";
        }
    }
    // ---- SURFboard reference: found automatically, so sign-in is just email + password ----

    // Order: saved (encrypted) copy, a copy bundled into a personal build, the installed
    // SURFboard Central app; only then ask the user (Play Store install or file).
    async Task<CloudConfiguration?> ResolveConfigurationAsync()
    {
        status.Text = "Preparing sign-in…";
        var (found, alreadySaved) = await Task.Run<(CloudConfiguration? Config, bool Saved)>(() =>
        {
            try { if (CloudProtocol.LoadSaved() is { } saved) return (saved, true); } catch (RouterException) { }
            if (CloudProtocol.LoadEmbedded() is { } embedded) return (embedded, false);
            return (InstalledReference(), false);
        }, lifetime.Token);
        if (found is not null) { if (!alreadySaved) CloudProtocol.Save(found); return found; }
        var choice = await Choose("Sign-in needs SURFboard Central", [
            "Install SURFboard Central (recommended)\nFree on Google Play. Arris Control only reads its settings; you don't need to open or set it up.",
            "Choose a SURFboard Central APK/XAPK file"]);
        if (choice == 0)
        {
            OpenPlayStore();
            status.Text = "When SURFboard Central has installed, come back and tap Sign in again.";
            return null;
        }
        return choice == 1 ? await PickAndImportReferenceAsync() : null;
    }

    // Reads the protocol constants from the installed SURFboard Central APK and splits.
    // Nothing from it is installed, executed or launched. Null when it isn't installed.
    CloudConfiguration? InstalledReference()
    {
        Android.Content.PM.ApplicationInfo app;
        try { app = PackageManager!.GetApplicationInfo("com.arris.sbc", PackageInfoFlags.MetaData)!; }
        catch (Android.Content.PM.PackageManager.NameNotFoundException) { return null; }
        var paths = new[] { app.SourceDir }.Concat(app.SplitSourceDirs ?? []).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToArray();
        return CloudProtocol.ReadReferences(paths);
    }

    void OpenPlayStore()
    {
        try { StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse("market://details?id=com.arris.sbc"))); }
        catch (ActivityNotFoundException) { StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse("https://play.google.com/store/apps/details?id=com.arris.sbc"))); }
    }

    async Task<CloudConfiguration?> PickAndImportReferenceAsync()
    {
        pickedReference = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var intent = new Intent(Intent.ActionOpenDocument); intent.SetType("*/*"); intent.AddCategory(Intent.CategoryOpenable);
        StartActivityForResult(intent, ImportReference);
        var uri = await pickedReference.Task; pickedReference = null;
        if (uri is null) { status.Text = "No file chosen."; return null; }
        var imported = await ImportReferenceUri(uri);
        CloudProtocol.Save(imported); configuration = imported;
        status.Text = "SURFboard settings imported and encrypted on this phone.";
        return imported;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == ImportReference) pickedReference?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
    }

    async Task<CloudConfiguration> ImportReferenceUri(Android.Net.Uri uri)
    {
        // Keep the original extension, including a Java BuildConfig reference.
        string name = "reference.apk";
        using (var cursor = ContentResolver!.Query(uri, null, null, null, null))
            if (cursor?.MoveToFirst() == true) { var column = cursor.GetColumnIndex("_display_name"); if (column >= 0) name = cursor.GetString(column) ?? name; }
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is not (".apk" or ".xapk" or ".java")) throw new RouterException("Select a SURFboard Central APK, XAPK, or BuildConfig.java reference.");
        var temporary = Path.Combine(CacheDir!.AbsolutePath, Guid.NewGuid().ToString("N") + extension);
        try
        {
            using (var source = ContentResolver!.OpenInputStream(uri)!)
            await using (var file = File.Create(temporary))
            {
                var buffer = new byte[65536]; int count; long total = 0;
                while ((count = await source.ReadAsync(buffer, lifetime.Token)) > 0)
                {
                    total += count; if (total > 300L * 1024 * 1024) throw new RouterException("Reference exceeds 300 MB.");
                    await file.WriteAsync(buffer.AsMemory(0, count), lifetime.Token);
                }
            }
            return await Task.Run(() => CloudProtocol.ReadReference(temporary), lifetime.Token);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    async Task PickReferenceFile()
    {
        if (await PickAndImportReferenceAsync() is not null) await Message("Ready", "SURFboard settings imported. Enter your account email and password, then tap Sign in.");
    }

    // ---- Bluetooth: scan, read-only inspect, and guided per-write setup ----

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Android.Content.PM.Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != BlePermissions) return;
        var granted = grantResults.Length > 0 && grantResults.All(r => r == Android.Content.PM.Permission.Granted);
        permissionResult?.TrySetResult(granted);
    }

    async Task<bool> EnsureBlePermissions()
    {
        string[] needed = Build.VERSION.SdkInt >= BuildVersionCodes.S
            ? [Android.Manifest.Permission.BluetoothScan, Android.Manifest.Permission.BluetoothConnect]
            : [Android.Manifest.Permission.AccessFineLocation];
        if (needed.All(p => CheckSelfPermission(p) == Android.Content.PM.Permission.Granted)) return true;
        permissionResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestPermissions(needed, BlePermissions);
        return await permissionResult.Task;
    }

    async Task BleScanAndPick()
    {
        if (!await EnsureBlePermissions())
            throw new RouterException("Bluetooth permission is needed to find a nearby router. You can grant it in Android Settings → Apps → Arris Control.");
        if (!MaxBle.Supported(this))
            throw new RouterException("Turn on Bluetooth to set up a new router, then try again.");
        status.Text = "Scanning over Bluetooth…";
        ble?.Dispose(); ble = new BleManager(this);
        var devices = await ble.ScanAsync(TimeSpan.FromSeconds(7), lifetime.Token);
        if (devices.Count == 0)
        {
            await Message("No Bluetooth devices", "No Bluetooth devices answered. A mAX router advertises over Bluetooth only during setup — power it on (or factory-reset it) and keep this phone close.");
            return;
        }
        var choice = await Choose("Nearby Bluetooth devices", devices.Select(d => d.ToString()).ToArray());
        if (choice < 0) return;
        await BleConnectAndInspect(devices[choice]);
    }

    async Task BleConnectAndInspect(BleDevice device)
    {
        status.Text = "Connecting over Bluetooth to " + (string.IsNullOrEmpty(device.Name) ? device.Address : device.Name) + "…";
        await ble!.ConnectAsync(device.Device, lifetime.Token);

        string serial = await BleReadText(MaxBle.Serial), mac = await BleReadText(MaxBle.WanMac);
        string model = await BleReadText(MaxBle.ModelNumber), mode = await BleReadText(MaxBle.DeviceMode);
        string wan = await BleReadText(MaxBle.WanStatus), obaState = await BleReadText(MaxBle.ObaState);

        Shell("Router over Bluetooth"); backAction = BackToStart;
        var looksArris = serial.Length > 0 || model.Length > 0;
        var info = Card(string.IsNullOrEmpty(device.Name) ? "Connected device" : device.Name,
            chipText: looksArris ? "mAX" : "unknown", chipColor: looksArris ? Palette.Success : Palette.Muted);
        void Row(string k, string v) { if (!string.IsNullOrEmpty(v)) info.AddView(Label(k + ":  " + v, 14)); }
        Row("Model", model); Row("Serial", serial); Row("WAN MAC", mac);
        Row("Routing mode", mode); Row("WAN status", wan); Row("Onboarding state", obaState);
        if (!looksArris) info.AddView(Label("This device didn't return mAX identity values. It may not be an ARRIS router, or not in setup mode.", 13, color: Palette.Warning));

        var next = Card("What next");
        Action(next, "Guided setup (experimental)", () => BleGuidedSetup(serial, mac, model), Tone.Primary);
        Action(next, "Disconnect", () => { ble?.Dispose(); ble = null; ShowConnect(); return Task.CompletedTask; });
        content.AddView(Label("Inspection is read-only. Offline setup is experimental; each phase needs confirmation.", 12, color: Palette.Muted));
    }

    // Read-only survey of what nearby mAX units expose during setup/pairing. Never writes, and never
    // reads secret characteristics (key, credential slots, passphrases); those are listed by presence only.
    async Task SetupDiagnostics()
    {
        if (!await EnsureBlePermissions())
            throw new RouterException("Bluetooth permission is needed for setup diagnostics. You can grant it in Android Settings → Apps → Arris Control.");
        if (!MaxBle.Supported(this)) throw new RouterException("Turn on Bluetooth, then run setup diagnostics again.");
        status.Text = "Surveying nearby Bluetooth devices for 10 seconds…";
        ble?.Dispose(); ble = new BleManager(this);
        var devices = await ble.ScanAsync(TimeSpan.FromSeconds(10), lifetime.Token);
        static bool IsMax(BleDevice d) =>
            (d.ServiceUuids ?? []).Any(u => u.Equals(MaxBle.ServiceOba, StringComparison.OrdinalIgnoreCase) || u.Equals(MaxBle.ServiceLca, StringComparison.OrdinalIgnoreCase)) ||
            Regex.IsMatch(d.Name, "ARRIS|SURFboard|mAX|^W\\d{2,3}", RegexOptions.IgnoreCase);
        var report = new System.Text.StringBuilder();
        var version = PackageManager!.GetPackageInfo(PackageName!, 0)?.VersionName ?? "?";
        report.AppendLine($"Arris Control setup diagnostics · Android {Build.VERSION.Release} · app {version} · {DateTime.Now:yyyy-MM-dd HH:mm}");
        report.AppendLine("Read-only: nothing was written. Secret characteristics are listed but never read. MACs and non-ARRIS names are replaced.");
        var candidates = devices.Where(IsMax).ToList();
        report.AppendLine().AppendLine($"Bluetooth survey: {devices.Count} devices, {candidates.Count} look like mAX units");
        foreach (var d in devices.OrderByDescending(IsMax).ThenByDescending(d => d.Rssi).Take(25))
        {
            var label = IsMax(d) ? (string.IsNullOrEmpty(d.Name) ? "(unnamed mAX)" : d.Name) : "<other device>";
            var services = string.Join(",", (d.ServiceUuids ?? []).Select(MaxBle.Short));
            var makers = string.Join(",", (d.ManufacturerIds ?? []).Select(m => "0x" + m.ToString("X4")));
            report.AppendLine($"  {label} · {d.Rssi} dBm · services [{services}] · manufacturers [{makers}]");
        }
        if (candidates.Count > 0)
        {
            var labels = candidates.Select(d => (string.IsNullOrEmpty(d.Name) ? "(unnamed mAX)" : d.Name) + "\n" + d.Rssi + " dBm").Append("Skip mapping").ToArray();
            var pick = await Choose("Map which unit's Bluetooth services?", labels);
            if (pick >= 0 && pick < candidates.Count)
            {
                var target = candidates[pick];
                status.Text = "Connecting to map Bluetooth services (read-only)…";
                await ble.ConnectAsync(target.Device, lifetime.Token);
                var map = ble.Map();
                report.AppendLine().AppendLine($"Service map for {(string.IsNullOrEmpty(target.Name) ? "(unnamed mAX)" : target.Name)}: {map.Count} characteristics");
                foreach (var group in map.GroupBy(m => m.Service))
                {
                    report.AppendLine($"  service {MaxBle.Short(group.Key)}");
                    foreach (var m in group)
                        report.AppendLine($"    {MaxBle.Short(m.Characteristic)} · {m.Properties}{(MaxBle.NeverRead.Contains(m.Characteristic) ? " · secret, not read" : "")}");
                }
                report.AppendLine().AppendLine("Safe values:");
                foreach (var (uuid, label) in MaxBle.SafeReads)
                {
                    if (!ble.Has(uuid)) { report.AppendLine($"  {label}: (not exposed)"); continue; }
                    var value = await BleReadText(uuid);
                    var shown = uuid == MaxBle.Serial ? (value.Length == 0 ? "(empty)" : $"<serial, {value.Length} chars>") : Diagnostics.Value(label.Replace(" ", "_").ToUpperInvariant(), value);
                    report.AppendLine($"  {label}: {shown}");
                }
                ble.Dispose(); ble = null;
            }
        }
        report.AppendLine().AppendLine("Please also note (type these when you paste):");
        report.AppendLine("  1. Wi-Fi networks the phone sees named like SURFBoard-Onboarding (open or secured?)");
        report.AppendLine("  2. Light colour/pattern on each unit, and whether the satellite is wired to the main router");
        report.AppendLine("  3. If you also tried the official app with ARRIS servers blocked: the last step it reached and any error");
        report.AppendLine("  4. From the main router, run Diagnostics (section list) and paste that report too");
        ShowReport("Setup diagnostics", "Read-only survey of what nearby mAX units expose for setup and pairing.", report.ToString());
    }

    void ShowReport(string title, string description, string report)
    {
        Shell(title); backAction = BackToStart;
        var card = Card("Report", description, chipText: "Read-only", chipColor: Palette.Success);
        Action(card, "Copy report", () =>
        {
            var clipboard = (Android.Content.ClipboardManager)GetSystemService(ClipboardService)!;
            clipboard.PrimaryClip = ClipData.NewPlainText(title, report);
            status.Text = "Report copied. Paste it wherever you need it."; return Task.CompletedTask;
        }, Tone.Primary);
        Action(card, "Share report", () =>
        {
            var send = new Intent(Intent.ActionSend); send.SetType("text/plain"); send.PutExtra(Intent.ExtraText, report);
            StartActivity(Intent.CreateChooser(send, "Share " + title.ToLowerInvariant())); return Task.CompletedTask;
        });
        Action(card, "Back", () => { ble?.Dispose(); ble = null; ShowConnect(); return Task.CompletedTask; });
        var text = Label(report, 11, color: Palette.Text);
        text.SetTypeface(Android.Graphics.Typeface.Monospace, Android.Graphics.TypefaceStyle.Normal); text.SetTextIsSelectable(true);
        card.AddView(text);
        status.Text = title + " finished.";
    }

    async Task<string> BleReadText(string uuid)
    {
        try { var bytes = await ble!.ReadAsync(uuid, lifetime.Token); return System.Text.Encoding.UTF8.GetString(bytes).Trim('\0', ' ', '\r', '\n'); }
        catch (RouterException) { return ""; }
    }

    Task BleGuidedSetup(string serial, string mac, string model)
    {
        var device = new CloudDevice(model, serial.Trim().ToUpperInvariant(), BleSetupProtocol.NormalizeMac(mac));
        BleSetupProtocol.ValidateIdentity(device);
        Shell("Offline first-time setup"); backAction = BackToStart;
        var info = Card("Experimental · factory-reset unit only", device.ToString(), chipText: "No cloud", chipColor: Palette.Warning);
        info.AddView(Label("This attempts local setup over Bluetooth without an ARRIS account. The text format and fragment order were checked against SURFboard Central 4.3.5. Whether your firmware finishes without cloud registration still needs hardware testing. Use a spare unit you can reset; this changes its local login and Wi-Fi.", 14, color: Palette.Muted));
        var form = Card("Local login and Wi-Fi", "Use printable ASCII for this experimental flow. These are new local credentials, independent of an ARRIS account.");
        var user = Input(form, "Local username", "admin@router.local");
        var admin = Input(form, "Local admin password (12–64 characters)", GeneratePassword(), secret: true);
        var ssid = Input(form, "Wi-Fi name (1–32 characters)");
        var pass = Input(form, "Wi-Fi password (8–63 characters)", secret: true);
        var steps = Card("Setup steps", "Each phase sends its displayed sequence once, in order, waiting for every acknowledgement. A failed or uncertain phase locks all further writes.");
        var writes = Column(); writes.Visibility = ViewStates.Gone; steps.AddView(writes);
        var stage1 = Column(); var stage2 = Column(); var stage3 = Column();
        writes.AddView(stage1); writes.AddView(stage2); writes.AddView(stage3);
        stage2.Visibility = stage3.Visibility = ViewStates.Gone;
        BleSetupSession? session = null; OfflineSetupDraft? draft = null; string? pendingPath = null;
        var recovery = Card("Verify after joining Wi-Fi", "The proposed login is saved encrypted before any write. Verification checks the router's certificate, authenticated serial and protected settings; a Bluetooth acknowledgement alone is not success.");
        recovery.Visibility = ViewStates.Gone;
        Action(recovery, "Join Wi-Fi and verify local login", () =>
        {
            ble?.Dispose(); ble = null; ShowOfflineVerification(pendingPath!, draft!); return Task.CompletedTask;
        }, Tone.Primary);
        Action(recovery, "Export Bluetooth activity log", () => { ShowReport("Bluetooth activity log", "Setup steps and lengths; provisioning values are withheld.", BleLog.Export()); return Task.CompletedTask; });
        Button? unlock = null;
        unlock = Action(steps, "Unlock this setup attempt", async () =>
        {
            var candidate = new OfflineSetupDraft(device, new RouterCredential(user.Text?.Trim() ?? "", admin.Text ?? ""), ssid.Text ?? "");
            BleSetupProtocol.Validate(candidate, pass.Text ?? "");
            if (!await Confirm("Attempt offline setup on this unit?",
                "Target: " + device + "\n\nConfirm this is your factory-reset spare router. Three separate phases will change the local login, Wi-Fi, and setup state. The firmware may still require cloud registration. A partial attempt may need a factory reset. The app never resets a router automatically.", "Unlock attempt")) return;
            writes.Visibility = ViewStates.Visible; unlock!.Visibility = ViewStates.Gone;
            status.Text = "Unlocked. No router writes have been sent.";
        }, Tone.Danger);
        async Task Step(Func<Task> action)
        {
            try { await action(); }
            catch
            {
                writes.Visibility = ViewStates.Gone; unlock!.Visibility = ViewStates.Gone;
                ble?.Dispose(); ble = null; throw;
            }
        }
        Action(stage1, "1 · Write local login", () => Step(async () =>
        {
            draft = new OfflineSetupDraft(device, new RouterCredential(user.Text?.Trim() ?? "", admin.Text ?? ""), ssid.Text ?? "");
            var wifi = pass.Text ?? ""; BleSetupProtocol.Validate(draft, wifi);
            if (!await Confirm("Write the local login?",
                "Send SET:1, then four username fragments (slots 4 → 1); send SET:2, then four admin-password fragments (slots 4 → 1). Ten acknowledged writes, no retries.\n\nThe proposed login will be saved encrypted on this phone first.", "Write local login")) return;
            pendingPath = OfflineSetupStore.Save(draft);
            recovery.Visibility = ViewStates.Visible;
            user.Enabled = admin.Enabled = ssid.Enabled = pass.Enabled = false;
            session = new BleSetupSession(ble!, draft, wifi);
            await session.WriteCredentialsAsync(lifetime.Token);
            stage1.Visibility = ViewStates.Gone; stage2.Visibility = ViewStates.Visible;
            status.Text = "Local-login writes acknowledged. HTTPS login is not verified yet.";
        }), Tone.Primary);
        Action(stage2, "2 · Write Wi-Fi", () => Step(async () =>
        {
            if (!await Confirm("Write Wi-Fi settings?",
                "Network: " + draft!.Ssid + "\n\nSend four network-name fragments, then four Wi-Fi-password fragments, each in slot order 4 → 1. Eight acknowledged writes, no retries.", "Write Wi-Fi")) return;
            await session!.WriteWifiAsync(lifetime.Token);
            pass.Text = ""; admin.Text = "";
            stage2.Visibility = ViewStates.Gone; stage3.Visibility = ViewStates.Visible;
            status.Text = "Wi-Fi writes acknowledged. Join and verify after the final step.";
        }), Tone.Primary);
        Action(stage3, "3 · Finish Bluetooth setup", () => Step(async () =>
        {
            if (!await Confirm("Send setup-complete command?",
                "Send SETOBCOMPLETE once. Bluetooth may disconnect and the router may restart its Wi-Fi. No cloud registration or account upload is performed. You must still verify the local login.", "Finish attempt")) return;
            await session!.CompleteAsync(lifetime.Token);
            writes.Visibility = ViewStates.Gone; ble?.Dispose(); ble = null;
            status.Text = "Setup-complete command acknowledged. Join the new Wi-Fi and verify the local login.";
        }), Tone.Danger);
        var log = Card("Activity");
        Action(log, "Export Bluetooth activity log", () => { ShowReport("Bluetooth activity log", "Provisioning values are withheld.", BleLog.Export()); return Task.CompletedTask; });
        Action(log, "Disconnect Bluetooth", () => { ble?.Dispose(); ble = null; ShowConnect(); return Task.CompletedTask; });
        return Task.CompletedTask;
    }

    void ShowOfflineVerification(string pendingPath, OfflineSetupDraft draft)
    {
        Shell("Verify offline setup"); backAction = BackToStart;
        var info = Card("Pending local login", draft.Device.ToString(), chipText: "Unverified", chipColor: Palette.Warning);
        info.AddView(Label("Join “" + draft.Ssid + "” or connect to this router over Ethernet. Mobile data can be off. This page uses only the local API and needs no SURFboard reference or ARRIS account.", 14));
        Action(info, "Open Wi-Fi settings", () => { StartActivity(new Intent(Android.Provider.Settings.ActionWifiSettings)); return Task.CompletedTask; }, Tone.Primary);
        var verify = Card("Find and verify the router");
        address = Input(verify, "Private IPv4 address", "192.168.7.1");
        Action(verify, "Find this router on Wi-Fi and verify", async () =>
        {
            AndroidNetwork.UseLocal(); status.Text = "Looking for the pending router on this local network…";
            var found = await RouterDiscovery.DiscoverApisAsync(AndroidNetwork.Candidates().Distinct().Take(1024).ToArray(), null, lifetime.Token);
            var matches = found.Where(x => x.Mac == draft.Device.Mac).ToArray();
            if (matches.Length != 1) throw new RouterException(matches.Length == 0 ? "The pending router did not answer. Check its Wi-Fi or enter its address." : "Multiple devices used this identity. Inspect the network before attempting login.");
            await VerifyOfflineDraft(pendingPath, draft, matches[0].Address, matches[0].Pin);
        }, Tone.Primary);
        Action(verify, "Verify at entered address", () => VerifyOfflineDraft(pendingPath, draft, address.Text?.Trim() ?? "", null));
        info.AddView(Label("If login fails, this pending record remains available on the start screen. No credentials or setup writes are changed by verification.", 13, color: Palette.Muted));
    }

    async Task VerifyOfflineDraft(string path, OfflineSetupDraft draft, string target, string? pin)
    {
        status.Text = "Checking the proposed local login…"; AndroidNetwork.UseLocal();
        var record = await CloudAcquisition.VerifyAndSaveAsync(new(draft.Credential, [draft.Device]), draft.Device, target, lifetime.Token, pin,
            "User-confirmed experimental Bluetooth setup, then authenticated local serial/mode reads");
        // A verified credential is safely stored before the pending record is removed.
        try { File.Delete(path); } catch (IOException) { }
        await Connect(record);
    }

    // A strong local admin password: mixed case, digits and a symbol, generated on-device.
    static string GeneratePassword()
    {
        const string lower = "abcdefghijkmnpqrstuvwxyz", upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", digits = "23456789", symbols = "!@#$%*-_";
        const string all = lower + upper + digits + symbols;
        var rng = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var chars = new char[16];
        chars[0] = lower[rng[0] % lower.Length]; chars[1] = upper[rng[1] % upper.Length];
        chars[2] = digits[rng[2] % digits.Length]; chars[3] = symbols[rng[3] % symbols.Length];
        for (var i = 4; i < chars.Length; i++) chars[i] = all[rng[i] % all.Length];
        return new string(chars);
    }

    // One tap: sign in, pick the router (automatically if there's one), find it on this
    // Wi-Fi by its certificate, verify it and connect. No address typing.
    async Task EasySignIn()
    {
        var accountEmail = email.Text?.Trim() ?? ""; var accountPassword = password.Text ?? "";
        if (!System.Net.Mail.MailAddress.TryCreate(accountEmail, out _) || accountPassword.Length == 0)
            throw new RouterException("Enter your ARRIS account email and password.");
        GetSharedPreferences("connection", FileCreationMode.Private)!.Edit()!.PutString("email", accountEmail)!.Apply();
        configuration ??= await ResolveConfigurationAsync();
        if (configuration is null) return;
        password.Text = "";
        AndroidNetwork.UseInternet(); status.Text = "Signing in to your ARRIS account…";
        try
        {
            using var api = new CloudApi();
            acquired = await CloudAcquisition.AcquireAsync(accountEmail, accountPassword, configuration, api, lifetime.Token);
        }
        finally { accountPassword = ""; }
        AcquisitionStore.Save(acquired);
        await FinishSetupAsync();
    }

    async Task FinishSetupAsync()
    {
        var devices = acquired!.Devices;
        if (devices.Count == 0) { ShowDevices(); throw new RouterException("Your ARRIS account has no routers registered to it."); }
        var device = devices.Count == 1 ? devices[0] : null;
        if (device is null)
        {
            var index = await Choose("Which router?", devices.Select(d => d.ToString()).ToArray());
            if (index < 0) { ShowDevices(); return; }
            device = devices[index];
        }
        try { AndroidNetwork.UseLocal(); }
        catch (RouterException)
        {
            ShowDevices();
            status.Text = "Signed in. Join your router's Wi-Fi, then tap Find my router automatically.";
            return;
        }
        var candidates = AndroidNetwork.Candidates().Distinct().Take(1024).ToArray();
        var progress = new Progress<ScanProgress>(p => status.Text = $"Looking for your router on this Wi-Fi: {p.Checked}/{p.Total}…");
        var found = await RouterDiscovery.DiscoverApisAsync(candidates, progress, lifetime.Token);
        var match = found.Where(e => string.Equals(e.Mac, device.Mac, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count != 1)
        {
            ShowDevices();
            status.Text = match.Count == 0
                ? "Signed in, but your router didn't answer on this Wi-Fi. Check you're on its network, or enter its address below and tap it."
                : "More than one device answered with your router's identity. Enter its address below and tap it.";
            return;
        }
        status.Text = "Found it at " + match[0].Address + ". Verifying…";
        var record = await CloudAcquisition.VerifyAndSaveAsync(acquired!, device, match[0].Address, lifetime.Token, match[0].Pin);
        acquired = null; await Connect(record);
    }

    void ShowDevices()
    {
        var previousAddress = address?.Text ?? "192.168.7.1";
        Shell("Finish setup"); backAction = BackToStart;
        var auto = Card("Find your router", "Make sure this phone is on your router's Wi-Fi.", chipText: "Signed in", chipColor: Palette.Success);
        Action(auto, "Find my router automatically", FinishSetupAsync, Tone.Primary);
        var manual = Card("Or enter its address", "Then tap the router. The app checks its certificate, serial and routing mode before saving.");
        address = Input(manual, "Router IPv4 address", previousAddress);
        foreach (var device in acquired!.Devices) Action(manual, device.ToString(), () => Verify(device));
        if (acquired.Devices.Count == 0) manual.AddView(Label("The account returned no supported router identities. You can enter an existing local management login under More options.", 13));
        Action(content, "Back", () => { ShowConnect(); return Task.CompletedTask; });
    }
    async Task Verify(CloudDevice device)
    {
        status.Text = "Verifying the selected router locally…"; AndroidNetwork.UseLocal();
        var target = address.Text!.Trim();
        var record = await CloudAcquisition.VerifyAndSaveAsync(acquired!, device, target, lifetime.Token, scannedPins.GetValueOrDefault(target));
        acquired = null; await Connect(record);
    }

    // On launch, quietly reconnect to the last-used (or only) saved router when on Wi-Fi.
    void TryAutoConnect()
    {
        var records = CredentialStore.Find();
        if (records.Count == 0 || !AndroidNetwork.HasLocalNetwork) return;
        var last = GetSharedPreferences("connection", FileCreationMode.Private)!.GetString("serial", null);
        var record = records.FirstOrDefault(r => r.Serial == last) ?? (records.Count == 1 ? records[0] : null);
        if (record is not null) _ = Run(() => Connect(record), quiet: true);
    }
    string? AddressOverride() => addressEdited && !string.IsNullOrWhiteSpace(address?.Text) ? address.Text!.Trim() : null;

    async Task Connect(CredentialRecord record, string? addressOverride = null)
    {
        if (!string.IsNullOrEmpty(addressOverride))
        {
            if (!CredentialStore.IsPrivate(addressOverride)) throw new RouterException("Enter a private IPv4 address or use Scan for ARRIS routers.");
            record = record with { Address = addressOverride };
        }
        status.Text = "Connecting to " + record.Address + "…"; client?.Dispose(); client = new(record);
        client.Progress = message => RunOnUiThread(() => { if (!IsDestroyed) status.Text = message; });
        await Task.Run(() => client.ConnectAsync(lifetime.Token), lifetime.Token); dirty = false;
        GetSharedPreferences("connection", FileCreationMode.Private)!.Edit()!.PutString("address", client.Record.Address)!.PutString("serial", client.Record.Serial)!.Apply();
        currentPage = Catalog.Pages[0]; ShowRouter(); await Read();
    }
    void ShowRouter()
    {
        Shell(currentPage.Name);
        backAction = async () =>
        {
            if (!await Discard()) return;
            dirty = false;
            if (currentPage != Catalog.Pages[0]) { currentPage = Catalog.Pages[0]; ShowRouter(); await Read(); }
            else { client?.Dispose(); client = null; AndroidNetwork.UseInternet(); ShowConnect(); }
        };
        var navigation = Card("Connected locally", client!.Record.Address + " · " + client.Record.Serial, chipText: "Connected", chipColor: Palette.Success);
        Action(navigation, "Change section  ⌄", async () =>
        {
            var selected = await Choose("Router settings", Catalog.Pages.Select(p => p.Name).ToArray());
            if (selected < 0 || Catalog.Pages[selected] == currentPage || !await Discard()) return;
            currentPage = Catalog.Pages[selected]; dirty = false; ShowRouter(); await Read();
        });
        var bottom = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        root.AddView(bottom);
        var refresh = Action(bottom, "Refresh", async () => { if (await Discard()) { dirty = false; ShowRouter(); await Read(); } }, Tone.Primary);
        refresh.LayoutParameters = new LinearLayout.LayoutParams(0, Dp(60), 1) { RightMargin = Dp(6) };
        var connections = Action(bottom, "Connections", async () => { if (await Discard()) { client?.Dispose(); client = null; AndroidNetwork.UseInternet(); ShowConnect(); } });
        connections.LayoutParameters = new LinearLayout.LayoutParams(0, Dp(60), 1) { LeftMargin = Dp(6) };
    }
    async Task Read()
    {
        status.Text = "Reading " + currentPage.Name + "…";
        var snapshot = await Task.Run(() => client!.ReadPageAsync(currentPage, lifetime.Token), lifetime.Token);
        if (currentPage.Id == "diagnostics") { dirty = false; ShowDiagnostics(snapshot); return; }
        dirty = false; content.AddView(Label(currentPage.Description.Replace("this PC", "this phone")));
        if (currentPage.Id == "tools") ShowTools(snapshot);
        if ((currentPage.Id is "overview" or "internet") && snapshot.Data.TryGetValue("DEVICEOPERMODE_ALL", out var modes) && modes["DEVICEOPERMODE"] is JsonObject mode)
        {
            var value = Catalog.Text(mode["MODE"]);
            var bridge = Card("Bypass mode", value == "BRIDGE" ? "On · routing is handled by your main router." : "Off · this unit handles routing.");
            var definition = Settings.Definitions.Single(d => d.Container == "DEVICEOPERMODE");
            var section = new SettingSection(definition, "DEVICEOPERMODE", 0, "", Settings.Filter(definition, mode), "Bypass mode");
            if (value is "DUAL" or "IPV4ONLY") Action(bridge, "Turn bypass ON", () => Save(section, new() { ["MODE"] = () => "BRIDGE" }), Tone.Danger);
            if (value == "BRIDGE") Action(bridge, "Turn bypass OFF · restore router mode", () => Save(section, new() { ["MODE"] = () => "DUAL" }), Tone.Primary);
        }
        foreach (var section in Settings.Sections(snapshot)) Editor(section);
        foreach (var definition in Settings.Definitions.Where(d => d.Collection && snapshot.Data.ContainsKey(d.Key)))
            Action(content, "Add " + definition.Title.ToLowerInvariant(), () =>
            {
                Editor(new(definition, definition.Container, -1, "", new JsonObject(), "New " + definition.Title), true);
                return Task.CompletedTask;
            });
        var details = Card("Details", "Read-only fields returned by this firmware.");
        var fields = Label(string.Join("\n\n", snapshot.Rows.Select(row => row.Field + "\n" + row.Value)), 13);
        fields.SetTextIsSelectable(true); fields.Visibility = ViewStates.Gone;
        Action(details, "Show returned fields", () => { fields.Visibility = fields.Visibility == ViewStates.Gone ? ViewStates.Visible : ViewStates.Gone; return Task.CompletedTask; });
        details.AddView(fields);
        var failed = snapshot.Reads.Where(r => !r.Readable).Select(r => r.Key + ": " + r.Status).ToArray();
        if (failed.Length > 0) content.AddView(Label("Unavailable reads\n" + string.Join("\n", failed), 12));
        status.Text = $"{snapshot.Reads.Count(r => r.Readable)}/{snapshot.Reads.Count} queries read · {snapshot.Time:HH:mm:ss}.";
    }
    void ShowDiagnostics(Snapshot snapshot)
    {
        var version = PackageManager!.GetPackageInfo(PackageName!, 0)?.VersionName ?? "?";
        var report = Diagnostics.Report(snapshot, "Android", version);
        var ok = snapshot.Reads.Count(r => r.Readable);
        var card = Card("Diagnostics report", "Read-only — nothing on the router was changed. Values that could identify you (names, addresses, passwords) are replaced. Copy or share it to help add support for more settings.",
            chipText: $"{ok}/{snapshot.Reads.Count} answered", chipColor: ok > 0 ? Palette.Success : Palette.Warning);
        Action(card, "Copy report", () =>
        {
            var clipboard = (Android.Content.ClipboardManager)GetSystemService(ClipboardService)!;
            clipboard.PrimaryClip = ClipData.NewPlainText("Arris Control diagnostics", report);
            status.Text = "Diagnostics report copied. Paste it wherever you need it.";
            return Task.CompletedTask;
        }, Tone.Primary);
        Action(card, "Share report", () =>
        {
            var send = new Intent(Intent.ActionSend); send.SetType("text/plain"); send.PutExtra(Intent.ExtraText, report);
            StartActivity(Intent.CreateChooser(send, "Share diagnostics report")); return Task.CompletedTask;
        });
        var text = Label(report, 11, color: Palette.Text);
        text.SetTypeface(Android.Graphics.Typeface.Monospace, Android.Graphics.TypefaceStyle.Normal); text.SetTextIsSelectable(true);
        card.AddView(text);
        status.Text = $"Diagnostics finished · {ok}/{snapshot.Reads.Count} endpoints answered · {snapshot.Time:HH:mm:ss}.";
    }

    void ShowTools(Snapshot snapshot)
    {
        var speed = Card("Router speed test", "Measures your Internet speed from the router itself, so Wi-Fi doesn't affect the result. Takes about a minute.");
        if (snapshot.Data.TryGetValue("SPEEDTESTWAN_ALL", out var last))
            foreach (var row in Catalog.Flatten(last, "SPEEDTESTWAN_ALL").Where(r => r.Value != "[hidden]").Take(8))
                speed.AddView(Label(row.Field.Split('.').Last() + ":  " + row.Value, 13, color: Palette.Muted));
        Action(speed, "Run speed test", RunSpeedTest, Tone.Primary);
        var renew = Card("Renew Internet connection", "Asks your ISP for a fresh address. Useful when the connection is stuck; Internet drops for a few seconds.");
        Action(renew, "Renew IPv4 connection", async () =>
        {
            if (!await Confirm("Renew the Internet connection?", "The router asks your ISP for a new IPv4 address. Internet access drops for a few seconds.", "Renew")) { status.Text = "Cancelled; nothing was sent."; return; }
            status.Text = "Renewing the Internet connection…";
            await Task.Run(() => client!.RunActionAsync("WANIPCONFIG", new() { ["IPSTACK"] = "IPV4", ["RENEW"] = "true" }, lifetime.Token), lifetime.Token);
            status.Text = "Renew request sent. Give it a few seconds, then refresh Overview to see the Internet status.";
            await Message("Renew sent", status.Text);
        }, Tone.Danger);
    }

    async Task RunSpeedTest()
    {
        if (!await Confirm("Run a speed test?", "The router runs an Internet speed test. It uses your connection for about a minute.", "Start")) { status.Text = "Cancelled; nothing was sent."; return; }
        status.Text = "Starting the speed test on the router…";
        await Task.Run(() => client!.RunActionAsync("SPEEDTESTWAN", new() { ["DIAGSTATE"] = "Requested" }, lifetime.Token), lifetime.Token);
        JsonObject? result = null; var state = "";
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), lifetime.Token);
            var data = await Task.Run(() => client!.ReadAsync("SPEEDTESTWAN_ALL", lifetime.Token), lifetime.Token);
            var node = data["SPEEDTESTWAN"];
            var test = node as JsonObject ?? (node as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            state = Catalog.Text(test?["DIAGSTATE"]);
            status.Text = $"Speed test running… ({state}, {3 * (i + 1)} s)";
            result = data;
            if (state.Length > 0 && !Regex.IsMatch(state, "Requested|Progress|Running|Pending", RegexOptions.IgnoreCase)) break;
        }
        var summary = result is null ? "No result was returned." :
            string.Join("\n", Catalog.Flatten(result, "SPEEDTESTWAN_ALL").Where(r => r.Value != "[hidden]").Select(r => r.Field.Split('.').Last() + ": " + r.Value).Take(12));
        status.Text = "Speed test " + (state.Length > 0 ? state.ToLowerInvariant() : "finished") + ".";
        await Message("Speed test result", summary);
        ShowRouter(); await Read();
    }

    void Editor(SettingSection section, bool adding = false)
    {
        var editor = Card(section.Title);
        var getters = new Dictionary<string, Func<string>>();
        foreach (var field in section.Definition.Fields)
        {
            if (!adding && !section.Baseline.ContainsKey(field.Key) && field.Kind != "password") continue;
            var old = Catalog.Text(section.Baseline[field.Key]);
            if (field.Kind == "toggle")
            {
                var toggle = new Switch(this) { Text = field.Label, Checked = old == "1", TextSize = 15 };
                toggle.SetPadding(0, Dp(10), 0, Dp(10)); toggle.CheckedChange += (_, _) => dirty = true;
                toggle.SetMinHeight(Dp(60)); toggle.SetTextColor(Palette.Text); editor.AddView(toggle); getters[field.Key] = () => toggle.Checked ? "1" : "0";
            }
            else if (field.Kind is "choice" or "channel")
            {
                var choices = field.Kind == "channel" ? Settings.ChannelChoices(section.Container, old) : field.Choices!; var selected = old;
                string Caption() => field.Label + "\n" + (choices.FirstOrDefault(c => c.Value == selected)?.Label ?? "Choose a value");
                Button? choice = null;
                choice = Action(editor, Caption(), async () =>
                {
                    var index = await Choose(field.Label, choices.Select(c => c.Label).ToArray());
                    if (index < 0) return;
                    selected = choices[index].Value; choice!.Text = Caption(); dirty = true;
                });
                getters[field.Key] = () => selected;
            }
            else
            {
                var input = Input(editor, field.Label, field.Kind == "password" ? "" : old, field.Kind == "password");
                input.TextChanged += (_, _) => dirty = true; getters[field.Key] = () => input.Text ?? "";
                if (field.Hint.Length > 0) editor.AddView(Label(field.Hint, 12));
            }
        }
        Action(editor, adding ? "Review new rule" : "Review changes", () => Save(section, getters, adding ? "ADD" : "MODIFY"), Tone.Primary);
        if (section.Definition.Collection && !adding) Action(editor, "Remove rule", () => Save(section, [], "DELETE"), Tone.Danger);
    }
    async Task Save(SettingSection section, Dictionary<string, Func<string>> getters, string operation = "MODIFY")
    {
        status.Text = "Checking current settings and device identity…";
        var edits = getters.ToDictionary(pair => pair.Key, pair => pair.Value());
        var plan = await Task.Run(() => client!.PrepareAsync(section, edits, operation, lifetime.Token), lifetime.Token);
        if (!await Confirm("Review router change", plan.Preview.Replace("this PC", "this phone") + "\n\nApply once to " + client!.Record.Serial + "?"))
        { status.Text = "Cancelled. No update was sent."; return; }
        status.Text = "Sending the reviewed change once…";
        var result = await Task.Run(() => client!.ApplyAsync(plan, lifetime.Token), lifetime.Token);
        dirty = false; status.Text = result; await Message("Save result", result);
        ShowRouter(); await Read();
    }
}
