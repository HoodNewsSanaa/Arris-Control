# Android 0.4.6 changes

This release combines the fixes and naming controls requested after 0.4.5.

- Backhaul rate matching recognizes the stock HNC keys `WANMACAddr`, `MACAddr`
  and `IPAddr`, and matches a node's reported device MAC alongside its WAN MAC.
  A regression reproduces the reported 2.88/2.59 Gbps rates without requiring an
  AUTO_WOBA discovery entry. Unrelated clients and ambiguous matches stay excluded.
- Mesh nodes can be named locally. Names follow stable node identities, update
  diagram/list/link descriptions immediately, and can be reset to reported names.
- Saved connections under Your routers can be named separately, making networks
  distinguishable. Connection labels follow serial/MAC identity rather than IP
  address or credential-file path. They never change authentication or router settings.
- DFS and band-steering controls recognize both boolean and 0/1 responses.
  Their writes use the firmware's `true`/`false` strings. In particular, the stock
  DFS setter maps `true` to radio value 2; sending `1` previously missed that
  conversion. Read-back and stale-state checks compare equivalent boolean forms
  while still rejecting changed or missing states. Unchanged toggles produce no write.

Shared protocol/settings tests cover these behaviors. The release workflow also
compiles the Android UI and checks the installed APK, signing and saved-login
retention on an emulator. Live-router display and DFS behavior still need checking
after installation.
