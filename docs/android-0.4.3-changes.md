# Android 0.4.3 changes

The owner authorized building and publishing this batch on 2026-11-02, following
Android 0.4.2, build 81. For future batches, collect fixes and wait for explicit
confirmation before building or publishing another APK. Pushing to
`codex/android` triggers its build and release workflow.

## WOBA completion action

- Add **Finish onboarding…** to WOBA inspection, using the observed Bluetooth
  `SETOBCOMPLETE` command rather than an unverified HTTP completion request.
- Require a working saved normal admin login and automatically match Bluetooth
  serial/WAN MAC to the pinned WOBA identity; no serial-number entry.
- Refresh identity, role, firmware and update state before the one command.
- Offer separate verification of normal admin access and explicit onboarding
  completion. Unknown replies and Bluetooth acknowledgement do not imply success.
- Keep physical Bluetooth/completion validation outstanding.

Validation: portable shared tests pass, including normal-login failure, changed
identity/pin/address/role, busy updates, mismatched Bluetooth identities,
cancellation and no retry after an uncertain write. `git diff --check` passes.
The release workflow validates the Android UI/transport in the emulator. Physical
Bluetooth completion remains unverified. No live router command was sent during
implementation.

## Manual startup connections and section loading

- Remove automatic saved-router connection on launch. The existing saved-router
  buttons and **Find ARRIS routers on this Wi-Fi** start connections/discovery
  explicitly, so opening the app leaves Bluetooth and other tools available.
- Reuse the authenticated pinned transport when changing sections, without an
  extra discovery handshake or subnet scan. Expired sessions can log in once.
- Show query progress. Stop remaining page queries after a transport failure or
  timeout, mark the session disconnected, and reconnect on an explicit Refresh.
- Native fixture coverage now checks that changing sections does not resolve the
  router again as part of the release workflow.

## Bluetooth identity reads returning GATT 115

- Keep model/serial/WAN-MAC rejection statuses distinct from empty or absent data.
- Recognize a `maxrouter` application even when identity fields are unavailable.
- Explain the reported decimal/hex status (115 / 0x73), preserve other readable
  state fields, and provide a diagnostic-log shortcut and explicit reconnect.
- Do not assign an unverified cause to 115, invent identity values or change roles
  automatically. Require a unique expected onboarding service before reading.
- Add portable and native callback regression coverage for all three identity
  reads being rejected while subsequent nonsecret reads remain possible.

The cause of the physical router's read errors and the reported WAN interruption
after its LED brightness change is unresolved. These source changes do not claim
to repair that router state. No additional live settings writes were sent.

## Clear status summaries

- Add status summaries to normal Overview, Bluetooth inspection and WOBA inspection.
- Keep mesh role (MAINAP / EXTENDER / OB), routing mode (bridge / router),
  onboarding completion, WAN link and firmware activity distinct.
- Retain the normal login's explicit OBASTATUS flag and observation time; show
  connection/mode/onboarding in the navigation card on every settings section.
- Show installed model/firmware from the authenticated identity read, without
  additional discovery. Show source/time for prior WOBA role/WAN observations;
  attach them only to the same serial, WAN MAC and certificate pin.
- Missing values stay Unknown. Bluetooth UPGRADING does not prove an active
  download, and WAN Up does not establish Internet access. LED colours are not
  used to infer mode or completion.
- Add an explicit connection/onboarding recheck and shortcuts to WOBA completion
  and Bluetooth inspection. Completion verification accepts a fresh normal-login
  OBASTATUS=0; if normal login omits the flag, it checks WOBA's explicit flag or
  LoginNotApplicable. A working normal login is always required.

## Visible DFS and Band steering controls

- Put Band steering and DFS channels first in the Wi-Fi section.
- Keep both feature cards visible when firmware reads fail or omit their flags,
  showing the actual read status / unavailable-value explanation instead of
  silently hiding the options or treating unknown flags as Off.
- Retain the existing reviewed settings editors when the router returns a
  recognized ENABLE=0/1 value; do not guess unsupported setter requests.

Latest validation: portable shared tests pass, including distinct status mappings,
unknown-state refusal, page-load failure/refresh behavior and all earlier checks.
The expanded Android callback, TLS and UI checks run in the release workflow.
