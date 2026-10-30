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
    Page currentPage = Catalog.Pages[0];
    CancellationTokenSource lifetime = new();
    readonly List<Button> buttons = [];
    bool busy, dirty;
    // Certificate pins seen by the last scan, so first-time verification requires the same certificate.
    readonly Dictionary<string, string> scannedPins = new(StringComparer.Ordinal);
    const int ImportReference = 41;

    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        // Credentials/passwords never enter screenshots, recent-task thumbnails or backups.
        Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
        ShowConnect();
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
    protected override void OnResume()
    {
        base.OnResume();
        if (networkStatus is not null) networkStatus.Text = AndroidNetwork.Status();
    }
    protected override void OnDestroy()
    {
        lifetime.Cancel(); client?.Dispose(); AndroidNetwork.UseInternet();
        lifetime.Dispose(); base.OnDestroy();
    }
    int Dp(int n) => (int)(n * Resources!.DisplayMetrics!.Density);
    TextView Label(string text, int size = 15, bool heading = false)
    {
        var view = new TextView(this) { Text = text, TextSize = size };
        view.SetTextColor(Color.White);
        view.SetTypeface(Android.Graphics.Typeface.Create(heading ? "sans-serif-medium" : "sans-serif", Android.Graphics.TypefaceStyle.Normal), Android.Graphics.TypefaceStyle.Normal);
        view.SetLineSpacing(Dp(2), 1.12f);
        view.SetPadding(0, Dp(heading ? 4 : 3), 0, Dp(heading ? 12 : 8));
        return view;
    }
    LinearLayout Column() => new(this) { Orientation = Orientation.Vertical };
    Android.Graphics.Drawables.GradientDrawable Surface(int stroke = 1)
    {
        var shape = new Android.Graphics.Drawables.GradientDrawable();
        shape.SetColor(Color.Black); shape.SetCornerRadius(Dp(16));
        shape.SetStroke(Dp(stroke), Color.Rgb(70,70,70)); return shape;
    }
    LinearLayout Card(string title, string? description = null, LinearLayout? parent = null)
    {
        var box = Column(); box.Background = Surface(); box.SetPadding(Dp(18), Dp(18), Dp(18), Dp(18));
        var layout = new LinearLayout.LayoutParams(-1, -2); layout.BottomMargin = Dp(18);
        (parent ?? content).AddView(box, layout);
        box.AddView(Label(title, 21, true));
        if (!string.IsNullOrEmpty(description)) box.AddView(Label(description, 14));
        return box;
    }
    void Shell(string subtitle)
    {
        buttons.Clear();
        root = Column(); root.SetBackgroundColor(Color.Black); root.FocusableInTouchMode = true; root.RequestFocus();
        root.SetPadding(Dp(20), Dp(18), Dp(20), Dp(12));
        var brand = Label("ARRIS / CONTROL", 11); brand.LetterSpacing = 0.18f; root.AddView(brand);
        root.AddView(Label(subtitle, 32, true));
        networkStatus = Label(AndroidNetwork.Status(), 12); root.AddView(networkStatus);
        status = Label("", 13); status.SetTextColor(Color.White); root.AddView(status);
        activityProgress = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Indeterminate = true, Visibility = busy ? ViewStates.Visible : ViewStates.Gone };
        activityProgress.IndeterminateTintList = Android.Content.Res.ColorStateList.ValueOf(Color.White);
        root.AddView(activityProgress, new LinearLayout.LayoutParams(-1, Dp(2)));
        var scroll = new ScrollView(this) { FillViewport = true };
        scroll.SetClipToPadding(false);
        content = Column(); content.SetPadding(0, Dp(8), 0, Dp(18)); scroll.AddView(content);
        root.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        var windowContent = new FrameLayout(this); windowContent.SetBackgroundColor(Color.Black); windowContent.SetFitsSystemWindows(true);
        windowContent.AddView(root, new FrameLayout.LayoutParams(-1, -1)); SetContentView(windowContent);
    }
    Button Action(LinearLayout parent, string text, Func<Task> action)
    {
        var button = new Button(this) { Text = text, TextSize = 16, Gravity = GravityFlags.Center };
        button.SetAllCaps(false); button.SetTextColor(Color.White); button.Background = new Android.Graphics.Drawables.RippleDrawable(Android.Content.Res.ColorStateList.ValueOf(Color.Argb(45,255,255,255)), Surface(), null);
        button.SetTypeface(Android.Graphics.Typeface.Create("sans-serif-medium", Android.Graphics.TypefaceStyle.Normal), Android.Graphics.TypefaceStyle.Normal);
        button.SetPadding(Dp(16), Dp(8), Dp(16), Dp(8)); button.SetMinHeight(Dp(60));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) button.TooltipText = text.Replace("\n", " · ");
        button.Click += async (_, _) => { if (!busy) await Run(action); };
        var layout = new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(8), BottomMargin = Dp(4) };
        buttons.Add(button); parent.AddView(button, layout); return button;
    }
    EditText Input(LinearLayout parent, string label, string text = "", bool secret = false)
    {
        parent.AddView(Label(label, 13));
        var field = new EditText(this) { Text = text, TextSize = 18,
            InputType = secret ? InputTypes.ClassText | InputTypes.TextVariationPassword : InputTypes.ClassText | InputTypes.TextFlagNoSuggestions };
        field.SetSingleLine(true); field.SetTextColor(Color.White); field.SetHintTextColor(Color.White);
        field.Background = Surface(); field.SetPadding(Dp(16), Dp(14), Dp(16), Dp(14)); field.SetMinHeight(Dp(60));
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
            view.TextSize = 17; view.SetTextColor(Color.White); view.SetSingleLine(false); return view;
        }
    }
    async Task Run(Func<Task> action)
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
            await Message("Operation result", status.Text ?? "Operation failed.");
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
        Shell("Connect to ARRIS"); dirty = false;
        var connection = Card("Find your ARRIS", "Join its Wi-Fi, then scan to find its current address.");
        Action(connection, "Wi-Fi settings", () => { StartActivity(new Intent(Android.Provider.Settings.ActionWifiSettings)); return Task.CompletedTask; });
        var records = CredentialStore.Find();
        var lastAddress = GetSharedPreferences("connection", FileCreationMode.Private)!.GetString("address", records.FirstOrDefault()?.Address ?? "192.168.7.1")!;
        address = Input(connection, "Router address", lastAddress);
        Action(connection, "Scan for ARRIS routers", ScanRouters);
        if (records.Count > 0)
        {
            var saved = Card("Saved routers", "Reconnect locally. No account sign-in or reference import needed.");
            foreach (var record in records) Action(saved, "Connect to router\n" + record.Serial, () => Connect(record, address.Text!.Trim()));
        }
        var setup = Card("First-time setup", "Use this once to acquire a local router login.");
        var account = Column();
        Action(setup, "ARRIS account sign-in", () => { account.Visibility = account.Visibility == ViewStates.Gone ? ViewStates.Visible : ViewStates.Gone; return Task.CompletedTask; });
        setup.AddView(account); account.Visibility = ViewStates.Gone;
        account.AddView(Label("Account passwords and cloud tokens are not saved.", 13));
        email = Input(account, "ARRIS account email"); email.InputType = InputTypes.ClassText | InputTypes.TextVariationEmailAddress;
        password = Input(account, "ARRIS account password", secret: true);
        Action(account, "Use installed SURFboard Central reference", LoadInstalledReference);
        Action(account, "Choose a reference file", () =>
        {
            var intent = new Intent(Intent.ActionOpenDocument); intent.SetType("*/*"); intent.AddCategory(Intent.CategoryOpenable);
            StartActivityForResult(intent, ImportReference); return Task.CompletedTask;
        });
        Action(account, "Sign in and find my routers", Acquire);
        Action(setup, "Use previously acquired credential", async () =>
        {
            if (!Directory.Exists(AcquisitionStore.Folder)) throw new RouterException("No account credential is saved on this installation yet.");
            var file = Directory.EnumerateFiles(AcquisitionStore.Folder, "account-*.json").OrderDescending().FirstOrDefault()
                ?? throw new RouterException("No saved account credential was found.");
            acquired = await Task.Run(() => AcquisitionStore.Load(file), lifetime.Token); ShowDevices();
        });
        Action(setup, "Enter a local management login", async () =>
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
        content.AddView(Label("Local control · credentials encrypted on this phone", 12));
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
            status.Text = "Router address selected (key " + results[selected].Fingerprint + "). Choose a saved router to connect. Saved routers only accept their pinned key.";
        }
    }
    async Task LoadInstalledReference()
    {
        Android.Content.PM.ApplicationInfo app;
        try { app = PackageManager!.GetApplicationInfo("com.arris.sbc", PackageInfoFlags.MetaData)!; }
        catch (Android.Content.PM.PackageManager.NameNotFoundException)
        { throw new RouterException("SURFboard Central is not installed in this Android profile. Use Import protocol reference with your own SURFboard Central 4.3.5 APK/XAPK or BuildConfig.java. A reference is not needed to reconnect with saved local credentials."); }
        var paths = new[] { app.SourceDir }.Concat(app.SplitSourceDirs ?? []).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToArray();
        status.Text = "Reading the installed app's APK and split references…";
        configuration = await Task.Run(() => CloudProtocol.ReadReferences(paths), lifetime.Token);
        CloudProtocol.Save(configuration);
        status.Text = "Protocol reference imported and encrypted on this phone.";
        await Message("Reference imported", "The supported protocol reference is ready for account sign-in. Saved local credentials do not need this step.");
    }
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != ImportReference || resultCode != Result.Ok || data?.Data is not { } uri) return;
        _ = Run(async () =>
        {
            // Try the original extension, including a Java BuildConfig reference.
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
                configuration = await Task.Run(() => CloudProtocol.ReadReference(temporary), lifetime.Token);
                CloudProtocol.Save(configuration); status.Text = "Reference imported. You can now sign in.";
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });
    }
    async Task Acquire()
    {
        configuration ??= CloudProtocol.LoadSaved();
        if (configuration is null) throw new RouterException("Import a SURFboard Central protocol reference first. Its app client secret is not bundled with Arris Control.");
        var accountEmail = email.Text!.Trim(); var accountPassword = password.Text!; password.Text = "";
        AndroidNetwork.UseInternet(); status.Text = "Signing in and reading your account inventory…";
        try
        {
            using var api = new CloudApi();
            acquired = await CloudAcquisition.AcquireAsync(accountEmail, accountPassword, configuration, api, lifetime.Token);
            AcquisitionStore.Save(acquired); ShowDevices();
        }
        finally { accountPassword = ""; }
    }
    void ShowDevices()
    {
        var previousAddress = address.Text ?? "192.168.7.1";
        Shell("Select your Arris");
        address = Input(content, "Router IPv4 address", previousAddress);
        content.AddView(Label("Choose the exact unit. The app checks its certificate, serial number and protected routing mode before saving the local credential."));
        foreach (var device in acquired!.Devices) Action(content, device.ToString(), () => Verify(device));
        if (acquired.Devices.Count == 0) content.AddView(Label("The account returned no supported router identities. You can enter an existing local management credential from the connection screen."));
        Action(content, "Back to connections", () => { ShowConnect(); return Task.CompletedTask; });
    }
    async Task Verify(CloudDevice device)
    {
        status.Text = "Verifying the selected router locally…"; AndroidNetwork.UseLocal();
        var target = address.Text!.Trim();
        var record = await CloudAcquisition.VerifyAndSaveAsync(acquired!, device, target, lifetime.Token, scannedPins.GetValueOrDefault(target));
        acquired = null; await Connect(record);
    }
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
        GetSharedPreferences("connection", FileCreationMode.Private)!.Edit()!.PutString("address", client.Record.Address)!.Apply();
        currentPage = Catalog.Pages[0]; ShowRouter(); await Read();
    }
    void ShowRouter()
    {
        Shell(currentPage.Name);
        var navigation = Card("Connected locally", client!.Record.Address + " · " + client.Record.Serial);
        Action(navigation, "Change section  ⌄", async () =>
        {
            var selected = await Choose("Router settings", Catalog.Pages.Select(p => p.Name).ToArray());
            if (selected < 0 || Catalog.Pages[selected] == currentPage || !await Discard()) return;
            currentPage = Catalog.Pages[selected]; dirty = false; ShowRouter(); await Read();
        });
        var bottom = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        root.AddView(bottom);
        var refresh = Action(bottom, "Refresh", async () => { if (await Discard()) { dirty = false; ShowRouter(); await Read(); } });
        refresh.LayoutParameters = new LinearLayout.LayoutParams(0, Dp(60), 1) { RightMargin = Dp(6) };
        var connections = Action(bottom, "Connections", async () => { if (await Discard()) { client?.Dispose(); client = null; AndroidNetwork.UseInternet(); ShowConnect(); } });
        connections.LayoutParameters = new LinearLayout.LayoutParams(0, Dp(60), 1) { LeftMargin = Dp(6) };
    }
    async Task Read()
    {
        status.Text = "Reading " + currentPage.Name + "…";
        var snapshot = await Task.Run(() => client!.ReadPageAsync(currentPage, lifetime.Token), lifetime.Token);
        dirty = false; content.AddView(Label(currentPage.Description.Replace("this PC", "this phone")));
        if ((currentPage.Id is "overview" or "internet") && snapshot.Data.TryGetValue("DEVICEOPERMODE_ALL", out var modes) && modes["DEVICEOPERMODE"] is JsonObject mode)
        {
            var value = Catalog.Text(mode["MODE"]);
            var bridge = Card("Bypass mode", value == "BRIDGE" ? "On · routing is handled by your main router." : "Off · this unit handles routing.");
            var definition = Settings.Definitions.Single(d => d.Container == "DEVICEOPERMODE");
            var section = new SettingSection(definition, "DEVICEOPERMODE", 0, "", Settings.Filter(definition, mode), "Bypass mode");
            if (value is "DUAL" or "IPV4ONLY") Action(bridge, "Turn bypass ON", () => Save(section, new() { ["MODE"] = () => "BRIDGE" }));
            if (value == "BRIDGE") Action(bridge, "Turn bypass OFF · restore router mode", () => Save(section, new() { ["MODE"] = () => "DUAL" }));
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
                toggle.SetMinHeight(Dp(60)); toggle.SetTextColor(Color.White); editor.AddView(toggle); getters[field.Key] = () => toggle.Checked ? "1" : "0";
            }
            else if (field.Kind == "choice")
            {
                var choices = field.Choices!; var selected = old;
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
        Action(editor, adding ? "Review new rule" : "Review changes", () => Save(section, getters, adding ? "ADD" : "MODIFY"));
        if (section.Definition.Collection && !adding) Action(editor, "Remove rule", () => Save(section, [], "DELETE"));
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
