Native Windows settings manager with a separate Full settings browser, local
DPAPI credential import, asynchronous reads, reviewed Save operations with
read-back verification, and a built-in GitHub updater.

Includes controls for DNS, Wi-Fi, guest networks, DHCP, UPnP, reservations,
forwarding rules, priority/QoS, parental-control enable state, routing/IPv6,
and status lights where supported. Enabling bridge mode remains blocked.

The release is one self-contained Windows x64 executable. Native live router
writes and a full two-version update cycle remain unverified; offline tests
cover protocol/payload mapping, stale saves, single-attempt writes, privacy,
UI construction, checksum/version policy, and atomic replacement/backup.
This initial release does not establish complete app parity or bridge recovery.
