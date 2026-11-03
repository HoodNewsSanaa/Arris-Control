# W30 firmware lookup experiment

This is an optional URL-only experiment, not a firmware installer or a proven firmware-download method. It is limited to W30 / WR11AXER1 / AXR.0814.240522.10, a verified original source of https://plt.surfboardconnect.com/manifest and inactive boot/update flags. The initial update state must have zero progress, Unknown/Idle status and no different offered version.

Connect Android to the ARRIS primary Wi-Fi. Update the app in place. Open Diagnostics → Software access diagnostics → Record firmware lookup and review the explanation. Keep the app visible for up to 90 seconds. It starts a listener bound to the phone's LAN IPv4 on port 8765, limited to the selected router's source IP and a random request path. Guest/client isolation can prevent the connection.

Only MODIFY UPDATE {CONTAINER: Update, URL: ...} is sent. This shape is based on the official app's Update model, with CHECKNOW deliberately omitted. Acceptance or side effects of a partial setter are not established on real hardware. No fallback to CheckNowDownloadInstall is provided. No version is changed on the router. No reboot/reset is requested.

The recorder persists at most one matching GET/HEAD request, headers bounded to 16 KiB, and replies with HTTP 503, an empty body and no redirect. It serves no firmware, filesystem paths or valid manifest. It does not relay requests to ARRIS. It may record nothing if HTTP is rejected or no natural check occurs. A failure does not establish that the server cannot provide firmware.

Before writing, save an encrypted recovery record. Finally cancel the receiver, recheck identity/inactive boot+update flags, restore the source only if it is still the recorded temporary URL, and verify read-back. An uncertain write is not resent. If restoration fails or the process is killed, retain the record and expose a Restore action on next access to this router's diagnostics. A third-party URL change is not overwritten. Recovery may need retrying after reconnection or after an active update finishes. Do not reset the router to clear the journal.

The full request may contain serial/MAC identifiers or authentication material. Capture and recovery records use the existing Android Keystore protection, remain app-private and never enter diagnostic exports. Review recorded lookup offers a separately confirmed private clipboard export. Never commit real captures, credentials, vendor app files or firmware to this repository.

The next investigation step, if a request is recorded, is to examine its parameter names and authentication locally, reconstruct a request to the original vendor endpoint and change only a clearly identified reported-version parameter on the laptop. Do not change versions on the router or serve an offered image to it. Downloading an image for analysis and flashing it are separate actions.
