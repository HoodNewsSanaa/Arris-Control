# Satellite pages and satellite DFS

Findings from static reading of W30 firmware `AXR.0814.240522.10` (W3x production
image), plus how Android 0.4.8 uses them. No firmware files are included in this
repository.

## Why DFS on the main router doesn't reach satellites

- The local API's `WIFI_FH_DFS` setter (`MobAppOperation.php`) maps `true`/`false`
  to `2`/`0` and writes `Device.WiFi.Radio.N.X_ARRIS_COM.DFS` on the **receiving
  unit's own radios only**. `getDfsWiFiIndicies()` is not defined in this build,
  so only radio 2 (5 GHz fronthaul) is changed.
- Satellites don't run the port-50011 management API. `lighttpd.sh` starts
  `lca_lighttpd.conf` (port 50011) only in main-AP or onboarding mode. In extender
  mode, `hne_lighttpd.conf` serves a single CGI, `/hne_config.htm`, on ports
  8444 (HTTP) and 8445/8446 (HTTPS).
- The main router's mesh controller (`libhnc`) pushes channel, bandwidth and
  auto-channel settings to satellites, plus `WlAcsDfs` on radio 3 (the backhaul),
  but never the fronthaul DFS flag. Broadcom's `wbd_master`/`wbd_slave` share DFS
  channel lists for radar handling, not the on/off setting.

This matches a user test with the official app: after turning DFS off, the main
router moved to 80 MHz while the satellite stayed at 160 MHz.

## Satellite pages (official forwarding)

The main router forwards a fixed set of requests to a satellite by LAN address:

- Read: `GET /getData?key=<KEY>&ip=<satellite IPv4>`
- Write: the normal `/setData` body with `REQUEST.HNEIPADDRESS` added.
- The firmware only accepts `192.168.`, `10.` and `172.` addresses.

`MobAppOperation.php` uses `OPMODE_PGET`/`OPMODE_PSET` (forwarded through
`propagate.so` → `libarris_hnereq` → the satellite's `/hne_config.htm`) for:

| Forwarded read | Forwarded write |
|---|---|
| DEVICE_INFO_ALL, FIRMWARE_ALL, DEVICESTATUS_ALL, UPDATE_ALL | LEDRING, LED_DISPLAY |
| LEDRING_ALL, LED_DISPLAY_ALL, SPEEDTESTBH_ALL | DEVICERESET, UPDATE, SPEEDTESTBH, DEVICEPRIORITY, TURNOFFOBA |

The app admits only these (`SatelliteForwarding`):

- Reads: DEVICE_INFO_ALL, FIRMWARE_ALL, DEVICESTATUS_ALL, LEDRING_ALL, LED_DISPLAY_ALL.
- Writes: LEDRING and LED_DISPLAY (normal field allowlists), and a restart.
- Restart is fixed to `RESETMODE=SIMPLE_RESET`, `DORESET=true`. The firmware also
  knows `WIFI_RESET` and `FACTORY_RESET` (`Common.php`, `prov_factoryreset`); both
  are refused by the transport allowlist.
- Not included: firmware update (downloads are paused in this project), device
  priority (a client setting), onboarding, and the backhaul speed test. The speed
  test's `IPerf3Client.DiagnosticState` is mapped read-only in `Map.php`, so the
  start trigger isn't clear from the firmware and no write is guessed.

Before every satellite write the app re-verifies the main router's serial, then
the serial the satellite reports through forwarding (`DEVICE_INFO.SERIAL_KEY`),
against the mesh map entry. Saves re-check current values, show a preview, send
once, and read back through forwarding. A lost reply is reported as unknown and
never retried.

## Satellite DFS (experimental, mesh agent)

The satellite's `hne_cgi` handles `/hne_config.htm`:

- `POST` reads values; `PUT` sets them (the same calls `libarris_hnereq` makes).
- URL: `https://<satellite>:8445/hne_config.htm` (`libarris_hnereq` uses port 8445).
- Body: `<?xml version="1.0" encoding="US-ASCII"?><X_ARRIS_COM><tr181i2><Param>value</Param>…</tr181i2></X_ARRIS_COM>`.
  Reads send empty elements; the reply uses the same shape. No types are sent;
  the agent looks them up.
- `ssl.verifyclient` is commented out in `hne_lighttpd.conf`, so no client
  certificate is required. The server certificate is issued for the controller's
  private CA, which the phone doesn't have.
- Unknown: `hne_cgi` records the controller address (`/tmp/HNC-IP-Address`) and
  reads `REMOTE_ADDR`. It may reject requests that don't come from the main
  router. The app reports a refusal and doesn't retry or try other ports.

The app (`MeshAgentProtocol`, `MeshAgent`) admits only:

- Reads: `Device.DeviceInfo.SerialNumber`, `Device.WiFi.Radio.2.X_ARRIS_COM.DFS`,
  `.Channel`, `.OperatingChannelBandwidth`, `.AutoChannelEnable`.
- One write: `Device.WiFi.Radio.2.X_ARRIS_COM.DFS` = `0` (off) or `2` (on), the
  same values the main router's own setter uses.

Order: forwarded serial check through the main router → connect to the agent and
lock the first certificate key for that connection → read the serial over it and
require a match → skip if already in the requested state → one `PUT` → read back.
XML responses are size-limited, DTDs are refused, and unrequested parameters are
rejected.

This is not something the official app does, and it hasn't been tried on hardware.
