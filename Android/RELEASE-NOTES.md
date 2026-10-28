Android preview for ARRIS SURFboard mAX local management (Android 8.0+, ARM64 phones and x86_64 emulators).

- Native interface with existing ARRIS-account acquisition, saved local credentials, identity verification and automatic address discovery.
- Bridge/bypass ON/OFF, Wi-Fi, DNS, DHCP, reservations/ports, QoS, parental enable state, LED controls and redacted returned fields where supported by firmware.
- Android Keystore encryption; backups and screenshots disabled. Windows DPAPI files are not portable to Android.
- Local traffic explicitly uses connected Wi-Fi/Ethernet, without requiring a validated Internet connection. The phone must already be able to join that network.
- Import an installed SURFboard Central reference or a local 4.3.5 APK/XAPK/BuildConfig.java for account sign-in. Vendor app secrets are not bundled. Existing local management credentials can also be entered directly.
- Reviews each change, checks for stale values, sends once, and reads back. Lost replies are never retried automatically.

This APK is a preview signed with the build runner's Android development key. It is installable but is not a production-signed Play Store release; a later build may require uninstalling this preview, which also removes its locally encrypted credentials.

GitHub CI tests protocol fixtures and real Android Keystore on an emulator. Live router operation and offline-WAN Wi-Fi behavior require physical-device testing. The APK does not change firmware or fix wireless association failures.
