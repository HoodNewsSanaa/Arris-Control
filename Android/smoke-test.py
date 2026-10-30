"""Exercise the actual APK and Android Keystore on the CI emulator; no router or account."""
import importlib.util
import subprocess
import tempfile
import time
from pathlib import Path


def annotate(message):
    """Surface a failure as a GitHub annotation, readable from the API without downloading logs."""
    text = message.replace('%', '%25').replace('\r', '').replace('\n', '%0A')
    print('::error title=Android smoke test::' + text[:4000], flush=True)


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
        # Sessions 1 and 2 read identity, mode and one page (3 each); the legacy upgrade reads identity and mode (2).
        if fixture.logins != 4 or fixture.anonymous_reads < 1 or fixture.protected_reads < 10:
            annotate(f'Fixture counters: logins={fixture.logins} (expected 4), anonymous_reads={fixture.anonymous_reads}, protected_reads={fixture.protected_reads} (expected >= 10)')
            raise SystemExit('Fixture did not observe the expected anonymous scan and exactly four authenticated sessions (none from the impostor check)')
        print('PASS: APK UI, Android Keystore, anonymous API scan, actual HTTPS login/cookies, saved address update, reconnect, certificate-pin impostor refusal, legacy pin upgrade and pending offline-login verification without cloud.')
        adb('root')
        adb('wait-for-device')
        adb('pull', '/data/user/0/com.hoodnewssanaa.arriscontrol/cache/ui-preview.png', 'artifacts/Android-UI-preview.png')
        break
    logs = adb('logcat', '-d')
    app_crashed = any('AndroidRuntime' in line and 'Process: com.hoodnewssanaa.arriscontrol,' in line for line in logs.splitlines())
    if app_crashed or 'Operation result' in ui:
        Path('artifacts/android-smoke.log').write_text(logs)
        print('Fixture counters:', fixture.logins, fixture.anonymous_reads, fixture.protected_reads)
        errors = adb('logcat', '-d', '-s', 'ArrisControlSelfTest:E', 'AndroidRuntime:E')
        print(errors)
        annotate(f'App self-test failed. Fixture counters: logins={fixture.logins}, anonymous_reads={fixture.anonymous_reads}, protected_reads={fixture.protected_reads}\n' + errors[-3500:])
        raise SystemExit('Android offline smoke test failed; see android-smoke.log')
else:
    Path('artifacts/android-smoke.log').write_text(adb('logcat', '-d'))
    annotate('Android checks did not finish within 90 seconds.\n' + adb('logcat', '-d', '-s', 'ArrisControlSelfTest:E', 'AndroidRuntime:E', 'DOTNET:I')[-3500:])
    raise SystemExit('Android offline checks did not finish within 90 seconds')

# Exercise the actual Android archive parser/content provider, then update the
# installed APK in place and verify its existing Keystore-encrypted login.
probe = list(Path('update-probe').glob('*-Signed.apk'))
if len(probe) != 1:
    raise SystemExit('Expected one update probe APK')
adb('root')
adb('wait-for-device')
cache = '/data/user/0/com.hoodnewssanaa.arriscontrol/cache/updates'
adb('shell', 'mkdir', '-p', cache)
adb('push', str(probe[0]), cache + '/candidate.apk')
adb('push', 'update-probe/foreign.apk', cache + '/foreign.apk')
owner = adb('shell', 'stat', '-c', '%u', '/data/user/0/com.hoodnewssanaa.arriscontrol').strip()
if not owner.isdigit():
    raise SystemExit('Could not determine the test app UID')
adb('shell', 'chown', '-R', owner + ':' + owner, cache)
def app_update_check(flag):
    adb('logcat', '-c')
    adb('shell', 'am', 'force-stop', 'com.hoodnewssanaa.arriscontrol')
    adb('shell', 'am', 'start', '-W', '-n', component, '--ez', flag, 'true')
    for _ in range(20):
        time.sleep(1)
        adb('shell', 'uiautomator', 'dump', '/sdcard/arris-update.xml')
        ui = adb('shell', 'cat', '/sdcard/arris-update.xml')
        if 'PASS: Android update verification.' in ui:
            return
        if 'Operation result' in ui:
            break
    errors = adb('logcat', '-d', '-s', 'ArrisControlSelfTest:E', 'AndroidRuntime:E', 'DOTNET:I')
    annotate('Android updater check failed: ' + errors[-3500:])
    raise SystemExit('Android updater check failed')
app_update_check('updaterSelfTest')
adb('install', '-r', str(probe[0]))
app_update_check('updateRetentionSelfTest')
print('PASS: APK update signer/version checks, foreign signing-key refusal, private installer URI and encrypted-login retention after an in-place Android upgrade.')
