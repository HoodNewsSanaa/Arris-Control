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
    EditText address = null!, email = null!, password = null!;
    CloudConfiguration? configuration;
    AcquiredCredential? acquired;
    RouterClient? client;
    Page currentPage = Catalog.Pages[0];
    CancellationTokenSource lifetime = new();
    readonly List<Button> buttons = [];
    bool busy, dirty;
    const int ImportReference = 41;
    readonly Color muted = Color.Rgb(165, 180, 201);
    readonly Color accent = Color.Rgb(99, 217, 180);

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
                status.Text = "PASS: offline protocol and Android Keystore checks.";
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
        view.SetTextColor(heading ? Color.White : muted);
        view.SetPadding(0, Dp(heading ? 12 : 5), 0, Dp(7));
        return view;
    }
    LinearLayout Column() => new(this) { Orientation = Orientation.Vertical };
    void Shell(string subtitle)
    {
        buttons.Clear();
        root = Column(); root.SetBackgroundColor(Color.Rgb(16, 23, 36));
        root.SetPadding(Dp(20), Dp(18), Dp(20), Dp(14)); root.SetFitsSystemWindows(true);
        root.AddView(Label("ARRIS CONTROL", 12));
        root.AddView(Label(subtitle, 27, true));
        networkStatus = Label(AndroidNetwork.Status(), 12); root.AddView(networkStatus);
        status = Label("", 13); status.SetTextColor(accent); root.AddView(status);
        var scroll = new ScrollView(this) { FillViewport = true };
        content = Column(); scroll.AddView(content);
        root.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1)); SetContentView(root);
    }
    Button Action(LinearLayout parent, string text, Func<Task> action)
    {
        var button = new Button(this) { Text = text };
        button.SetAllCaps(false); button.SetTextColor(accent); button.Click += async (_, _) => { if (!busy) await Run(action); };
        buttons.Add(button); parent.AddView(button, new LinearLayout.LayoutParams(-1, Dp(52)));
        return button;
    }
    EditText Input(LinearLayout parent, string label, string text = "", bool secret = false)
    {
        parent.AddView(Label(label, 13));
        var field = new EditText(this) { Text = text, TextSize = 16,
            InputType = secret ? InputTypes.ClassText | InputTypes.TextVariationPassword : InputTypes.ClassText | InputTypes.TextFlagNoSuggestions };
        field.SetSingleLine(true); field.SetTextColor(Color.White); field.SetSelectAllOnFocus(true); field.SaveEnabled = false;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) field.ImportantForAutofill = ImportantForAutofill.NoExcludeDescendants;
        parent.AddView(field); return field;
    }
    async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true; foreach (var b in buttons) b.Enabled = false;
        try { await action(); }
        catch (OperationCanceledException) { if (!IsDestroyed) status.Text = "Operation cancelled."; }
        catch (Exception ex)
        {
            if (IsDestroyed) return;
            status.Text = ex is RouterException ? ex.Message :
                ex is System.Net.Http.HttpRequestException ? "Connection failed. Check the local network, router address, and device identity." :
                "The operation could not complete. No success was assumed. Check your connection and try again.";
            await Message("Operation result", status.Text ?? "Operation failed.");
        }
        finally
        {
            busy = false;
            if (!IsDestroyed) { foreach (var b in buttons) b.Enabled = true; networkStatus.Text = AndroidNetwork.Status(); }
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
        Shell("Your router. Locally."); dirty = false;
        content.AddView(Label("Connect to your Arris network. Saved local credentials work without Internet when the router's management API is reachable."));
        Action(content, "Open Wi-Fi settings", () => { StartActivity(new Intent(Android.Provider.Settings.ActionWifiSettings)); return Task.CompletedTask; });
        var records = CredentialStore.Find();
        if (records.Count > 0)
        {
            content.AddView(Label("Saved routers", 20, true));
            foreach (var record in records) Action(content, record.ToString(), () => Connect(record));
        }
        address = Input(content, "Router IPv4 address", "192.168.7.1");
        content.AddView(Label("Sign in through your ARRIS account", 20, true));
        content.AddView(Label("One-time account sign-in retrieves your existing local router credential. Account passwords and cloud tokens are never saved."));
        email = Input(content, "ARRIS account email"); email.InputType = InputTypes.ClassText | InputTypes.TextVariationEmailAddress;
        password = Input(content, "ARRIS account password", secret: true);
        Action(content, "Use installed SURFboard Central reference", LoadInstalledReference);
        Action(content, "Import protocol reference (APK / XAPK / Java)", () =>
        {
            var intent = new Intent(Intent.ActionOpenDocument); intent.SetType("*/*"); intent.AddCategory(Intent.CategoryOpenable);
            StartActivityForResult(intent, ImportReference); return Task.CompletedTask;
        });
        Action(content, "Sign in and find my routers", Acquire);
        Action(content, "Use previously acquired account credential", async () =>
        {
            if (!Directory.Exists(AcquisitionStore.Folder)) throw new RouterException("No account credential is saved on this installation yet.");
            var file = Directory.EnumerateFiles(AcquisitionStore.Folder, "account-*.json").OrderDescending().FirstOrDefault()
                ?? throw new RouterException("No saved account credential was found.");
            acquired = await Task.Run(() => AcquisitionStore.Load(file), lifetime.Token); ShowDevices();
        });
        Action(content, "Enter an existing local management credential", async () =>
        {
            var form = Column(); form.SetPadding(Dp(20), Dp(4), Dp(20), Dp(4));
            var serial = Input(form, "Router serial number"); var mac = Input(form, "Router certificate MAC address");
            var user = Input(form, "Local management username / email"); var pass = Input(form, "Local management password (not Wi-Fi password)", secret: true);
            var done = new TaskCompletionSource<bool>();
            var dialog = new AlertDialog.Builder(this)!.SetTitle("Existing local credential")!.SetView(form)!
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
        content.AddView(Label("Windows credential JSON files use DPAPI and cannot be decrypted on Android. Acquire the local login again on this phone. Factory onboarding and account recovery use the vendor app.", 12));
    }
    Task LoadInstalledReference()
    {
        var app = PackageManager!.GetApplicationInfo("com.arris.sbc", PackageInfoFlags.MetaData)!;
        configuration = CloudProtocol.ReadReference(app.SourceDir!); CloudProtocol.Save(configuration);
        status.Text = "Protocol reference imported and encrypted on this phone."; return Task.CompletedTask;
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
        var record = await CloudAcquisition.VerifyAndSaveAsync(acquired!, device, address.Text!.Trim(), lifetime.Token);
        acquired = null; await Connect(record);
    }
    async Task Connect(CredentialRecord record)
    {
        status.Text = "Connecting to " + record.Address + "…"; client?.Dispose(); client = new(record);
        client.Progress = message => RunOnUiThread(() => { if (!IsDestroyed) status.Text = message; });
        await Task.Run(() => client.ConnectAsync(lifetime.Token), lifetime.Token); dirty = false;
        currentPage = Catalog.Pages[0]; ShowRouter(); await Read();
    }
    void ShowRouter()
    {
        Shell("Router settings");
        content.AddView(Label(client!.Record.ToString(), 12));
        var picker = new Spinner(this);
        picker.Adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerDropDownItem, Catalog.Pages.Select(p => p.Name).ToArray());
        picker.SetSelection(Array.IndexOf(Catalog.Pages, currentPage));
        picker.ItemSelected += async (_, args) =>
        {
            if (busy || Catalog.Pages[args.Position] == currentPage) return;
            if (!await Discard()) { picker.SetSelection(Array.IndexOf(Catalog.Pages, currentPage)); return; }
            currentPage = Catalog.Pages[args.Position]; dirty = false; await Run(async () => { ShowRouter(); await Read(); });
        };
        content.AddView(picker);
        Action(content, "Refresh this page", async () => { if (await Discard()) { dirty = false; ShowRouter(); await Read(); } });
        Action(content, "Switch router / credentials", async () => { if (await Discard()) { client?.Dispose(); client = null; AndroidNetwork.UseInternet(); ShowConnect(); } });
    }
    async Task Read()
    {
        status.Text = "Reading " + currentPage.Name + "…";
        var snapshot = await Task.Run(() => client!.ReadPageAsync(currentPage, lifetime.Token), lifetime.Token);
        dirty = false; content.AddView(Label(currentPage.Description.Replace("this PC", "this phone")));
        if ((currentPage.Id is "overview" or "internet") && snapshot.Data.TryGetValue("DEVICEOPERMODE_ALL", out var modes) && modes["DEVICEOPERMODE"] is JsonObject mode)
        {
            var value = Catalog.Text(mode["MODE"]); content.AddView(Label("Bypass / bridge mode: " + value, 20, true));
            var definition = Settings.Definitions.Single(d => d.Container == "DEVICEOPERMODE");
            var section = new SettingSection(definition, "DEVICEOPERMODE", 0, "", Settings.Filter(definition, mode), "Bypass mode");
            if (value is "DUAL" or "IPV4ONLY") Action(content, "Turn bypass ON", () => Save(section, new() { ["MODE"] = () => "BRIDGE" }));
            if (value == "BRIDGE") Action(content, "Turn bypass OFF · restore router mode", () => Save(section, new() { ["MODE"] = () => "DUAL" }));
        }
        foreach (var section in Settings.Sections(snapshot)) Editor(section);
        foreach (var definition in Settings.Definitions.Where(d => d.Collection && snapshot.Data.ContainsKey(d.Key)))
            Action(content, "Add " + definition.Title.ToLowerInvariant(), () =>
            {
                Editor(new(definition, definition.Container, -1, "", new JsonObject(), "New " + definition.Title), true);
                return Task.CompletedTask;
            });
        content.AddView(Label("Returned fields", 20, true));
        var fields = Label(string.Join("\n\n", snapshot.Rows.Select(row => row.Field + "\n" + row.Value)), 13);
        fields.SetTextIsSelectable(true); content.AddView(fields);
        var failed = snapshot.Reads.Where(r => !r.Readable).Select(r => r.Key + ": " + r.Status).ToArray();
        if (failed.Length > 0) content.AddView(Label("Unavailable reads\n" + string.Join("\n", failed), 12));
        status.Text = $"{snapshot.Reads.Count(r => r.Readable)}/{snapshot.Reads.Count} queries read · {snapshot.Time:HH:mm:ss}.";
    }
    void Editor(SettingSection section, bool adding = false)
    {
        content.AddView(Label(section.Title, 20, true));
        var getters = new Dictionary<string, Func<string>>();
        foreach (var field in section.Definition.Fields)
        {
            if (!adding && !section.Baseline.ContainsKey(field.Key) && field.Kind != "password") continue;
            var old = Catalog.Text(section.Baseline[field.Key]);
            if (field.Kind == "toggle")
            {
                var toggle = new Switch(this) { Text = field.Label, Checked = old == "1", TextSize = 15 };
                toggle.SetPadding(0, Dp(10), 0, Dp(10)); toggle.CheckedChange += (_, _) => dirty = true;
                content.AddView(toggle); getters[field.Key] = () => toggle.Checked ? "1" : "0";
            }
            else if (field.Kind == "choice")
            {
                content.AddView(Label(field.Label, 13)); var picker = new Spinner(this);
                var choices = field.Choices!;
                if (!choices.Any(c => c.Value == old)) choices = [new Choice(old, "Current: " + (old.Length == 0 ? "choose a value" : old)), .. choices];
                picker.Adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerDropDownItem, choices.Select(c => c.Label).ToArray());
                picker.SetSelection(Array.FindIndex(choices, c => c.Value == old));
                var initial = true; picker.ItemSelected += (_, _) => { if (initial) initial = false; else dirty = true; };
                content.AddView(picker); getters[field.Key] = () => choices[picker.SelectedItemPosition].Value;
            }
            else
            {
                var input = Input(content, field.Label, field.Kind == "password" ? "" : old, field.Kind == "password");
                input.TextChanged += (_, _) => dirty = true; getters[field.Key] = () => input.Text ?? "";
                if (field.Hint.Length > 0) content.AddView(Label(field.Hint, 12));
            }
        }
        Action(content, adding ? "Review new rule" : "Review changes", () => Save(section, getters, adding ? "ADD" : "MODIFY"));
        if (section.Definition.Collection && !adding) Action(content, "Remove rule", () => Save(section, [], "DELETE"));
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
