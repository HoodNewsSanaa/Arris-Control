# In-app Android updates

Open **Updates** at the top of the app to check, download and install a newer
Android build without opening GitHub. The optional automatic check is enabled
initially and runs when the app opens, at most once every six hours. It downloads
metadata only. APK downloads require a tap and show their size. Android asks the
user to approve installation; root is not required.

The updater uses this repository's numbered Android releases. It ignores Windows
releases, installed versions and downgrades. Downloads must use the fixed project
asset names and HTTPS GitHub destinations, stay within size limits, and match
the release checksum. Before installation, Android's archive parser checks the
package name, version and signing certificate against the installed app.
A different signing key is rejected. The installer receives a temporary read
grant for one app-private APK through a provider that is not exported.

Update requests use Android's default Internet network directly and do not
change the local router connection's process binding. An offline update check
does not block startup or local router control. No router credential, ARRIS
password, cloud token or protocol reference is sent to GitHub.

## Permanent signing

Future public Android releases require these repository Actions secrets:

- ANDROID_KEYSTORE_BASE64: base64 of the private PKCS12 signing keystore.
- ANDROID_KEYSTORE_PASSWORD: its password.
- ANDROID_KEY_ALIAS: the signing alias.

The public certificate fingerprint is in [release-signing.sha256](release-signing.sha256).
CI verifies that a permanently signed build matches it. Private signing material
is never committed or published. Pull-request builds do not receive these
secrets. When signing is not configured, CI builds and tests a disposable-key
APK but does not publish it to the updater feed.

Older previews used disposable keys. Switching from one to the permanent signer
requires one final uninstall/reinstall, which erases that installation's saved
logins. Later compatible updates preserve the app data and Keystore key.
Keep a private backup of the keystore if managing signing outside Actions.

## Verification

Portable fixtures cover release selection, URL/size limits, downgrades, checksum
mapping and tampered downloads. The Android emulator parses real signed APKs,
rejects a differently signed APK, reads the installation content URI and checks
provider access limits. It then installs a newer APK in place and verifies that
an existing Keystore-encrypted fictional router login still decrypts.
