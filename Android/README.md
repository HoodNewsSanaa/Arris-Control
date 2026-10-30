# Arris Control for Android

**[Download the newest APK](https://github.com/HoodNewsSanaa/Arris-Control/releases/download/android-latest/Arris-Control-android.apk)** ([release page](https://github.com/HoodNewsSanaa/Arris-Control/releases/tag/android-latest)). Allow installation from your browser/file manager when Android prompts. Requires Android 8.0+ on ARM64; the APK also contains x86_64 for emulators. No root or separate .NET installation is needed. Numbered `android-preview-N` releases keep earlier builds.

## Touchscreen interface

The interface uses a black background, white text, large outlined controls, 60 dp minimum buttons and 64 dp option rows. Connection, discovery and saved routers have separate cards. Account setup and returned-field details expand when needed; settings keep Refresh and Connections actions within reach at the bottom. No teal or blue accents are used.

## Connect

1. Join your router's Wi-Fi.
2. Open Arris Control, enter your ARRIS / SURFboard Central account email and password, and tap **Sign in**.

That's it. The app finds the SURFboard settings it needs, signs in, picks your router (or asks if your account has several), finds it on your Wi-Fi by its certificate, verifies it, and connects. Your account password and cloud tokens are never saved; the router's local login is saved encrypted, and later launches reconnect automatically without signing in.

**SURFboard settings.** Account sign-in needs a few settings from the SURFboard Central app. Arris Control looks for them automatically: its own encrypted copy, then a copy bundled into a personal build (below), then the installed SURFboard Central app. If none is found, it offers to install SURFboard Central from Google Play. You don't need to open or set it up, and you can uninstall it after the first sign-in. You can also pick a SURFboard Central 4.3.5 APK/XAPK under **More options**.

**More options** also has: scanning for routers, a manual router address, a previously acquired credential, an existing local management login (not the Wi-Fi password), and Bluetooth setup for a factory-reset router.

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

GitHub Actions builds on `main`, `codex/android`, relevant PRs and manual dispatch. Push builds publish a separate Android prerelease without replacing the latest stable Windows release. The workflow runs shared offline regression tests, publishes the APK, launches it on an Android 15 x86_64 emulator, and runs those fixtures plus Android Keystore encryption/tamper tests inside the actual app. It also runs an emulator-host HTTPS router fixture to verify anonymous API discovery, real login/session cookies, verified address changes and reconnect from an encrypted saved profile. It uploads APK/checksum/UI-preview artifacts and publishes the tested APK on push.

Locally with .NET 10, JDK 21 and the Android SDK:

```sh
dotnet workload install android
dotnet run --project Tests/Arris-Control.Tests.csproj
dotnet publish Android/Arris-Control.Android.csproj -c Release -p:AndroidPackageFormats=apk -o artifacts
```

The included CI APK uses a development signing key generated by that build runner. Each build can have a different signature, requiring uninstall/reacquisition to replace it. For stable personal updates, supply your own persistent signing key through Android's normal `AndroidKeyStore`, `AndroidSigningKeyStore`, `AndroidSigningKeyAlias`, `AndroidSigningKeyPass` and `AndroidSigningStorePass` build properties. Never commit keys or passwords.

Shared tests use fictional credentials only. The Keystore smoke test also uses fictional data and performs no router/cloud requests. Passing CI establishes installation/startup/protocol behavior, not physical-router compatibility; live changes still need user testing.
