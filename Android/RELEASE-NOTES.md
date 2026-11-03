Android 0.4.6 fixes backhaul-rate matching and adds names for mesh nodes and saved networks.

The mesh parser now recognizes the stock HNC fields WANMACAddr, MACAddr and IPAddr. It matches a router's reported device MAC alongside its WAN MAC, so existing satellites can show their reported link rates without depending on the AUTO_WOBA discovery list. Tests reproduce the reported 2.88 Gbps down / 2.59 Gbps up association and exclude unrelated clients or ambiguous matches. These are PHY link rates, not measured throughput.

Select a node in the mesh diagram or list, then choose **Name selected node**. Names update the diagram, list and connection descriptions immediately and remain saved on this phone across reconnects and address changes. **Use reported name** removes that local override.

Under **Your routers**, choose **Name this router / network** to label connections such as Home or Parents. The name appears on the connection button and selected-router header. Saved-network names are separate from physical mesh-node names and follow router identity across IP changes and refreshed credential files. Naming does not change Wi-Fi settings or login credentials.

DFS and band-steering controls now recognize true/false as well as 0/1 responses. Their writes use the firmware's expected true/false strings; the DFS handler converts true to its native radio value 2. Read-back checks compare equivalent boolean forms while rejecting missing or unchanged states. Unknown values remain unavailable, and unchanged toggles do not send a write.

The existing local login, saved-router matching and WOBA management flows remain available. This update uses the existing package and permanent signing key to preserve saved logins when installed over the previous release. Release checks include shared regressions, Android compilation, HTTPS/Keystore checks and update credential retention.
