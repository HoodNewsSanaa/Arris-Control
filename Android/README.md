# Arris Control for Android

Install `Arris-Control-android.apk` from an **Android preview** on the repository's Releases page. Allow installation from your browser/file manager when Android prompts. Requires Android 8.0+ on ARM64; the APK also contains x86_64 for emulators. No root or separate .NET installation is needed.

## App updates

Use **Updates** at the top of the app. Automatic checks run when the app opens;
downloads and Android installation approval stay under your control. Public
releases use a permanent signing key and compatible updates preserve saved logins. Older
disposable-key previews need one final reinstall. See [updater details](UPDATES.md).

## Touchscreen interface

The interface uses a black background, white text, large outlined controls, 60 dp minimum buttons and 64 dp option rows. Connection, discovery and saved routers have separate cards. Account setup and returned-field details expand when needed; settings keep Refresh and Connections actions within reach at the bottom. No teal or blue accents are used.

## Connect

1. Join your router's Wi-Fi.
2. Open Arris Control, enter your ARRIS / SURFboard Central account email and password, and tap **Sign in**.

That's it. The app finds the SURFboard settings it needs, signs in, picks your router (or asks if your account has several), finds it on your Wi-Fi by its certificate, verifies it, and connects. Your account password and cloud tokens are never saved; the router's local login is saved encrypted, and later launches reconnect automatically without signing in.

**SURFboard settings.** Account sign-in needs a few settings from the SURFboard Central app. Arris Control looks for them automatically: its own encrypted copy, then a copy bundled into a personal build (below), then the installed SURFboard Central app. If none is found, it offers to install SURFboard Central from Google Play. You don't need to open or set it up, and you can uninstall it after the first sign-in. You can also pick a SURFboard Central 4.3.5 APK/XAPK under **More options**.

**More options** also has: scanning for routers, a manual router address, a previously acquired credential, an existing local management login (not the Wi-Fi password), and Bluetooth setup for a factory-reset router.

**Experimental first-time offline setup:** Bluetooth setup now uses the text
fragments and slot order observed in SURFboard Central 4.3.5, followed by its
actual setup-complete command. It saves an encrypted pending local login before
writing, stops after any uncertain result, and provides local HTTPS verification
without an ARRIS account or protocol reference. This still needs a spare-unit
hardware test; firmware may require cloud registration. See
[the exact scope, procedure and evidence](../docs/offline-first-setup.md).

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

The app shares the Windows protocol and settings engine: status, mesh/client fields, bridge/bypass ON/OFF, Wi-Fi names/passwords, guest settings, DNS, DHCP, UPnP, reservations/forwarding, priority/QoS, parental-control enable and LED state. Only fields returned by the unit's firmware have editors. All returned nonsecret fields appear below the forms. Settings require a preview confirmation, fresh identity/current-value checks, and a single write with read-back. A lost save reply is reported as unknown and never retried.

This does not implement factory onboarding, account recovery, MFA challenges, firmware updates or all vendor-app features. A visible SSID that refuses association/IP assignment must be diagnosed separately; the app cannot contact a unit until the phone has a working local network connection.

## Build and verification

GitHub Actions builds on `main`, `codex/android`, relevant PRs and manual dispatch. Permanently signed push builds publish a separate Android prerelease without replacing the latest stable Windows release. The workflow runs shared offline regression tests, publishes the APK, launches it on an Android 15 x86_64 emulator, and runs those fixtures plus Android Keystore encryption/tamper tests inside the actual app. It also runs an emulator-host HTTPS router fixture to verify anonymous API discovery, real login/session cookies, verified address changes and reconnect from an encrypted saved profile. It uploads APK/checksum/UI-preview artifacts. Public updater releases are published only when permanent signing is configured.

Locally with .NET 10, JDK 21 and the Android SDK:

```sh
dotnet workload install android
dotnet run --project Tests/Arris-Control.Tests.csproj
dotnet publish Android/Arris-Control.Android.csproj -c Release -p:AndroidPackageFormats=apk -o artifacts
```

Public updater releases require the permanent signing key stored in GitHub Actions secrets. Unsigned-setup and pull-request builds use disposable keys for testing and are not published to the updater feed. Switching from an older disposable-key preview requires one final reinstall. See [permanent signing and update verification](UPDATES.md).

Shared tests use fictional credentials only. The Keystore smoke test also uses fictional data and performs no router/cloud requests. Passing CI establishes installation/startup/protocol behavior, not physical-router compatibility; live changes still need user testing.
