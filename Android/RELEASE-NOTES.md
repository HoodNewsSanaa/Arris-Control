Android 0.4.8 adds satellite pages, an experimental satellite DFS control, Wi-Fi channel width and power, and fixes found by checking the app against W30 firmware.

On **Mesh and devices**, select a satellite and choose **Open selected satellite**. Its page is read through your main router and shows the satellite's model and firmware, its light ring and status light (editable), and **Restart satellite**. Network-wide settings such as DNS, DHCP, Wi-Fi names and bypass stay on the main router and aren't shown. Restart is a normal restart only; factory and Wi-Fi resets are refused.

Turning DFS off on the main router doesn't change satellites, in this app or the official one: the main router's firmware never passes that setting on. The satellite page's **DFS channels · experimental** card can read the satellite's 5 GHz radio and turn DFS on or off by talking directly to the satellite's internal mesh agent. This has not been tested on real hardware, and the satellite may refuse it. The app checks the satellite's serial twice, sends one request, reads it back and never retries.

Also in this build:
- **Channel width** (up to 160 MHz on 5 GHz) and **transmit power** on the main Wi-Fi networks. They're only sent when you change them.
- **DNS** now has separate primary and secondary fields, hidden when DNS is automatic.
- **Time zone** works: it uses the router's own zone list. The previous editor never appeared because it read the wrong field.
- **Light ring brightness** is 1–100. The router ignores 0; use the status light to turn it off.
- The LAN and Internet pages no longer request two values this firmware doesn't support.

Every satellite change re-checks the main router's and the satellite's serials, previews the change, sends once and reads it back. Details: docs/satellites.md and docs/firmware-audit.md.

Offline tests cover the forwarding allowlists, restart-only reset, identity checks, verified saves, the mesh-agent DFS request format, DNS fields, time zone, channel width and power. The release workflow compiles and launches the Android app; satellite features still need checking on a real mesh.
