Pins the router's TLS public key (0.4.0).

The router certificate's common name is its MAC, which any device on the network
can copy into its own certificate. Earlier versions accepted any certificate with
that name, so an impostor answering during automatic discovery could receive the
router login. The app now records the certificate's public-key hash at the first
successful login, stores it inside the DPAPI-encrypted credential, and requires it
for every connection and discovery probe. A mismatching certificate fails before
any credential is sent.

Credentials saved by 0.3.0 and earlier connect only at their saved address, never
by scanning, and are upgraded to a new pinned version after one successful login.
The original record is kept. Offline checks cover copied-MAC impostor certificates,
other MACs, expired and malformed pins, encrypted pin round-trips, the legacy
saved-address limit and the pin upgrade.
