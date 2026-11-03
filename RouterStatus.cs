namespace ArrisControl;

internal enum RouterStatusLevel { Normal, Attention, Unknown }
internal sealed record RouterStatusFact(string Value, string Detail, RouterStatusLevel Level = RouterStatusLevel.Normal);

// Keep mesh role, routing mode, onboarding completion and WAN state separate.
// Missing values must never become a healthy or completed state.
internal static class RouterStatus
{
    public static RouterStatusFact BluetoothService(string raw, string source) => raw switch
    {
        "1" => new("Running", source + " · onboarding process present; phone Bluetooth visibility is separate"),
        "0" => new("Stopped", source + " · onboarding process absent; normal admin access is independent", RouterStatusLevel.Attention),
        _ => Unknown(raw, source)
    };
    static RouterStatusFact Unknown(string raw, string source) => new("Unknown",
        source + (string.IsNullOrWhiteSpace(raw) ? " · no value returned" : " · reported: " + raw[..Math.Min(raw.Length, 120)]), RouterStatusLevel.Unknown);
    public static RouterStatusFact Role(string raw, string source) => raw.Trim().ToUpperInvariant() switch
    {
        "MAINAP" => new("Primary mesh router", source + " · MAINAP"),
        "EXTENDER" => new("Mesh satellite", source + " · EXTENDER"),
        "OB" or "ONBOARDING" => new("Onboarding / uninitialized role", source + " · " + raw, RouterStatusLevel.Attention),
        _ => Unknown(raw, source)
    };
    public static RouterStatusFact Routing(string raw, string source) => raw.Trim().ToUpperInvariant() switch
    {
        "BRIDGE" => new("Bridge / bypass", source + " · routing is handled upstream"),
        "DUAL" => new("Router · IPv4 + IPv6", source + " · DUAL"),
        "IPV4ONLY" => new("Router · IPv4", source + " · IPV4ONLY"),
        _ => Unknown(raw, source)
    };
    public static RouterStatusFact Onboarding(string raw, string source) => raw switch
    {
        "0" => new("Complete", source + " · OBASTATUS=0"),
        "1" => new("Incomplete", source + " · OBASTATUS=1", RouterStatusLevel.Attention),
        _ => Unknown(raw, source)
    };
    public static RouterStatusFact Wan(string raw, string source) => raw.Trim().ToUpperInvariant() switch
    {
        "UP" or "WANUP" => new("Up", source + " · reported WAN link; Internet access is a separate check"),
        "DOWN" or "WANDOWN" => new("Down", source + " · WAN link unavailable", RouterStatusLevel.Attention),
        _ => Unknown(raw, source)
    };
    public static RouterStatusFact BluetoothSetup(string raw, string source) => string.IsNullOrWhiteSpace(raw)
        ? Unknown(raw, source)
        : new(raw, source + " · setup state, not a verified completion flag" +
            (BleSetupProtocol.IsUpgrading(raw) ? "; UPGRADING alone does not prove a download is running" : ""), RouterStatusLevel.Unknown);
    public static RouterStatusFact FirmwareActivity(string booting, string updating, string source) => (booting, updating) switch
    {
        (_, "1") => new("Update active", source, RouterStatusLevel.Attention),
        ("1", _) => new("Starting up", source, RouterStatusLevel.Attention),
        ("0", "0") => new("Startup / update inactive", source),
        _ => Unknown("", source)
    };
}
