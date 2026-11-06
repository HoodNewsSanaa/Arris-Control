using Android.Views;
using Android.Widget;

namespace ArrisControl;
public sealed partial class MainActivity
{
    Task BackToMesh()
    {
        currentPage = Catalog.Pages.First(p => p.Id == "mesh"); ShowRouter(); return Read();
    }

    /// <summary>Simplified satellite page. Everything goes through the main router's forwarding,
    /// except the experimental DFS card, which talks to the satellite's mesh agent.</summary>
    async Task ShowSatellite(SatelliteTarget target)
    {
        Shell("Satellite · " + target.Name);
        backAction = async () => { if (!await Discard()) return; dirty = false; await BackToMesh(); };
        var head = Card("Satellite · " + target.Name, target.Address + " · " + target.Serial);
        head.AddView(Label("Reached through your main router. A satellite only controls the options on this page. DNS, DHCP, Wi-Fi names, bypass and other network-wide settings are set on the main router.", 13));
        Action(head, "Refresh this satellite", async () => { if (await Discard()) { dirty = false; await ShowSatellite(target); } }, Tone.Primary);
        Action(head, "Back to mesh", async () => { if (await Discard()) { dirty = false; await BackToMesh(); } });

        status.Text = "Reading " + target.Name + " through the main router…";
        var snapshot = await Task.Run(() => client!.ReadSatellitePageAsync(target, lifetime.Token), lifetime.Token);
        UpdateRouterSessionStatus();

        var info = Card("Device and firmware");
        var device = snapshot.Data.GetValueOrDefault("DEVICE_INFO_ALL")?["DEVICE_INFO"];
        StatusRow(info, "Model", new(Or(Catalog.Text(device?["MODEL_NAME"])), "Reported by the satellite", RouterStatusLevel.Unknown));
        StatusRow(info, "Firmware", new(Or(Catalog.Text(device?["SOFTWARE_VERSION"])), "Reported by the satellite", RouterStatusLevel.Unknown));
        foreach (var read in snapshot.Reads.Where(r => !r.Readable))
            info.AddView(Label(read.Key + ": " + read.Status, 13, color: Palette.Muted));

        foreach (var section in Settings.Sections(snapshot).Where(s => SatelliteForwarding.WriteContainers.Contains(s.Container)))
            Editor(section, save: getters => SaveSatellite(target, section, getters));

        ShowSatelliteDfs(target);

        var restart = Card("Restart satellite", "Restarts only this satellite. Devices near it lose Wi-Fi for a few minutes. Settings are kept; this is not a reset.");
        Action(restart, "Restart " + target.Name, async () =>
        {
            if (!await Confirm("Restart satellite", $"Restart {target.Name} ({target.Serial}) at {target.Address}?\n\nA normal restart, not a factory or Wi-Fi reset. The satellite's serial is checked first and the request is sent once.", "Restart"))
            { status.Text = "Cancelled. Nothing was sent."; return; }
            status.Text = "Checking the satellite's identity…";
            await Task.Run(() => client!.RebootSatelliteAsync(target, lifetime.Token), lifetime.Token);
            status.Text = "Restart requested. The satellite rejoins the mesh in a few minutes; refresh Mesh and devices then.";
            await Message("Restart requested", status.Text);
        }, Tone.Danger);

        var details = Card("Details", "Read-only fields the satellite returned through the main router.");
        var fields = Label(string.Join("\n\n", snapshot.Rows.Select(row => row.Field + "\n" + row.Value)), 13);
        fields.SetTextIsSelectable(true); fields.Visibility = ViewStates.Gone;
        Action(details, "Show returned fields", () => { fields.Visibility = fields.Visibility == ViewStates.Gone ? ViewStates.Visible : ViewStates.Gone; return Task.CompletedTask; });
        details.AddView(fields);
        status.Text = snapshot.Reads.All(r => r.Readable) ? target.Name + " read through the main router." : target.Name + " read; some items are unavailable on this firmware.";
    }

    static string Or(string value) => value.Length > 0 ? value : "Not reported";

    async Task SaveSatellite(SatelliteTarget target, SettingSection section, Dictionary<string, Func<string>> getters)
    {
        status.Text = "Checking the satellite's identity and current settings…";
        var edits = getters.ToDictionary(pair => pair.Key, pair => pair.Value());
        var plan = await Task.Run(() => client!.PrepareSatelliteAsync(target, section, edits, lifetime.Token), lifetime.Token);
        if (!await Confirm("Review satellite change", plan.Preview + "\n\nApply once to " + target.Serial + " through the main router?"))
        { status.Text = "Cancelled. No update was sent."; return; }
        status.Text = "Sending the reviewed change once…";
        var result = await Task.Run(() => client!.ApplySatelliteAsync(target, plan, lifetime.Token), lifetime.Token);
        dirty = false; status.Text = result; await Message("Save result", result);
        await ShowSatellite(target);
    }

    void ShowSatelliteDfs(SatelliteTarget target)
    {
        var card = Card("DFS channels · experimental", "Not available through the official API",
            chipText: "Experimental", chipColor: Palette.Muted);
        card.AddView(Label("Your main router doesn't pass its DFS setting on to satellites, and the official app can't change it here either. This talks directly to the satellite's mesh agent, the channel the main router uses internally. It hasn't been tested on real hardware, and the satellite may refuse requests that don't come from the main router.", 13));
        var state = Label("Satellite radio not read yet.", 14);
        card.AddView(state);
        Action(card, "Read satellite 5 GHz radio", async () =>
        {
            status.Text = "Reading the satellite's radio through its mesh agent…";
            var radio = await Task.Run(() => client!.ReadSatelliteRadioAsync(target, null, lifetime.Token), lifetime.Token);
            state.Text = radio.Summary; status.Text = "Satellite radio read.";
        });
        Action(card, "Turn DFS OFF on this satellite", () => SetSatelliteDfs(target, false, state), Tone.Primary);
        Action(card, "Turn DFS ON on this satellite", () => SetSatelliteDfs(target, true, state));
    }

    async Task SetSatelliteDfs(SatelliteTarget target, bool enable, TextView state)
    {
        var word = enable ? "ON" : "OFF";
        if (!await Confirm("Satellite DFS · experimental",
            $"Turn DFS {word} on {target.Name} ({target.Serial}) at {target.Address}?\n\nThis uses the satellite's internal mesh agent, not an official app path. The app checks the satellite's serial through the main router and again over the mesh agent, sends one request, then reads it back. It is never retried.\n\nWi-Fi near this satellite may drop briefly while it picks a new channel.", "Send once"))
        { status.Text = "Cancelled. Nothing was sent."; return; }
        status.Text = "Checking the satellite's identity…";
        var result = await Task.Run(() => client!.SetSatelliteDfsAsync(target, enable, null, lifetime.Token), lifetime.Token);
        state.Text = result; status.Text = result;
        await Message("Satellite DFS result", result);
    }
}
