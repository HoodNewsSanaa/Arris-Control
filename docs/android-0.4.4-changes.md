# Android 0.4.4: local completion and satellite verification

The owner authorized implementing and building this batch on 2026-11-02.
Future batches still require confirmation before an APK build/release.

## Local onboarding completion

The normal login's `OBASTATUS` and `BLESTATUS` are retained with their read time.
`BLESTATUS` comes from a process-presence check in the inspected `Login.php`;
it does not indicate phone discoverability or radio state. The new local status
screen and the normal Overview show both flags independently of bridge mode.
Normal saved administration is never gated on either flag.

Static inspection of the recovered W30 `MobAppOperation.php` identified an exact
normal-admin operation: MODIFY with the scalar `TURNOFFBLE` string `1`.
The handler requires the onboarding process, asks it to complete setup, then stops
its Bluetooth services. A phone Bluetooth connection is not needed. The handler
does not check every child-process exit status, so its SUCCESS response cannot
prove completion. The app instead verifies a new normal login and explicit
`OBASTATUS=0` afterward.

Preparation and application require a pinned private endpoint, matching device
identity, W30 / WR11AXER1 / AXR.0814.240522.10, incomplete onboarding, service
presence, inactive startup/update flags, and unchanged identity/address/mode
after review. One plan permits one command, with no automatic retry after an
uncertain reply. Acknowledged-but-incomplete and unknown results stay unverified.
Unexpected post-command routing-mode changes are reported for review. A fresh
login also preserves an already observed certificate key if a legacy record's
pin upgrade could not be saved; it never silently falls back to unpinned trust.

When `BLESTATUS=0`, the known command cannot run; the app reports this directly
and keeps normal settings available. Earlier experiments found OFFWIFI could
acknowledge without changing the flag. An additional OFFCOBA handler maps to a
separate onboarding state, but its completion effect is not validated. Neither
handler is used as a speculative fallback. No live completion command was sent
during implementation. Physical completion validation remains outstanding.

## Satellite workflow

Controller administration stays on its normal pinned local session. Only the
new node is inspected over Bluetooth. Its serial/WAN MAC are retained in memory
for that controller/pin while the user opens discovery and refreshes join status.
Unreadable identities, including code 115, are shown without substitution.

An incomplete controller flag or stopped onboarding process does not prevent
using the existing local discovery controls. This does not establish that every
firmware will finish pairing in that state. Discovery requests retain fresh-value
checks, a preview and one write with read-back. No new satellite role, backhaul
credential, cloud configuration or provisioning request is guessed.

Join verification requires a fresh network map rooted at the selected controller,
a correlated router/satellite node and a matching active backhaul association.
Client nodes, discovery-only entries, conflicting identities, stale snapshots and
unavailable reads cannot produce success. Wired links without active evidence
remain unverified. Full offline provisioning still needs additional protocol and
hardware validation.

Shared tests cover command allowlists, eligibility/state changes, lost replies,
false-success refusal, topology/association correlation and identity conflicts.
The Android fixture exercises three fresh normal logins around one completion
request, checks OBASTATUS=0/BLESTATUS=0 afterward, and verifies that normal
bridge-mode administration and the encrypted credential pin remain usable.
