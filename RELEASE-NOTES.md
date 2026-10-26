Automatically locates the selected router when its saved IP stops matching or
responding, including address changes after enabling bypass. Checks the saved
address first, then performs bounded local Wi-Fi/Ethernet discovery on port 50011.
Only a unique matching certificate is eligible for login; authenticated exact
serial and protected mode reads are required before selecting the new address.

Retains the updated address in a new encrypted credential profile, preserves old
versions, and updates the router selector/status. No cloud requests or router
settings writes are used for discovery. Interrupted settings writes are not retried.

Offline checks cover saved-address selection, moved addresses, subnet bounds,
public-address exclusion, ambiguous/missing matches, cancellation, and encrypted
payload preservation. Existing protocol, credential, settings, WPF and updater
checks remain. No live router settings were changed for this release. Native
live automatic reconnection and bridge-to-router recovery still need validation.