# WOBA local onboarding and admin setup

Android 0.4.1 adds **WOBA · inspect or establish local admin** on the connection
screen and a shortcut from Bluetooth inspection. Bluetooth remains the initial
bring-up mechanism when a fresh unit exposes no IP network. WOBA is the separate
local HTTPS onboarding interface, not root access and not an ARRIS cloud API.

## Available functions

- A certificate-pinned WOBA login at the selected private local address.
- Read device identity, MAINAP/EXTENDER/ONBOARDING role, WAN state, setup/satellite
  state and count, model/capabilities, firmware version and upgrade progress.
- Copy a redacted onboarding diagnostic report, including unavailable endpoints.
- On the inspected W30 firmware, set a chosen local admin username/password through
  OB_LCAINFO after target confirmation and a separate review. This can establish
  local administration without obtaining credentials from an ARRIS account.
- Verify the new credential through a different, normal login session, protected
  serial and routing-mode reads, then retain a verified encrypted credential and
  open the existing Wi-Fi/settings/mesh interface.
- Keep an encrypted pending recovery credential if the write reply or normal
  verification is uncertain. Recover it after restart without repeating the write.

WOBA login uses the known vendor onboarding account once per inspection; there is
no password guessing loop. The firmware rejects it with LoginNotApplicable after
onboarding is complete. Owning the device does not change that firmware state.
The app does not automatically reset it or force it back to onboarding.

## Scope and safeguards

Onboarding transport is isolated from normal sessions. Its read allowlist excludes
physical-hash, encryption-key and Wi-Fi-password queries. Device-info responses
contain a physical hash in stock firmware; projection discards it and any unknown
fields before retaining the inspection. Satellite projection excludes pairing
hashes. Exported reports additionally redact identifying values.

The only accepted WOBA setter is an exact MODIFY/OB_LCAINFO payload with two string
fields. No reset, root exploit, shell, firmware command, cloud configuration,
backhaul provisioning or Wi-Fi write is admitted. Admin setup currently requires
incomplete onboarding, MAINAP, valid identity, W30 / AXR.0814.240522.10, and an idle
or completed upgrade report. Other reported models, including W31, can be inspected
but are not enabled for credential writes without compatible hardware evidence.
EXTENDER/ONBOARDING can stage rather than activate the credential, so they are not
presented as verified normal-admin provisioning.

A fresh inspection rechecks serial, WAN MAC, model, role, firmware, completion and
upgrade state after review. Android 0.4.2 removes manual serial-number entry;
the review identifies the selected router and automatic identity checks remain.
A Keystore-encrypted pending credential with the observed public-key
pin and local address is persisted before one setter request. An uncertain write
is never retried. Verification requires that original pin, even after rediscovery;
it cannot silently fall back to trusting a different key. Verified records are
created only after a separate normal login and exact serial/protected mode read.
Old records remain intact. Legacy pending Bluetooth records retain compatibility.

## What this does not establish

### Android 0.4.3: completion from WOBA

The WOBA inspection screen has a **Finish onboarding…** action.
It first verifies a saved normal admin login against the inspected certificate,
then lets the user select the nearby Bluetooth unit. Serial and WAN MAC are
matched automatically; no serial entry is requested. After confirmation it
refreshes WOBA identity/role/firmware/update state, verifies normal admin access
again, and sends the observed Bluetooth `SETOBCOMPLETE` command once. There is
no HTTP completion fallback. Earlier hardware notes record an HTTP completion
reply that did not actually change onboarding state.

The result screen separately checks normal admin access and a recognized
onboarding result: explicit `OBASTATUS=0` from a fresh normal login, or, if that
login omits the flag, explicit `OBASTATUS=0` / `LoginNotApplicable` from WOBA.
An acknowledgement, unavailable response or unrecognized state does not count
as completion. A lost write reply is never automatically retried. Completion
still requires physical hardware validation and a running Bluetooth service.
The same W30 firmware, MAINAP, identity and idle-update guards apply.

This is local administrator access, not UID 0/root or persistent root. No CPU
sensor endpoint was found in this WOBA API. It does not complete offline satellite
pairing, bypass the three-node limit, enable automatic controller failover, or
prove that router firmware no longer depends on ARRIS services. Those need
separate evidence and implementation.

The protocol was mapped from the owner's W30 firmware and earlier successful
WOBA admin setup. Offline fixtures test state/identity/upgrade guards, exact
payload, recovery encryption, secret projection, cancellation and no retry. The
Android TLS fixture additionally tests real WOBA cookies, one credential request,
normal-session isolation and independently verified normal local login.

## Live read-only result, 2026-11-02

The owner-authorized local discovery found one management API on the connected
network. Using the implemented transport, WOBA login succeeded and all seven
onboarding reads answered. The unit reported W30 / AXR.0814.240522.10, MAINAP,
COBA 2.0.1, WAN Up and LatestSoftwareInstalled with zero progress. No normal admin
credential was changed and no WOBA settings write was sent. Private identifiers
and physical hashes were excluded from the retained/shareable report.

This establishes real local WOBA inspection on that firmware. The new app's
credential-write/verification transaction is covered by fixtures and the prior
successful manual OB_LCAINFO experiment, not a new live password change in this
implementation session. The app recognizes the observed LatestSoftwareInstalled
idle state instead of guessing its meaning from a generic UPGRADING label.
