# Local mesh display

The parser consumes NETWORKMAP_ALL.HNC, AUTO_WOBA_SAT_STATUS_ALL and LINKSPEEDBH_ALL.
The HNC children hierarchy supplies reported parent relationships; interface/client
nodes are not router nodes. The satellite table supplies identities without
assuming topology. An active, uniquely MAC-correlated local AP backhaul association
supplies a direct local-AP/satellite link. Inactive or ambiguous rows are excluded.

Native HNC fields `WANMACAddr`, `MACAddr` and `IPAddr` are recognized alongside
the older aliases. A router retains both its reported WAN and device MAC identities
for unique association matching. This lets an existing satellite display rates
without depending on its presence in the AUTO_WOBA discovery table. Client MACs
are never promoted to router aliases, and a single satellite is not matched to an
unrelated association just because it is the only node.

Select a router in the diagram or node list, then use **Name selected node** to
give it a local label. Labels are saved in Android's private app preferences,
keyed by stable serial/MAC identities rather than IP address or diagram position.
They apply to node buttons, the diagram, accessibility descriptions and link
details. **Use reported name** clears the local override for that node. Naming
does not send a router write, change Wi-Fi names, or change credentials.

Vendor PHP maps DOWNLINKRATE and UPLINKRATE to Device.WiFi.AccessPoint.18.
AssociatedDevice.{i}.LastDataDownlinkRate / LastDataUplinkRate. The standard defines
these in kbit/s, in AP-to-STA and STA-to-AP directions respectively:
[TR-181 LastDataDownlinkRate](https://cwmp-data-models.broadband-forum.org/tr-181-2-16-0-cwmp-diffs.html).
Proprietary TXRATE/RXRATE units are not inferred. A wired topology edge without a
reported rate stays unknown, rather than assuming a gigabit negotiated speed.

The triangle is a layout, not a fabricated full mesh. Solid lines appear only for reported
edges. A dotted third side explicitly labeled Not reported is a layout guide,
not a claimed backhaul connection. No physical distance is encoded. Three routers form a triangle; two stack;
larger graphs use a ring. Eight nodes are drawn at most and all known nodes are
available through accessible selection controls. All labels come from one timestamped
read snapshot. A manual refresh replaces it; missing responses never reuse old rates.

Node setup exposes Bluetooth inspection, normal authenticated AUTO_WOBA controls,
and refresh. It never copies cloud metadata or asserts that enabling discovery has
paired a node. Full offline pairing is pending hardware validation.
