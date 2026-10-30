Android 0.3.0 — onboarding capture and individual commands:

- Record up to three minutes of read-only Bluetooth state with exact status bytes,
  UTC/elapsed timestamps, service properties and GATT rejection codes. Identities
  are masked; secret/unknown characteristics are not read. A partial report is
  saved privately and can be reopened after relaunch.
- A rejected serial read (including code 115) no longer blocks all other reads.
  Timeouts, cancellations and dropped connections still require reconnecting.
- Add confirmed one-shot MAINAP/EXTENDER role selection and SETOBCOMPLETE controls
  separate from guided setup. They can be attempted with incomplete identity or
  an UPGRADING state; known identities are rechecked, no command is retried, and
  acknowledgements never imply successful onboarding. No firmware is installed.
- First-time setup without ARRIS services remains unverified on physical hardware.

Certificate pinning update (0.2.0):

- The router's TLS public key is pinned inside the encrypted credential at the first successful login. Reconnects, discovery and scan follow-ups refuse any other certificate before sending a credential, closing a same-network impostor gap where a copied MAC common name was accepted.
- Scan results show a short key fingerprint, and first-time verification at a scanned address requires that same key.
- Credentials saved by earlier previews connect only at their saved or entered address, then upgrade to a pinned version automatically.
- CI now also checks that an impostor certificate gets no login and that an older credential upgrades against the HTTPS fixture.

Touchscreen and discovery update (0.1.1):

- Black-and-white interface with large touch targets, rounded outlined cards, expandable first-time setup, large option pickers and persistent settings navigation.

- Added **Scan for ARRIS routers**: bounded, anonymous TLS/API checks on the connected private subnet, with selectable responding addresses.
- The edited IPv4 address now applies when reconnecting a saved router and is retained after successful identity verification.
- Connection errors show the destination and distinguish refusal/unreachable/TLS failures.
- Installed SURFboard reference import checks base/split APKs off the UI thread and gives actionable missing/unsupported-app errors. Saved local reconnect requires no protocol reference.
- CI now exercises a real mock HTTPS router through Android's network stack, including anonymous discovery, login, session cookies, encrypted profile address updates and reconnect.

Android preview for ARRIS SURFboard mAX local management (Android 8.0+, ARM64 phones and x86_64 emulators).

- Native interface with existing ARRIS-account acquisition, saved local credentials, identity verification and automatic address discovery.
- Bridge/bypass ON/OFF, Wi-Fi, DNS, DHCP, reservations/ports, QoS, parental enable state, LED controls and redacted returned fields where supported by firmware.
- Android Keystore encryption; backups and screenshots disabled. Windows DPAPI files are not portable to Android.
- Local traffic explicitly uses connected Wi-Fi/Ethernet, without requiring a validated Internet connection. The phone must already be able to join that network.
- Import an installed SURFboard Central reference or a local 4.3.5 APK/XAPK/BuildConfig.java for account sign-in. Vendor app secrets are not bundled. Existing local management credentials can also be entered directly.
- Reviews each change, checks for stale values, sends once, and reads back. Lost replies are never retried automatically.

This APK is a preview signed with the build runner's Android development key. It is installable but is not a production-signed Play Store release; a later build may require uninstalling this preview, which also removes its locally encrypted credentials.

GitHub CI tests protocol fixtures and real Android Keystore on an emulator. Live router operation and offline-WAN Wi-Fi behavior require physical-device testing. The APK does not change firmware or fix wireless association failures.
# Offline first-time setup experiment

- Replaced guessed encrypted single-slot Bluetooth writes with the observed
  4.3.5 text-fragment protocol: separate username/password selectors, slots 4
  through 1, and the actual setup-complete command.
- Added three separately confirmed phases, complete-interface preflight,
  identity checks before each phase, and stop-on-error behavior without retries.
- Preserve the proposed local login encrypted before any write and expose pending
  logins on the start screen for local verification after a restart.
- No ARRIS-server calls or SURFboard reference are needed in this flow. Actual
  first-time setup still needs hardware validation on a spare factory-reset unit.

# Bluetooth identity read feedback

- Always show Model, Serial, WAN MAC and state fields, with explicit empty,
  unavailable or GATT error results instead of hiding failed reads.
- Add reconnect/refresh and activity-log export directly to Bluetooth inspection.
- Include the device application, upgrade status and download progress as separate
  read-only fields to investigate a persistent onboarding UPGRADING response.
- Read Android 13+ callback value snapshots and ignore callbacks for unrelated
  characteristics; require reconnection after incomplete GATT operations.
- Lock setup during the reported UPGRADING state and recheck state before each
  setup phase. Keep complete identity verification before all setup writes.

# Android in-app updater

- Updates at the top of the app checks for Android builds, downloads and verifies
  the APK, and opens Android's installation confirmation without visiting GitHub.
- Optional launch checks run at most every six hours. Downloads are user-started
  and show size/progress; router control stays available without Internet.
- APK checks reject wrong package/version, size/checksum changes and different
  signing keys. Permanent signing is required for future public update releases.
- Emulator verification checks a real in-place APK upgrade and retention of an
  encrypted local login and Keystore key.
- Explicit Bluetooth read rejections now leave the connection available for
  further diagnostic reads. Added a confirmed one-shot primary initialization
  for eligible OB-mode units; active upgrades remain locked.

