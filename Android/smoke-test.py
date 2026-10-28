"""Exercise the actual APK and Android Keystore on the CI emulator; no router or account."""
import importlib.util
import subprocess
import tempfile
import time
from pathlib import Path


def adb(*args):
    return subprocess.check_output(['adb', *args], text=True, stderr=subprocess.STDOUT)


temporary = tempfile.TemporaryDirectory()
certificate = str(Path(temporary.name) / 'fixture.crt')
private_key = str(Path(temporary.name) / 'fixture.key')
subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
    '-subj', '/O=ARRIS CI Fixture/CN=001122334455', '-keyout', private_key, '-out', certificate],
    check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
spec = importlib.util.spec_from_file_location('router_fixture', 'Android/mock-router.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
fixture = module.RouterFixture(certificate, private_key)

adb('install', '-r', 'artifacts/Arris-Control-android.apk')
adb('logcat', '-c')
component = adb('shell', 'cmd', 'package', 'resolve-activity', '--brief', 'com.hoodnewssanaa.arriscontrol').strip().splitlines()[-1]
adb('shell', 'am', 'start', '-W', '-n', component, '--ez', 'offlineSelfTest', 'true', '--ez', 'networkSelfTest', 'true')
for _ in range(45):
    time.sleep(2)
    # FLAG_SECURE disables screenshots but allows accessibility hierarchy verification.
    adb('shell', 'uiautomator', 'dump', '/sdcard/arris-ui.xml')
    ui = adb('shell', 'cat', '/sdcard/arris-ui.xml')
    if 'PASS: Android API scan, HTTPS login and reconnect.' in ui:
        if fixture.logins != 2 or fixture.anonymous_reads < 1 or fixture.protected_reads < 6:
            raise SystemExit('Fixture did not observe the expected anonymous scan and two authenticated sessions')
        print('PASS: APK UI, Android Keystore, anonymous API scan, actual HTTPS login/cookies, saved address update and reconnect.')
        adb('root')
        adb('wait-for-device')
        adb('pull', '/data/user/0/com.hoodnewssanaa.arriscontrol/cache/ui-preview.png', 'artifacts/Android-UI-preview.png')
        break
    logs = adb('logcat', '-d')
    if 'FATAL EXCEPTION' in logs or 'FATAL UNHANDLED EXCEPTION' in logs or 'Operation result' in ui:
        Path('artifacts/android-smoke.log').write_text(logs)
        print('Fixture counters:', fixture.logins, fixture.anonymous_reads, fixture.protected_reads)
        print(adb('logcat', '-d', '-s', 'ArrisControlSelfTest:E', 'AndroidRuntime:E', 'DOTNET:I'))
        raise SystemExit('Android offline smoke test failed; see android-smoke.log')
else:
    Path('artifacts/android-smoke.log').write_text(adb('logcat', '-d'))
    raise SystemExit('Android offline checks did not finish within 90 seconds')
