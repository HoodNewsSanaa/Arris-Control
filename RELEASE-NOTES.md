Enables the Bypass ON/OFF controls on Overview and Internet/DNS and adds bridge
mode to the routing selector. Enabling requires an explicit warning confirmation,
with No selected by default. The app checks the target serial and current setting
before sending one update, and does not automatically retry interrupted saves.

Enabling bypass may make management unreachable. Turning it off requires a
reachable authenticated API; bridge-mode access and recovery remain unverified.
A physical reset may be needed. No live router mode changes were made for testing.

Offline tests cover simulated bridge enable/disable and read-back, unknown-mode
rejection, and ON/OFF availability in router, bridge, and unknown states. Existing
credential acquisition, encrypted history, Full settings, and updater are retained.