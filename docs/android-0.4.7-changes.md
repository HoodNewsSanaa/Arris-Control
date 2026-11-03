# Android 0.4.7 changes

- Overview provides **Name this network**, using the same saved connection label
  as Your routers. Holding the network's Overview name opens the same editor.
  Saving updates both that label and the selected-router heading immediately.
- Long-press a router in the Mesh diagram or its node-list button to name that
  specific node. A short tap still selects it. Movement, scrolling cancellation,
  and a second touch cancel the hold; releasing after a handled long press does
  not also perform the short-tap action. The explicit naming button remains.
- Existing identity-based name storage is unchanged. Naming causes no router
  settings write, management read or discovery scan.

The release workflow compiles the Android UI and checks launch, local HTTPS
management, signing and saved-login retention. The new touch gestures still need
checking on a physical phone. W31 normal management and local labels are not
model-gated, but W31 hardware has not been validated; WOBA admin credential setup
and onboarding-completion writes retain the verified W30 gate.
