using System.Text;

namespace ArrisControl;

/// <summary>
/// A small in-memory activity log for the Bluetooth setup experiment, so a failed attempt can be
/// exported and diagnosed. Records timestamps, operations, byte lengths and status codes only.
/// It never records credential, key or passphrase values; those are noted by length alone.
/// Cleared when the app process ends; never written to disk by itself.
/// </summary>
internal static class BleLog
{
    const int Max = 400;
    static readonly object Gate = new();
    static readonly List<string> Lines = new();

    public static void Add(string line)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
        lock (Gate)
        {
            Lines.Add($"{stamp}  {line}");
            if (Lines.Count > Max) Lines.RemoveRange(0, Lines.Count - Max);
        }
    }

    // Describes a written/read value without revealing a secret one.
    public static string Describe(string uuid, byte[] value)
    {
        if (MaxBle.NeverRead.Contains(uuid) || BleSetupProtocol.WriteUuids.Contains(uuid)) return $"{value.Length} bytes (secret, not shown)";
        var text = Encoding.UTF8.GetString(value);
        var printable = text.Length > 0 && text.All(c => !char.IsControl(c));
        return printable && text.Length <= 24 ? $"{value.Length} bytes \"{text}\"" : $"{value.Length} bytes";
    }

    public static void Clear() { lock (Gate) Lines.Clear(); }

    public static string Export()
    {
        lock (Gate) return Lines.Count == 0 ? "(no Bluetooth activity recorded yet)" : string.Join("\n", Lines);
    }
}
