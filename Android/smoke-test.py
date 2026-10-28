"""Exercise the actual APK and Android Keystore on the CI emulator; no router or account."""
import subprocess
import time
from pathlib import Path


def adb(*args):
    return subprocess.check_output(['adb', *args], text=True, stderr=subprocess.STDOUT)


adb('install', '-r', 'artifacts/Arris-Control-android.apk')
adb('logcat', '-c')
component = adb('shell', 'cmd', 'package', 'resolve-activity', '--brief', 'com.hoodnewssanaa.arriscontrol').strip().splitlines()[-1]
adb('shell', 'am', 'start', '-W', '-n', component, '--ez', 'offlineSelfTest', 'true')
for _ in range(30):
    time.sleep(2)
    # FLAG_SECURE disables screenshots but allows accessibility hierarchy verification.
    adb('shell', 'uiautomator', 'dump', '/sdcard/arris-ui.xml')
    ui = adb('shell', 'cat', '/sdcard/arris-ui.xml')
    if 'PASS: offline protocol and Android Keystore checks.' in ui:
        print('PASS: APK launched, connection UI rendered, shared fixtures and real Android Keystore passed.')
        break
    logs = adb('logcat', '-d')
    if 'FATAL EXCEPTION' in logs or 'FATAL UNHANDLED EXCEPTION' in logs or 'Operation result' in ui:
        Path('artifacts/android-smoke.log').write_text(logs)
        raise SystemExit('Android offline smoke test failed; see android-smoke.log')
else:
    Path('artifacts/android-smoke.log').write_text(adb('logcat', '-d'))
    raise SystemExit('Android offline checks did not finish within 60 seconds')
