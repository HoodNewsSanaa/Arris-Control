# W30 software-access investigation

The user confirmed first-time Bluetooth setup, Wi-Fi creation and protected local
HTTPS login on W30 / WR11AXER1 / AXR.0814.240522.10 with WAN disconnected. This is
one device/firmware result. Preserve that working login.

The authenticated API returns LOGINCFG_ALL with WEBUIENABLE=0, while
DEVICESETTINGSFEATURE_ALL advertises WEBGUI_ENABLE=false. These are distinct:
one is a configuration flag and the other is a support advertisement.
REMOTE_CONFIG_ALL did not return a usable JSON response on this unit.

The official Android app's MAX login-settings request model supports LOGINCFG
with an OPERTYPE and a nullable WEBUIENABLE string. Its API sends the request
through /setData. Our optional experiment sends only the one field and MODIFY;
it never copies the surrounding account/password fields.

## Optional browser-interface test

In Android, open Diagnostics → Software access diagnostics. If a supported
WEBUIENABLE value was read, a separate review button offers the opposite value.
Preparing the review does not write. After explicit confirmation, the app:

1. Checks the selected serial, model, hardware and inspected firmware.
2. Requires startup/software-update flags to be inactive.
3. Re-reads LOGINCFG and refuses a changed baseline.
4. Sends one request containing only WEBUIENABLE.
5. Reads back the flag and checks whether other login fields changed.

It has no automatic retry, password fallback, service scan, firmware upload or
SSH control. The generic settings editor still rejects LOGINCFG. Acknowledgement
alone is not success, and a changed flag does not prove browser access. If other
login fields change, stop the experiment and check the saved login.

The existing firmware may have no browser interface to enable. The test exposes
an observed setting without claiming that it creates a missing service.

## Mesh size

ARRIS documents two satellites plus one main W30/W31 router, three units total.
The official app uses left/right satellite slots. A display or onboarding limit
in the app does not establish the mesh firmware's capacity. No larger mesh has
been demonstrated here and the app does not claim to bypass that limit.

https://arris.my.salesforce-sites.com/consumers/articles/knowledge/mAX-W30-W31-Add-Additional-mAX-Satellite

## Alternative firmware

Software access diagnostics also offers Inspect firmware source. This reads
UPDATE_ALL and shows the configured HTTP(S) source locally. It may point to an
update lookup service rather than an image. Copying is separate from inspection;
addresses with parameters or credentials receive a review notice and are never
included in shared diagnostics. No download, update check or install is sent.

The published ARRIS profile uses BCM4908 and WR11AXER1a. The supplied ASUS
RT-AX88U firmware has related Broadcom boot files but an ASUS product target and
RAM-stage bootloader. Matching processors does not establish compatible board
parameters, NAND layout, radio calibration, signatures or recovery behavior.
No ASUS image was uploaded and no root shell has been obtained.
