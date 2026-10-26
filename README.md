# Arris Control

A native Windows application for local management of ARRIS SURFboard mAX
routers. Uses regular setting forms, switches, dropdowns, and Save buttons,
with a separate **Full settings** browser for all returned fields.

Download **Arris-Control-win-x64.exe** from
[Releases](https://github.com/HoodNewsSanaa/Arris-Control/releases).
The release is a self-contained Windows x64 executable: no PowerShell,
Android device, or separate .NET installation is needed to run it.

## Connect

Use the same Windows user and PC that created your existing DPAPI-protected
local router credential. The app finds `.local-credentials` alongside it or
in an ancestor directory, and also checks
`%LOCALAPPDATA%\ArrisControl\Credentials`. Otherwise select **Import
credential** and choose an existing encrypted `ARRIS-LocalCredential` v1
JSON file. Import verifies decryption before copying the encrypted record
into the per-user folder. Credentials are not included in this repository
or any release, and DPAPI records cannot simply be shared with another PC.

This first native release consumes the local credential established by the
Windows protocol investigation. It does not implement first-time router
onboarding, a cloud login/recovery workflow, or automatic credential recovery.

The app authenticates directly to the private router HTTPS API on port 50011,
explicitly preserves its management session cookie, and checks the returned
router serial. Normal router management sends no cloud requests. The Updates
button optionally contacts GitHub; router access does not depend on that.

## Settings and information

- Overview: routing mode, firmware version, WAN state, and DNS.
- Mesh and devices: expandable topology, radios, clients, and backhaul data.
- Wi-Fi: main/guest names, optional password changes, guest enable state,
  channel selection, and name broadcast where returned by the firmware.
- Internet: IPv4/IPv6 DNS and routing/IPv6 mode.
- LAN: IPv4/IPv6 DHCP ranges and lease times, subnet information, and UPnP.
- Reservations/ports: add, edit, and remove address reservations and port rules.
- Priority/QoS, parental-control enable state, device details, and status light.
- Capabilities and the complete returned-field browser.

Controls appear only for returned setting containers/fields. A firmware
rejection remains visible; the app does not assume every endpoint in the
multi-product API is implemented on every mAX model.

**Save changes** re-reads the setting and device identity, checks for stale
values, displays a change preview, and sends one update only after the user
confirms. It then reads back the changed fields. An interrupted save is
reported as unknown and is never automatically retried. Secret values are
hidden in previews and the field browser; new Wi-Fi passwords use masked inputs.

Enabling bridge mode is blocked in both the UI and request validator. Selecting
router mode can request bridge-off only if the local API is reachable. This is
not a verified recovery method: local reachability/login while bridged and
WAN-disconnected operation have not been established.

The earlier Windows client verified local login and protected configuration
reads on a W30-family mAX. Native protocol, payload, stale-update, privacy,
UI, and update-replacement behavior have offline tests. Native live operation
and setting writes still need user validation; no live changes were made to
test this release. Complete app parity, parental profile/schedule editors,
subnet migration, all radio/maintenance functions, and historical statistics
remain ongoing work. This is an initial release, not a claim of full parity.
Firmware downloading/extraction work is paused.

## App updates

Select **Updates** to check this repository's stable GitHub releases. The app
accepts only a newer three-part release version and the fixed Windows asset
name, verifies its advertised length, SHA-256 checksum, GitHub asset digest
when present, PE header, and executable version. It limits redirects to
GitHub's download hosts. No token or router credential is sent to GitHub.

Installation requires a separate confirmation. A copy of the current app
waits for the original process to close, rechecks hashes, replaces only the
executable atomically, retains a `.previous` backup, and restarts the app.
Router credentials and reports are not part of replacement. The executable
directory must be writable by the current Windows user; no elevation is used.
If a check/download fails, the existing application remains usable.

The executable is not Authenticode-signed. Checksums/digests verify download
integrity through the configured GitHub release, not an independent publisher
signing identity. The full live update cycle between two published versions
still needs validation; atomic replacement and policy checks run on fixtures.

## Build

Requires Windows and the .NET 10 SDK. No third-party NuGet packages are used.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

`build.ps1` publishes a self-contained single-file executable, runs offline
checks against that packaged executable, and writes the Windows asset plus
`SHA256SUMS.txt` into `dist`. `-Offline` uses existing local runtime packs.
No build/test accesses a router or decrypts a real credential.

GitHub Actions builds/checks pushes to `main` and creates a release for the
project's version if that tag is not already published. Increase the project
version for subsequent releases. Workflow actions are pinned to commit hashes.

This is an independent project, not an ARRIS/SURFboard product. Protocol
mapping was derived from behavior and static API models; APKs, vendor firmware,
user identifiers, credentials, and diagnostic responses are not redistributed.
