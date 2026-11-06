Android 0.4.8 adds satellite pages and an experimental satellite DFS control.

On **Mesh and devices**, select a satellite and choose **Open selected satellite**. Its page is read through your main router and shows the satellite's model and firmware, its light ring and status light (editable), and **Restart satellite**. Network-wide settings such as DNS, DHCP, Wi-Fi names and bypass stay on the main router and aren't shown. Restart is a normal restart only; factory and Wi-Fi resets are refused.

Turning DFS off on the main router doesn't change satellites, in this app or the official one: the main router's firmware never passes that setting on. The satellite page's **DFS channels · experimental** card can read the satellite's 5 GHz radio and turn DFS on or off by talking directly to the satellite's internal mesh agent. This has not been tested on real hardware, and the satellite may refuse it. The app checks the satellite's serial twice, sends one request, reads it back and never retries.

Every satellite change re-checks the main router's and the satellite's serials, previews the change, sends once and reads it back. Details: docs/satellites.md.

Offline tests cover the forwarding allowlists, restart-only reset, identity checks, verified saves, the mesh-agent DFS request format, one-shot writes and read-back. The release workflow compiles and launches the Android app; satellite features still need checking on a real mesh.
