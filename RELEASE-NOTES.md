Adds a dedicated Bypass (bridge) mode panel on Overview and Internet/DNS, with
status and a reviewed Turn bypass OFF action. Enabling remains blocked under
the existing recovery restriction; bridge-mode access/recovery is not verified.

Adds native credential acquisition: SRP cloud sign-in, existing SSO retrieval,
account inventory selection, local login and exact serial verification, then
Windows DPAPI-protected storage. Static APK/XAPK protocol reading requires no
Android installation/execution and embeds no vendor client secret in the app.

Adds a Credentials menu, retained credential history, Open credentials folder,
and protected saved acquisitions that can be reloaded without cloud sign-in.
Imported, acquired, and loaded router credentials are retained in versioned
encrypted files under LocalAppData. Cloud-account passwords/tokens are not saved.

Keeps the friendly settings forms, Full settings browser, and built-in updater.
No live router changes or real cloud-account logins were used to test this
release. Native acquisition/saves and the complete updater cycle need live
validation. Offline tests cover SRP vectors, SSO decryption, account/inventory
handling, APK/XAPK parsing, device mismatch rejection, settings/write guards,
WPF layout, update policy, and atomic replacement. Windows persistence fixtures
report a skip if the test profile cannot use DPAPI.
