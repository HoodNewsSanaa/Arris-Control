using Android.Widget;

namespace ArrisControl;

public sealed partial class MainActivity
{
    WobaInspection? lastWobaStatus;

    void StatusRow(LinearLayout card, string title, RouterStatusFact fact)
    {
        var color = fact.Level == RouterStatusLevel.Attention ? Palette.Warning :
            fact.Level == RouterStatusLevel.Unknown ? Palette.Muted : Palette.Text;
        card.AddView(Label(title, 12, color: Palette.Muted));
        card.AddView(Label(fact.Value, 20, true, color: color));
        card.AddView(Label(fact.Detail, 12, color: Palette.Muted));
    }

    void UpdateRouterSessionStatus()
    {
        if (routerSessionStatus is null || client is null) return;
        routerSessionStatus.Text = (client.IsConnected ? "Local admin session active" : "Connection lost · tap Refresh to reconnect") +
            "\n" + RouterStatus.Routing(client.RoutingMode, "").Value + " · Onboarding: " + RouterStatus.Onboarding(client.OnboardingStatus, "").Value;
    }

    void ShowNormalRouterStatus(Snapshot snapshot)
    {
        var current = client!;
        var card = Card("Router status", "Checked " + snapshot.Time.ToString("HH:mm:ss") + ". Local access, routing mode and setup state are separate.");
        StatusRow(card, "App → router", new(current.IsConnected ? "Connected locally" : "Connection lost",
            current.Record.Address, current.IsConnected ? RouterStatusLevel.Normal : RouterStatusLevel.Attention));
        StatusRow(card, "Routing mode", RouterStatus.Routing(current.RoutingMode, "Normal local API"));
        var loginSource = current.LoginObservedAt is { } checkedAt ? "Normal login · " + checkedAt.ToString("HH:mm:ss") : "Normal login · not reported";
        StatusRow(card, "Onboarding", RouterStatus.Onboarding(current.OnboardingStatus, loginSource));
        var prior = lastWobaStatus;
        var pin = current.SessionPin;
        if (prior is not null && prior.Device.Serial == current.Record.Serial && prior.Device.Mac == current.Record.Mac && prior.Pin == pin)
        {
            var source = "WOBA · last observed " + prior.Time.ToLocalTime().ToString("HH:mm:ss") + " (not refreshed by this page)";
            StatusRow(card, "Mesh role", RouterStatus.Role(prior.Role, source));
            StatusRow(card, "Router WAN link", RouterStatus.Wan(Catalog.Text(prior.Data.GetValueOrDefault("OB_WANSTATUS_ALL")?["OB_WANSTATUS"]?["WAN_STATUS"]), source));
        }
        else
        {
            StatusRow(card, "Mesh role", RouterStatus.Role("", "Check Bluetooth or WOBA inspection"));
            StatusRow(card, "Router WAN link", RouterStatus.Wan("", "Not established by this normal session; check Bluetooth or WOBA"));
        }
        var state = snapshot.Data.GetValueOrDefault("DEVICESTATUS_ALL")?["DEVICESTATUS"];
        var stateRead = snapshot.Reads.FirstOrDefault(r => r.Key == "DEVICESTATUS_ALL");
        StatusRow(card, "Startup / firmware activity", RouterStatus.FirmwareActivity(Catalog.Text(state?["BOOTING"]),
            Catalog.Text(state?["SOFTWAREUPDATEACTIVE"]), stateRead?.Readable == true ? "Normal API · " + snapshot.Time.ToString("HH:mm:ss") : "Status read: " + (stateRead?.Status ?? "not run")));
        StatusRow(card, "Model / installed firmware", new(
            (current.Model.Length > 0 ? current.Model : "Model unknown") + " · " + (current.FirmwareVersion.Length > 0 ? current.FirmwareVersion : "Firmware unknown"),
            "Authenticated device identity"));
        Action(card, "Recheck connection and onboarding", async () =>
        {
            await current.ReconnectAsync(lifetime.Token);
            ShowRouter(); await Read();
        });
        Action(card, "Inspect setup / finish onboarding", () =>
        {
            var target = current.Record.Address;
            if (CertificatePin.IsValid(pin)) scannedPins[target] = pin!;
            ShowWobaStart(target); return Task.CompletedTask;
        });
        Action(card, "Inspect status over Bluetooth", BleScanAndPick);
    }

    void ShowWobaStatus(WobaInspection inspection)
    {
        lastWobaStatus = inspection;
        var source = "WOBA · " + inspection.Time.ToLocalTime().ToString("HH:mm:ss");
        var card = Card("Router status", "Onboarding inspection is separate from a normal authenticated admin session.");
        StatusRow(card, "Mesh role", RouterStatus.Role(inspection.Role, source));
        StatusRow(card, "Onboarding", RouterStatus.Onboarding(inspection.OnboardingStatus, source));
        StatusRow(card, "Router WAN link", RouterStatus.Wan(Catalog.Text(inspection.Data.GetValueOrDefault("OB_WANSTATUS_ALL")?["OB_WANSTATUS"]?["WAN_STATUS"]), source));
        var update = inspection.Data.GetValueOrDefault("OB_SWUPDATESTATUS_ALL")?["OB_SWUPDATESTATUS"];
        var upgrade = Catalog.Text(update?["UPGRADE_STATUS"]); var progress = Catalog.Text(update?["PROGRESS_PERCENTAGE"]);
        StatusRow(card, "Firmware update report", new(upgrade.Length > 0 ? upgrade : "Unknown",
            source + (progress.Length > 0 ? " · progress " + progress + "%" : " · progress not reported"), RouterStatusLevel.Unknown));
        StatusRow(card, "Routing / bridge mode", new("Not read through WOBA", "Use normal local administration to check router versus bridge mode.", RouterStatusLevel.Unknown));
    }

    void ShowBluetoothStatus(string mode, string setup, string wan, string upgrade, string download, IReadOnlyDictionary<string, string> errors)
    {
        var source = "Bluetooth · " + DateTime.Now.ToString("HH:mm:ss");
        string Source(string uuid) => errors.TryGetValue(uuid, out var error) ? source + " · " + error : source;
        var card = Card("Router status", "Read from this Bluetooth unit. Status colours alone do not verify its configuration.");
        StatusRow(card, "Mesh role / device mode", RouterStatus.Role(mode, Source(MaxBle.DeviceMode)));
        StatusRow(card, "Onboarding state", RouterStatus.BluetoothSetup(setup, Source(MaxBle.ObaState)));
        StatusRow(card, "Router WAN link", RouterStatus.Wan(wan, Source(MaxBle.WanStatus)));
        StatusRow(card, "Firmware update report", new(upgrade.Length > 0 ? upgrade : "Unknown",
            Source(MaxBle.UpgradeStatus) + (download.Length > 0 ? " · progress " + download : ""), RouterStatusLevel.Unknown));
        StatusRow(card, "Routing / bridge mode", new("Not read over Bluetooth", "MAINAP / EXTENDER / OB describe the mesh role, not bridge mode.", RouterStatusLevel.Unknown));
        StatusRow(card, "Onboarding completion", new("Not verified by this inspection", "Check the local login's OBASTATUS flag or the verified completion flow.", RouterStatusLevel.Unknown));
    }

    void ShowWirelessControls(Snapshot snapshot)
    {
        content.AddView(Label("Wi-Fi roaming and channels", 20, true));
        var sections = Settings.Sections(snapshot);
        foreach (var container in new[] { "WIFI_FH_BANDSTEERING", "WIFI_FH_DFS" })
        {
            var definition = Settings.Definitions.Single(d => d.Container == container);
            var section = sections.FirstOrDefault(s => s.Container == container);
            var value = section is null ? "" : Catalog.Text(section.Baseline["ENABLE"]);
            if (section is not null && value is ("0" or "1")) Editor(section);
            else
            {
                var read = snapshot.Reads.FirstOrDefault(r => r.Key == definition.Key);
                var detail = read is null ? "This query has not run. Refresh after restoring the local connection." :
                    !read.Readable ? "Router read result: " + read.Status : "The router did not return a recognized on/off value for this feature.";
                var unavailable = Card(definition.Title, "Status unavailable", chipText: "Not available", chipColor: Palette.Muted);
                unavailable.AddView(Label(detail, 14));
                unavailable.AddView(Label(definition.Impact, 12, color: Palette.Muted));
            }
        }
    }
}
