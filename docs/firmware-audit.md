# Firmware vs app audit (W30 AXR.0814.240522.10)

Static comparison of the W30 local API (`/usr/lca/MobAppOperation.php`, 93 read
cases and 71 write cases) and the firmware's advertised feature list
(`/usr/lca/capabilities.json`) with this app. No firmware files are included here.

## Fixed in Android 0.4.8

- **Time zone editor never worked.** `TIME_ALL` returns `TIME{LOCALTIMEZONE,
  DSTENABLE, AUTOENABLE}` with a UTC offset such as `-05:00`. The app read and
  wrote a `TIMEZONE` container (no such case) and validated names like
  `America/New_York`. It now uses `TIME` with the firmware's ten zones
  (`Common.php getTimeZone`). `AUTO_TIME_ZONE_SUPPORT` is false, so the
  automatic toggle is no longer shown; its value is passed back unchanged.
- **Light ring brightness 0 could never verify.** `normalizeToLedRingQuery`
  drops `BRIGHTNESS "0"`. The range is now 1–100, with a pointer to the status
  light for turning it off.
- **Two dead reads per page load.** `IPV6SUBNET_ALL` (LAN page) and
  `WANSTATICIPV6_ALL` (Internet page) hit the GET handler's
  "Missing functionality" default and always return `InvalidOption`. Removed
  from those pages; still probed by Diagnostics.
- **DNS editor on Android** now has primary/secondary fields per IP version
  (and for static WAN DNS), hidden in automatic mode. The wire format is
  unchanged (`"primary,secondary"`). Windows already had two fields.
- **Wi-Fi channel width and transmit power** are now editable on the main
  2.4/5 GHz networks, using `ConverterJson` values (`20 MHz` … `20/40/80/160 MHz`;
  `High`/`Medium`/`Low`/`Very Low` = 100/75/50/25%). They're sent only when
  changed, so a name or password save can't alter them.

## Shown by the app but reported unsupported

- **Device priority** — `FEATURECAPABILITIES.DEVICE_PRIORITY = false`. The
  official app uses per-device QoS (`DEVICEQoS`) instead.

## Supported by the firmware, not in the app

From `capabilities.json` (true) plus a matching `MobAppOperation.php` case:

| Feature | Firmware calls | Notes |
|---|---|---|
| LAN subnet change | `IPV4SUBNET` set | `IPv4_SUBNET_CHANGE`, `IPv6_SUBNET_CHANGE` |
| Parental profiles, schedules, keyword and URL filters | `PC_PROFILE`, `PARENT_GROUP_DEVICES`, `PARENT_BLOCK_DEVICES`, `PARENT_KEYWORD_FILTER`, `PARENT_URL_FILTER` | App has only on/off and blocked websites |
| Per-device QoS | `DEVICEQoS_ALL` / `DEVICEQoS` | `DEVICE_QoS_ENABLED` |
| Backhaul DFS and channel | `WIFI_BH` (`WLACSDFS`, `CHANNEL`, `BANDWIDTH`), `WIFI_TRIBAND` | `BH_DFS` |
| Speed tests | `OOKLA_SPEED_TEST`, `IPERF_SPEED_TEST`, `SPEEDTESTBH` | Ookla WAN, Wi-Fi iperf, backhaul |
| WPS | `WPS` get/set | Push-button pairing |
| Admin login change | `ADMIN_LOGIN_ALL` / `ADMIN_LOGIN` | Sensitive; needs care with saved credentials |
| Remove offline device | `REMOVEDEVICE` | Client list clean-up |
| Satellite placement quality | `EXTENDER_PLACEMENT_ALL` | Radio 2 placement readings |
| Link aggregation | `WANLAG` set | `LAC`; app only reads it |
| Amazon Frustration-Free Setup | `WIFI_AMAZON_FFS_ALL` / `WIFI_AMAZON_FFS` | `AMAZON_FFS` |
| Static WAN IPv6 / dynamic IPv6 | `WANDYNAMICIPV6` set | App only reads it |

Each of these still needs a firmware read of its exact fields before an editor
is added, following the app's existing allowlist/preview/read-back pattern.

## Code review notes

The codebase is consistently defensive: fixed request allowlists, certificate
pinning before any login, serial checks before writes, single non-retried
writes with read-back, Keystore AES-GCM credentials, verified updates. No
`async void`, blocking waits on the UI thread, or swallowed exceptions were
found. The bugs found were all firmware-contract mismatches, listed above.
