using System.Text.Json.Nodes;

namespace ArrisControl;

internal sealed record SatelliteJoinResult(bool Joined, string State, string Detail);
internal static class SatelliteJoin
{
    static string Value(JsonObject? node, params string[] names)
    {
        foreach (var name in names) if (node?[name] is JsonValue value) return Catalog.Text(value);
        return "";
    }
    public static SatelliteJoinResult Verify(Snapshot snapshot, CredentialRecord controller, CloudDevice target, DateTimeOffset selectedAt)
    {
        BleSetupProtocol.ValidateIdentity(target);
        if (target.Serial == controller.Serial || target.Mac == controller.Mac)
            return new(false, "Select a satellite", "The selected Bluetooth unit is the connected controller.");
        if (snapshot.Time < selectedAt) return new(false, "Waiting for a fresh read", "This mesh snapshot predates satellite selection.");
        foreach (var key in new[] { "NETWORKMAP_ALL", "LINKSPEEDBH_ALL" })
            if (!snapshot.Reads.Any(r => r.Key == key && r.Readable))
                return new(false, "Join unverified", key + ": " + (snapshot.Reads.FirstOrDefault(r => r.Key == key)?.Status ?? "not read"));
        var list = snapshot.Data.GetValueOrDefault("AUTO_WOBA_SAT_STATUS_ALL")?["AUTO_WOBA_SAT_STATUS"]?["SAT_LIST"] as JsonArray;
        var candidates = list?.OfType<JsonObject>().Where(row => Value(row, "MAX_SERIAL_NO") == target.Serial || MeshTopology.Mac(Value(row, "MAX_WAN_MAC")) == target.Mac).ToArray() ?? [];
        if (candidates.Length > 1 || candidates.Any(row => Value(row, "MAX_SERIAL_NO") != target.Serial || MeshTopology.Mac(Value(row, "MAX_WAN_MAC")) != target.Mac))
            return new(false, "Identity conflict", "The controller's satellite identities are ambiguous or disagree with the selected Bluetooth unit.");
        var backhaulMac = candidates.Length == 1 ? MeshTopology.Mac(Value(candidates[0], "MAX_WiFi_MAC_BH")) : "";
        var tree = snapshot.Data.GetValueOrDefault("NETWORKMAP_ALL")?["HNC"] as JsonObject;
        var root = tree?["data"] as JsonObject ?? tree;
        var rootMatches = Value(root, "serialNum", "serialNumber") == controller.Serial ||
            MeshTopology.Mac(Value(root, "macAddress", "wanMac", "mac")) == controller.Mac;
        var visited = 0;
        bool InTopology(JsonObject node, int depth, bool isRoot = false)
        {
            if (depth > 16 || ++visited > 512) return false;
            var data = node["data"] as JsonObject ?? node;
            var kind = Value(data, "deviceType").ToLowerInvariant();
            var serial = Value(data, "serialNum", "serialNumber");
            var mac = MeshTopology.Mac(Value(data, "macAddress", "wanMac", "mac"));
            if (!isRoot && kind is ("router" or "extender" or "satellite" or "hne" or "hnc") &&
                (serial == target.Serial && (mac.Length == 0 || mac == target.Mac || mac == backhaulMac) ||
                 serial.Length == 0 && candidates.Length == 1 && mac.Length > 0 && (mac == target.Mac || mac == backhaulMac))) return true;
            return node["children"] is JsonArray children && children.OfType<JsonObject>().Take(256).Any(child => InTopology(child, depth + 1));
        }
        var joinedTopology = rootMatches && tree is not null && InTopology(tree, 0, true);
        var mesh = MeshTopology.Parse(snapshot, controller);
        var matches = mesh.Routers.Where(n => n.Id == target.Serial && n.Mac == target.Mac).ToArray();
        var active = matches.Length == 1 && mesh.Links.Any(l => l.Child == matches[0].Id && l.Source.StartsWith("LINKSPEEDBH_ALL", StringComparison.Ordinal));
        if (joinedTopology && active) return new(true, "Joined · active backhaul verified", "The selected satellite appears behind this controller in the network map and has a matching active backhaul association.");
        if (joinedTopology) return new(false, "In topology · backhaul unverified", "The selected satellite is in the network map, but no matching active wireless backhaul was reported. A wired link needs separate status evidence.");
        if (active) return new(false, "Backhaul seen · topology unverified", "An active association was reported, but the selected satellite's joined topology was not confirmed.");
        return new(false, "Waiting for the selected satellite", "A discovery-table entry or role assignment alone does not prove the satellite joined.");
    }
}
