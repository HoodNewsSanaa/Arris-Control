# Arris Control

A native Windows application for local management of ARRIS SURFboard mAX
routers. Uses regular setting forms, switches, dropdowns, and Save buttons,
with a separate **Full settings** browser for all returned fields.

Download **Arris-Control-win-x64.exe** from
[Releases](https://github.com/HoodNewsSanaa/Arris-Control/releases).
The release is a self-contained Windows x64 executable: no PowerShell,
Android device, or separate .NET installation is needed to run it.

## Connect

Use **Credentials** to acquire an existing router credential, import a saved
JSON, choose a retained version from history, or open the credentials folder.
The app also finds `.local-credentials` alongside it or in an ancestor directory.

### Acquire through your ARRIS account

Choose **Acquire from ARRIS account** and enter your current account email and
password. The native client uses Cognito SRP, reads the existing account SSO
credential and owned-device inventory, and decrypts the existing local login
credential in memory. It does not reset passwords, register devices, or create
a new password on the router. Select the exact device and its private LAN
address; the client makes one local login, verifies the returned serial and
protected mode, then saves a verified Windows credential JSON.

The protocol configuration is read statically from a local SURFboard Central
4.3.5 APK/XAPK or its decompiled BuildConfig.java; Android is never installed
or executed. A nearby existing analysis folder is detected automatically;
otherwise select the APK/XAPK once. No proprietary app archive or app client
secret is redistributed or embedded in this project's source/executable.
The extracted configuration is cached encrypted for this Windows user.

An acquired credential is preserved encrypted even before local verification,
under `%LOCALAPPDATA%\ArrisControl\Credentials\Acquired`, explicitly marked
unverified. **Load saved acquisition** can reload it without another cloud
sign-in and try local verification later. An inventory mismatch or failed
serial check cannot create a verified record. MFA and additional Cognito
challenges are reported instead of guessed or bypassed. Account-password
recovery and factory onboarding are not implemented.

### Credential retention

Verified/acquired/imported router credentials are retained as separate files
under `%LOCALAPPDATA%\ArrisControl\Credentials`, with unique versions;
older records are not overwritten or deleted. Successfully loaded credentials
from outside that folder are copied into it. Use **Open credentials folder**
or **Use a saved credential / history**. Protocol material is encrypted in
the `Protocol` subfolder. Cloud-account passwords and session tokens are not
stored. Every encrypted record is bound to the current Windows user/PC using
DPAPI; simply copying a record to a different PC does not make it decryptable.
Credentials and target-device identifiers are not included in releases.

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

**Bypass (bridge) mode** is shown on Overview and Internet/DNS, with its current
status and dedicated **Turn bypass ON** and **Turn bypass OFF** buttons.
Enabling bypass requires confirmation of a warning about losing management
access and potentially needing a physical reset. Unknown modes disable both buttons.
The off button restores IPv4/IPv6 router mode only after a reviewed save and
only if the local API is reachable. This is not a verified recovery method:
local reachability/login while bridged was confirmed in one user test after
an address change, but turning bypass off and WAN-disconnected operation remain
unverified.

The earlier Windows client verified local login and protected configuration
reads on a W30-family mAX. Native protocol, payload, stale-update, privacy,
UI, cloud-acquisition protocol, and update-replacement behavior have offline tests. Native live operation
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
No build/test accesses a router, signs in to a real cloud account, or decrypts
a real user credential. Fictional DPAPI persistence fixtures run where the
Windows profile permits it and explicitly report a skip otherwise. Static
reference extraction was additionally checked against the user's owned XAPK
without outputting its constants, saving configuration, or using Android.

GitHub Actions builds/checks pushes to `main` and creates a release for the
project's version if that tag is not already published. Increase the project
version for subsequent releases. Workflow actions are pinned to commit hashes.

This is an independent project, not an ARRIS/SURFboard product. Protocol
mapping was derived from behavior and static API models; APKs, vendor firmware,
user identifiers, credentials, and diagnostic responses are not redistributed.

## Automatic local address discovery

On connection and refresh, the app checks the saved address against the selected
router certificate. If unavailable, it searches private IPv4 addresses on connected
Wi-Fi/Ethernet networks at port 50011 (32 probes at a time, up to 1024 addresses;
large subnets are limited to the local /24). This sends TLS probes without credentials.
Only a unique matching certificate is eligible for login. An authenticated exact
serial and protected mode read are then required before selecting and saving the
new address. Certificate CN matching is a consistency check, not public-PKI proof.

A new encrypted profile version retains the existing encrypted payload; older
profiles are preserved. No cloud fallback or router setting writes are used for
discovery. Discovery is automatic when a read reconnects after a mode/address
change; settings writes are never retried. Private IPv4 networks reachable through
connected Wi-Fi/Ethernet are supported. Other subnets, isolated clients, changing
addresses during discovery, and unreachable management listeners may prevent it.
Native live automatic reconnection still needs user validation.
