# Local mesh display

The parser consumes NETWORKMAP_ALL.HNC, AUTO_WOBA_SAT_STATUS_ALL and LINKSPEEDBH_ALL.
The HNC children hierarchy supplies reported parent relationships; interface/client
nodes are not router nodes. The satellite table supplies identities without
assuming topology. An active, uniquely MAC-correlated local AP backhaul association
supplies a direct local-AP/satellite link. Inactive or ambiguous rows are excluded.

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
