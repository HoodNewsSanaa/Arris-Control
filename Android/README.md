# Arris Control for Android

Install `Arris-Control-android.apk` from an **Android preview** on the repository's Releases page. Allow installation from your browser/file manager when Android prompts. Requires Android 8.0+ on ARM64; the APK also contains x86_64 for emulators. No root or separate .NET installation is needed.

## Touchscreen interface

The interface uses a black background, white text, large outlined controls, 60 dp minimum buttons and 64 dp option rows. Connection, discovery and saved routers have separate cards. Account setup and returned-field details expand when needed; settings keep Refresh and Connections actions within reach at the bottom. No teal or blue accents are used.

## Connect

1. Join the Arris Wi-Fi (or the same local Ethernet network). If Android warns that Internet is unavailable, choose to stay connected.
2. Open Arris Control. Select a saved router, or acquire its existing local management credential through your ARRIS account.
3. For account acquisition, import the protocol reference from an installed SURFboard Central app, or select your own SURFboard Central 4.3.5 APK/XAPK/BuildConfig.java. Nothing is installed or executed from the imported reference. The supported protocol configuration is validated and cached encrypted.
4. Enter your ARRIS account email/password, sign in, select the exact owned unit, and enter its current private IPv4 address (commonly `192.168.7.1` in router mode). Initial acquisition needs Internet; local management after verification does not.
5. The app verifies certificate MAC, authenticated serial and protected routing mode before saving a local credential. Alternatively enter an existing **local management** username/password, serial and certificate MAC; these are not the Wi-Fi password.

Account passwords and cloud session tokens are not retained. Acquired local credentials are retained encrypted so local verification can be retried without signing in again. Normal settings reads/writes go directly to local port 50011. The app binds its own local traffic to connected Wi-Fi/Ethernet, including networks Android has not validated for Internet. Account sign-in restores the app's default Internet routing, allowing cellular fallback where Android provides it.

Windows DPAPI-encrypted JSON cannot be imported on Android. Android Keystore credentials are bound to this app installation; clearing app data or uninstalling loses them. Backups and screenshots are disabled. The router's certificate public key is pinned inside the encrypted credential at the first successful login; later connections refuse any other certificate before sending a credential. See **Router certificate pinning** in the main README.

## Find the router after an address change

Use **Scan for ARRIS routers** on the connection screen. The app checks private IPv4 addresses on the currently connected Wi-Fi/Ethernet subnet at port 50011 (24 probes at a time, up to 1024 addresses, with large networks limited to the device's /24). It requires a currently valid ARRIS-labelled device certificate with a MAC-shaped common name, then checks the fixed management API without credentials. A `SessionTimeout` response confirms that the API is present and requires login. Each result shows a short key fingerprint. Select one to fill the address; saved routers only connect to their pinned key, and first-time verification at a scanned address requires the key that was scanned.

The address field now applies to **saved-router reconnect** as well as account verification. Only a successfully authenticated address is persisted with the encrypted credential. Saved credentials do not require the SURFboard reference again. The installed-reference action runs off the UI thread, checks base and split APKs, and explains when the app is missing or its version does not expose the supported configuration.

## Features and limits

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
