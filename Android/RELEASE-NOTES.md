Android 0.3.4 adds a firmware lookup recorder under Diagnostics → Software access diagnostics.

The optional experiment temporarily sets only the update source URL to a recorder on this phone. It waits up to 90 seconds for a natural lookup, returns an empty HTTP 503 response, then restores and reads back the original ARRIS source. It sends no check, download, install or reported-version command. Whether this firmware accepts a URL-only change or looks up during that interval is unverified.

An encrypted recovery record is saved before the first write. If the app closes or restoration cannot be verified, the diagnostics screen offers Restore ARRIS firmware lookup. Captured query values and headers stay encrypted locally and are excluded from shared diagnostics; exporting the full private request requires review. Existing saved logins and the permanent updater signer are retained.
