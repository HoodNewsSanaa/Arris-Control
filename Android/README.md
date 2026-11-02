# Arris Control for Android

Install `Arris-Control-android.apk` from an **Android preview** on the repository's Releases page. Allow installation from your browser/file manager when Android prompts. Requires Android 8.0+ on ARM64; the APK also contains x86_64 for emulators. No root or separate .NET installation is needed.

## App updates

Use **Updates** at the top of the app. Automatic checks are off by default;
downloads and Android installation approval stay under your control. Public
releases use a permanent signing key and compatible updates preserve saved logins. Older
disposable-key previews need one final reinstall. See [updater details](UPDATES.md).

## Touchscreen interface

The interface uses a black background, white text, large outlined controls, 60 dp minimum buttons and 64 dp option rows. Connection, discovery and saved routers have separate cards. Account setup and returned-field details expand when needed; settings keep Refresh and Connections actions within reach at the bottom. No teal or blue accents are used.

## Local setup and connection

The primary screen is local-first. No ARRIS account, official app, protocol archive,
or Internet connection is needed by these local actions:

1. **Set up a router over Bluetooth** scans for the existing mAX GATT setup services,
   reads the selected unit's identity/state, and opens the existing guided setup.
   It stages local admin credentials and Wi-Fi settings, sends each confirmed phase
   once, then asks you to join Wi-Fi and verify a normal authenticated local login.
2. **Find ARRIS routers on this Wi-Fi** probes the current connected subnet without
   credentials and lets you select a matching local management API.
3. **Use an existing local admin login** verifies the certificate, exact device serial
   and protected routing mode, then retains the credential using Android Keystore.
4. Tap a saved router to reconnect locally, including bridge mode when its admin
   API is reachable. Startup does not automatically connect or scan the network.
   The app does not equate Wi-Fi credentials with admin credentials.

Bluetooth bring-up is separate from Wi-Fi management. Fresh routers may expose
only Bluetooth. **Offline factory setup is experimental and still needs spare-unit
validation**: the app makes no ARRIS requests, but firmware independence from ARRIS
services is not established. See [offline setup scope](../docs/offline-first-setup.md).

Optional account acquisition remains collapsed under **Optional account access**.
Opening it and signing in explicitly contacts ARRIS. It is not part of local setup.
The optional updater contacts GitHub only when requested or when automatic checks
have been enabled by the user. Existing explicit update preferences are retained.

### WOBA local admin setup

Android 0.4.4 labels WOBA as **Initial admin setup / recovery**. Saved normal
admin credentials remain the ongoing connection path independently of the
onboarding flag. **Check or finish onboarding locally** reads fresh completion
and service flags over the normal API. Its reviewed completion request needs
the router's onboarding process, but no phone Bluetooth connection. If that
process is stopped, the app explains the limit and leaves normal settings usable.
See [0.4.4 details](../docs/android-0.4.4-changes.md).

**WOBA · inspect or establish local admin** opens the local onboarding API after
Bluetooth/network bring-up. Inspect identity, role, WAN/onboarding state and
firmware progress without normal admin credentials. A reviewed **OB_LCAINFO**
request can establish the local admin credential on the inspected W30 build,
followed by a separate normal login and protected identity/mode verification.
An encrypted pending credential and original certificate pin are retained before
the one write, so uncertain attempts can be verified after restart without
repeating setup. Completed onboarding returns **LoginNotApplicable**; the app
does not reset the router. W31 inspections are allowed but its credential-write
path is not yet enabled. [WOBA scope and new capabilities](../docs/woba-local-admin.md).

### Triangle mesh view and adding nodes

**Mesh and devices** draws up to eight known routers, with a triangle arrangement
for three nodes. All known routers also have accessible selection buttons below
the map. Tap a router to view its parent links, medium, directional PHY rates,
signal and the source of the report. More than eight routers remain available in
that list. A node in the satellite inventory alone does not establish its parent
or an active backhaul. Clients and radio/interface tree entries are excluded from
router nodes. Unknown links/rates are shown explicitly; the display is a timestamped
snapshot refreshed with **Refresh**, not a continuously measured speed test.

Backhaul association records are joined to a unique satellite MAC/backhaul MAC.
Only the mapped standard DOWNLINKRATE/UPLINKRATE values are converted from kbit/s;
proprietary TXRATE/RXRATE units are not guessed. Link rate is not throughput.

**Add a mesh node** opens Bluetooth inspection, the main router's locally reported
discovery controls, and a topology refresh. Discovery uses a reviewed, single
AUTO_WOBA write with stale/identity checks and read-back, with a 1–3600 second
timeout. **It does not yet implement or promise complete offline satellite pairing.**
Setting EXTENDER or enabling discovery alone is not reported as a successful join.
No physical unit has been configured or paired as part of this build's tests.

### Record a stalled onboarding state

On **Router over Bluetooth**, choose **Record state for 3 minutes (read-only)**.
The app makes a baseline of the known identity/firmware/status fields, then
sequentially samples role, onboarding state, WAN status, download progress and
upgrade status with a five-second pause between completed samples. It preserves
UTC timestamps, elapsed time, exact escaped UTF-8/hex, service properties and
explicit GATT rejection codes. Serial/MAC bytes are withheld and replaced by
consistent tokens within that capture; unknown/secret fields are never read.

**Stop and save capture** stays available during reads. Leaving the app also
stops recording and closes the connection. A partial report is saved privately
on the phone as each sample completes; **Open saved Bluetooth captures** is
available under More options and Bluetooth inspection after relaunch. Each recording
has its own dated file, so later attempts retain the earlier baseline. Copy/share
is user-started. This is an app read-only capture, not an HCI log of the official
app or a capture/decryption of router WAN traffic. UPGRADING alone does not prove
that firmware is downloading or that a cloud service is required.

### Individual Bluetooth commands

The inspection screen also offers **Set role…** (MAINAP or EXTENDER) and
**Send SETOBCOMPLETE…**. These experimental commands are separate from guided
provisioning and can be attempted with missing bootstrap identity or a reported
UPGRADING state. Each needs explicit confirmation showing the selected unit and
last reported state. It reconnects, checks the onboarding interface and all
previously known application/role/serial/WAN-MAC values, sends one acknowledged
write, attempts read-back, then disconnects. An incomplete write is never retried;
inspect the unit again before another attempt. If an update is really running,
changing onboarding may disrupt it. Use a spare unit and preserve the baseline.
An acknowledgement is not proof of completed setup. EXTENDER alone does not pair
a mesh; neither command installs firmware or contacts ARRIS services.

### Personal builds with the settings bundled

Public releases never include ARRIS's app client secret. If you build your own APK, you can bundle it so sign-in never needs SURFboard Central:

```sh
dotnet run --project Tests/Arris-Control.Tests.csproj -- --export-protocol /path/to/SURFboardCentral_4.3.5.xapk Android/protocol.local.json
dotnet publish Android/Arris-Control.Android.csproj -c Release -p:AndroidPackageFormats=apk -o artifacts
```

`Android/protocol.local.json` is plaintext and gitignored. Keep it and any APK built with it private. CI refuses to build if the file exists, and a self-test fails if a CI build embeds it.

Android Keystore credentials are bound to this app installation; clearing app data or uninstalling loses them. Backups and screenshots are disabled. Account fields allow password-manager autofill; router credentials do not. The router's certificate public key is pinned inside the encrypted credential at the first successful login; later connections refuse any other certificate before sending a credential. See **Router certificate pinning** in the main README.

## Find the router after an address change

Use **Scan for ARRIS routers** on the connection screen. The app checks private IPv4 addresses on the currently connected Wi-Fi/Ethernet subnet at port 50011 (24 probes at a time, up to 1024 addresses, with large networks limited to the device's /24). It requires a currently valid ARRIS-labelled device certificate with a MAC-shaped common name, then checks the fixed management API without credentials. A `SessionTimeout` response confirms that the API is present and requires login. Each result shows a short key fingerprint. Select one to fill the address; saved routers only connect to their pinned key, and first-time verification at a scanned address requires the key that was scanned.

The address field now applies to **saved-router reconnect** as well as account verification. Only a successfully authenticated address is persisted with the encrypted credential. Saved credentials do not require the SURFboard reference again. The installed-reference action runs off the UI thread, checks base and split APKs, and explains when the app is missing or its version does not expose the supported configuration.

## Features and limits

**New in this build:** band steering, DFS channels, Wi-Fi fast lane, static WAN IPv4, light-ring brightness, time zone and blocked websites. Each appears only when your router's firmware returns it. Wi-Fi channels are now a dropdown of valid channels per band, with DFS channels marked. **Tools and tests** runs a speed test from the router and renews the Internet (WAN) connection, each after a confirmation.

**Setup and mesh diagnostics** (under More options) surveys nearby Bluetooth devices for 10 seconds, lists units advertising the mAX setup services, and can map one unit's Bluetooth services and characteristics with their permissions. It reads only identity and state values (model, mode, setup state, WAN status, firmware). The encryption key, credential slots, router password and Wi-Fi passphrases are listed by presence only, never read, and nothing is written.

**Diagnostics** (in the section list) reads about 60 endpoints and reports which ones your firmware answers, with field names and safe values. Names, addresses, MACs, serials and passwords are replaced. It never writes anything. Use **Copy report** or **Share report** to send the output.

From that report, **Software access diagnostics** runs a separate authenticated
read-only sweep of login/web-interface configuration, remote access, vendor
information, cloud features and metrics. It reports whether the known groups
exist and describes their returned fields, with account values and whole secret
subtrees hidden. It does not enable services or expose configuration editors.
Some firmware will return Unsupported/InvalidOption for particular groups.

The app shares the Windows protocol and settings engine: status, mesh/client fields, bridge/bypass ON/OFF, Wi-Fi names/passwords, guest settings, DNS, DHCP, UPnP, reservations/forwarding, priority/QoS, parental-control enable and LED state. Only fields returned by the unit's firmware have editors. All returned nonsecret fields appear below the forms. Settings require a preview confirmation, fresh identity/current-value checks, and a single write with read-back. A lost save reply is reported as unknown and never retried.

Complete factory onboarding and offline satellite pairing remain unverified. Account recovery, MFA challenges, firmware updates and full vendor-app parity are not implemented. A visible SSID that refuses association/IP assignment must be diagnosed separately; the app cannot contact a unit until the phone has a working local network connection.

## Build and verification

GitHub Actions builds on `main`, `codex/android`, relevant PRs and manual dispatch. Permanently signed push builds publish the latest stable Android release. The numbered `android-preview-N` tags are retained for updater compatibility. The workflow runs shared offline regressions, builds the APK, launches it on an Android 15 x86_64 emulator, and checks Android Keystore encryption and a real in-place update that retains an encrypted login. An emulator-host HTTPS router fixture verifies discovery, login/session cookies, address changes and reconnect. Publishing requires all checks and permanent signing.

The optional browser-interface experiment is available only from Software access
diagnostics. Review and confirm it separately. It changes only WEBUIENABLE on the
inspected W30 hardware/firmware and reads back the result; it does not promise a
working browser interface or shell. See [software-access notes](../docs/software-access.md).

Locally with .NET 10, JDK 21 and the Android SDK:

```sh
dotnet workload install android
dotnet run --project Tests/Arris-Control.Tests.csproj
dotnet publish Android/Arris-Control.Android.csproj -c Release -p:AndroidPackageFormats=apk -o artifacts
```

Public updater releases require the permanent signing key stored in GitHub Actions secrets. Unsigned-setup and pull-request builds use disposable keys for testing and are not published to the updater feed. Switching from an older disposable-key preview requires one final reinstall. See [permanent signing and update verification](UPDATES.md).

Shared tests use fictional credentials only. The Keystore smoke test also uses fictional data and performs no router/cloud requests. Passing CI establishes installation/startup/protocol behavior, not physical-router compatibility; live changes still need user testing.
