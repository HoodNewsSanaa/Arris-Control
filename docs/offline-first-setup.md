# Offline first-time setup experiment

This path attempts first-time local access on a factory-reset primary mAX router
without an ARRIS account, SURFboard reference, or ARRIS-server requests. **It is
not yet proven on physical hardware.** It does not reset a unit, register it to
a cloud account, configure a satellite, or replace router firmware.

## What was verified statically

The earlier experimental screen guessed that Bluetooth values were AES-CBC
ciphertext sent in a single slot, and sent `1` as the completion command.
Those guesses do not match SURFboard Central 4.3.5 (Android version code 232).
Static inspection of that app shows:

- `CreateWifiPresenter` selects username with `SET:1`, then password with
  `SET:2`, at characteristic `ab1c`.
- Each value is split into 16-character fragments. Slots 4, 3, 2, 1 are
  written in that order, including empty unused slots. The final write to
  slot 1 is the boundary after which the app starts its next operation.
- Username/password use `ab1d` through `ab20`. SSID uses `ab01` through
  `ab04`; Wi-Fi passphrase uses `ab05` through `ab08`.
  These are fragments of one value, not four radio profiles.
- `BLEServiceImpl.writeData` forwards the string to
  `BluetoothLeService.writeCharacteristic`, which sets its value directly.
  This path adds no application-layer encryption. The key used for cloud
  password storage must not be assumed to be a BLE write-encryption key.
- `RegisteringWifiModel.obaComplete` writes `SETOBCOMPLETE` to `aa1a`.

Only protocol behavior is described here; no vendor binary, source, cloud
client secret, physical hash, or encryption key is included in this repository.
The inspected public XAPK variant had SHA-256
`fbbd7a805a203058335ca75fa0e05890f1bfeebe718a018d945146506b3cf2ff`.

ARRIS also publishes a [mAX platform source package](https://sourceforge.net/projects/surfboard-max-platforms.arris/files/).
Its README describes a Broadcom WR11AX build. A source package's existence does
not establish that another manufacturer's firmware is compatible.

## App flow

1. More options → Set up a new router over Bluetooth. Inspect the identity,
   then open guided setup. Use a spare factory-reset primary unit.
2. Choose a local username, local admin password, Wi-Fi name and Wi-Fi password.
   The default username is local to this experiment; no ARRIS account is created.
3. Unlock the attempt, then confirm each phase separately:
   - Local login: 10 acknowledged writes.
   - Wi-Fi: 8 acknowledged writes.
   - Completion: one acknowledged `SETOBCOMPLETE` write.
4. Join the new Wi-Fi and verify the proposed local login. Scanning and login
   use the connected local Wi-Fi/Ethernet network, including without Internet.

Before the first write, the proposed username/password, target identity and
SSID are saved in a separate Keystore/DPAPI-encrypted pending record. The Wi-Fi
password is not saved. Pending records are available from the start screen
after a process restart. They are not shown as verified saved routers.

Each phase checks that the full writable interface exists and re-reads the
serial, WAN MAC and onboarding state. A reported UPGRADING state locks guided-provisioning writes.
All writes are sequential and acknowledged. Failed,
cancelled or uncertain writes permanently stop that session; there is no
automatic retry or automatic resume. The UI closes the BLE connection after
failure. No secret BLE characteristic is read, including the vendor key.

Verification sends the proposed login only to the local HTTPS API, checks
the certificate MAC and authenticated serial/mode, then saves a public-key
pin inside the encrypted verified credential. A scan-selected address also
requires the exact certificate key seen by that scan. The pending record is
removed only after the verified credential has been saved.

## Remaining hardware questions

The official client performs cloud registration and cloud credential uploads
around its local configuration work. Static client analysis cannot establish
whether the router needs them. This experiment deliberately makes neither call.
It also does not guess cloud endpoint fields, physical-hash authorization,
backhaul credentials, firmware-specific timezone values or mesh-pairing writes.

A useful hardware result is either a successful authenticated local login
with Internet unavailable, or the exported BLE log showing the first failed
phase and the router's resulting Wi-Fi/LED behavior. An acknowledged BLE write
alone is not evidence that the firmware applied it. Do not repeat an uncertain
phase automatically or use this on a primary network that must stay available.

Offline fixtures verify the exact byte sequence, empty and maximum-length
fragments, every one of the 19 lost-acknowledgement boundaries, identity changes,
cancellation, phase ordering, and encrypted pending-record round-trip/tamper
rejection. CI runs these on desktop and inside the Android emulator. Physical
Bluetooth and factory setup remain untested until a user tests a spare unit.

If identity cannot be read, the Bluetooth inspection screen shows each field
with its result, including empty replies, absent characteristics and GATT errors.
Reconnect and refresh from that screen before attempting setup. Its activity log
can be exported without entering setup; serial/MAC values are withheld.
Android 13+ reads use the value supplied with the callback, and a callback for
another characteristic cannot complete the pending read. An incomplete GATT
operation requires a fresh connection. The characteristic IDs have been checked
against the inspected 4.3.5 BLEAttributes enum; identifiers are never invented or
replaced by the Bluetooth advertisement address.

Inspection also reports the application name (aa02), upgrade status (aa13) and
download progress (aa12) separately. An onboarding UPGRADING string by itself
does not establish what is downloading or whether an ARRIS server is required.
The stock pairing flow reads the application name and device mode, and can assign
MAINAP to a unit reporting OB before its later registration/identity steps.
When the device reports the expected maxrouter application and OB mode, and is
not reporting an active upgrade, inspection offers a separately confirmed
primary-router initialization. It rechecks those values, sends MAINAP once and
reads the role back. A lost acknowledgement is reported as unconfirmed and is
never retried. Credential/Wi-Fi writes still require complete identity afterward.
Its effect on this unit's missing identity fields needs hardware observation.

An explicit rejected read (including status 115 from the test unit) is shown on
its field and does not prevent reading other diagnostic fields. Incomplete,
timed-out or cancelled operations still require reconnection. Status 115's exact
firmware-specific meaning is unconfirmed; it is not treated as successful data.

The target is operation when ARRIS services are unavailable. A test with WAN
Internet present and ARRIS destinations blocked distinguishes that from a
completely disconnected WAN. App-side omission of cloud requests alone does not
prove that the router firmware is independent of those services.

## Capture before an individual command

Android 0.3.0 provides a bounded read-only capture on the Bluetooth inspection
screen. Record the stuck unit before changing role/completion. The saved report
keeps independent aa08/aa11/aa12/aa13 readings and raw bytes, not an interpretation
that UPGRADING must be a genuine firmware download. GATT 115 is preserved without
assigning it an unverified meaning. A complete rejected read permits later
read-only fields; incomplete reads end the recording and close the connection.

The same screen exposes separate, confirmed MAINAP/EXTENDER and SETOBCOMPLETE
experiments. They permit incomplete bootstrap identity and UPGRADING, but recheck
every previously known application/role/serial/WAN-MAC value on a fresh connection.
They require a uniquely mapped onboarding interface and a fresh maxrouter app
identity, perform one acknowledged write, attempt nonsecret read-back and always
disconnect. No write is retried. A real update could be disrupted by changing
onboarding. Guided setup retains full identity/upgrade guards. No live command or
successful factory setup has been demonstrated by CI.

To establish firmware independence, compare this baseline with phone Bluetooth
HCI logging and router WAN captures during controlled official setup. The Android
recorder cannot observe another app's Bluetooth commands or decrypt WAN HTTPS.
Use a working unit as a management baseline without resetting it. Capture raw
network/HCI originals privately; publish only redacted extracts. Server emulation
and skip-update behavior are still hypotheses until traffic and trust behavior
are verified.
