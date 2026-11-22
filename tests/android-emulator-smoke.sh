#!/usr/bin/env bash
set -euo pipefail

APK_DIR="${RUNNER_TEMP:-/tmp}/uabea-apk"
APK="$(find "$APK_DIR" -type f -name '*.apk' -print -quit)"
if [[ -z "$APK" ]]; then
  echo "::error::Android host APK artifact was not downloaded."
  exit 1
fi

adb wait-for-device
adb install -r "$APK"
adb logcat -c
adb shell monkey -p com.g5j.uabeahost 1 >/dev/null

dump_ui() {
  adb shell uiautomator dump /sdcard/uabea-window.xml >/dev/null 2>&1 || true
  adb shell cat /sdcard/uabea-window.xml 2>/dev/null || true
}

echo "Waiting for the app to finish installing and validating Linux..."
installed=0
for attempt in $(seq 1 300); do
  UI="$(dump_ui)"
  if grep -q "Linux runtime installed and verified" <<<"$UI"; then
    installed=1
    break
  fi
  if adb logcat -d -t 200 | grep -q "FATAL EXCEPTION"; then
    adb logcat -d -t 1000
    echo "::error::Android host crashed during Linux installation."
    exit 1
  fi
  sleep 2
done
if [[ "$installed" != 1 ]]; then
  adb logcat -d -t 1500
  echo "::error::Linux rootfs and original UABEA payload did not finish installing in the Android app."
  exit 1
fi

run_app_shell() {
  adb shell run-as com.g5j.uabeahost sh -s
}

echo "Checking that the app extracted all critical Linux and UABEA files..."
run_app_shell <<'APP_SHELL'
set -e
test -s files/runtime/rootfs/opt/dotnet/dotnet
test -x files/runtime/rootfs/usr/bin/Xvfb
test -x files/runtime/rootfs/usr/bin/x11vnc
test -s files/runtime/original/UABEAvalonia.dll
test -s files/runtime/original/UABEAvalonia.deps.json
test -s files/runtime/rootfs/.install-complete
test -s files/runtime/original/.install-complete
test -s files/runtime/logs/runtime.log
test -d files/linux-home
APP_SHELL

echo "Locating and pressing the Start button through Android UIAutomator..."
UI="$(dump_ui)"
python3 - "$UI" <<'PY'
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

raw = sys.argv[1]
match = re.search(r'(<hierarchy[\s\S]*</hierarchy>)', raw)
if not match:
    raise SystemExit("Could not read Android UI hierarchy.")
root = ET.fromstring(match.group(1))
for node in root.iter("node"):
    if node.attrib.get("text") == "Start":
        bounds = node.attrib.get("bounds", "")
        values = list(map(int, re.findall(r"-?\d+", bounds)))
        if len(values) != 4:
            raise SystemExit("Start button has invalid screen bounds: " + bounds)
        x = (values[0] + values[2]) // 2
        y = (values[1] + values[3]) // 2
        subprocess.check_call(["adb", "shell", "input", "tap", str(x), str(y)])
        print(f"Tapped Start at {x},{y}")
        break
else:
    raise SystemExit("Start button was not found in the Android activity.")
PY

echo "Waiting for PRoot, Box64, Xvfb, VNC and the original UABEA process..."
started=0
for attempt in $(seq 1 240); do
  UI="$(dump_ui)"
  if grep -q "UABEA is running" <<<"$UI"; then
    started=1
    break
  fi
  if grep -q "Runtime start failed" <<<"$UI"; then
    echo "$UI"
    adb logcat -d -t 1500
    run_app_shell <<'APP_SHELL' || true
for f in files/runtime/logs/*.log; do echo "==== $f"; cat "$f"; done
APP_SHELL || true
    echo "::error::Android guest runtime failed to start."
    exit 1
  fi
  sleep 2
done
if [[ "$started" != 1 ]]; then
  dump_ui
  adb logcat -d -t 1500
  run_app_shell <<'APP_SHELL' || true
for f in files/runtime/logs/*.log; do echo "==== $f"; cat "$f"; done
APP_SHELL || true
  echo "::error::Original UABEA did not reach its running state inside Android."
  exit 1
fi

echo "Running a real UABEA UnityFS bundle open/edit/save round-trip..."
UI="$(dump_ui)"
python3 - "$UI" <<'PY'
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

raw = sys.argv[1]
match = re.search(r'(<hierarchy[\s\S]*</hierarchy>)', raw)
if not match:
    raise SystemExit("Could not read Android UI hierarchy.")
root = ET.fromstring(match.group(1))
for node in root.iter("node"):
    if node.attrib.get("text") == "Bundle test":
        bounds = node.attrib.get("bounds", "")
        values = list(map(int, re.findall(r"-?\d+", bounds)))
        if len(values) != 4:
            raise SystemExit("Bundle test button has invalid bounds: " + bounds)
        x = (values[0] + values[2]) // 2
        y = (values[1] + values[3]) // 2
        subprocess.check_call(["adb", "shell", "input", "tap", str(x), str(y)])
        print(f"Tapped Bundle test at {x},{y}")
        break
else:
    raise SystemExit("Bundle test button was not found.")
PY

bundle_test=0
for attempt in $(seq 1 120); do
  UI="$(dump_ui)"
  if grep -q "UABEA bundle open/edit/save test passed" <<<"$UI"; then
    bundle_test=1
    break
  fi
  if grep -q "Bundle self-test failed" <<<"$UI"; then
    echo "$UI"
    run_app_shell <<'APP_SHELL' || true
for f in files/runtime/logs/UABEA-Bundle-*.log; do echo "==== $f"; cat "$f"; done
APP_SHELL || true
    echo "::error::UABEA could not edit, save and reopen the test UnityFS bundle."
    exit 1
  fi
  sleep 2
done
if [[ "$bundle_test" != 1 ]]; then
  dump_ui
  run_app_shell <<'APP_SHELL' || true
for f in files/runtime/logs/UABEA-Bundle-*.log; do echo "==== $f"; cat "$f"; done
APP_SHELL || true
  echo "::error::UABEA bundle edit/save round-trip did not finish."
  exit 1
fi

tap_ui_button() {
  local button="$1"
  local ui
  ui="$(dump_ui)"
  python3 - "$button" "$ui" <<'PY'
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

label, raw = sys.argv[1], sys.argv[2]
match = re.search(r'(<hierarchy[\s\S]*</hierarchy>)', raw)
if not match:
    raise SystemExit("Could not read Android UI hierarchy.")
root = ET.fromstring(match.group(1))
for node in root.iter("node"):
    if node.attrib.get("text") == label:
        bounds = node.attrib.get("bounds", "")
        values = list(map(int, re.findall(r"-?\d+", bounds)))
        if len(values) != 4:
            raise SystemExit(f"Invalid bounds for {label}: {bounds}")
        x = (values[0] + values[2]) // 2
        y = (values[1] + values[3]) // 2
        subprocess.check_call(["adb", "shell", "input", "tap", str(x), str(y)])
        print(f"Tapped {label} at {x},{y}")
        break
else:
    raise SystemExit(f"Button not found: {label}")
PY
}

echo "Checking clean stop and restart lifecycle..."
tap_ui_button "Stop"
stopped=0
for attempt in $(seq 1 30); do
  UI="$(dump_ui)"
  if grep -q "Runtime stopped" <<<"$UI"; then
    stopped=1
    break
  fi
  sleep 1
done
if [[ "$stopped" != 1 ]]; then
  dump_ui
  echo "::error::Android host did not stop the guest runtime cleanly."
  exit 1
fi

tap_ui_button "Start"
restarted=0
for attempt in $(seq 1 240); do
  UI="$(dump_ui)"
  if grep -q "UABEA is running" <<<"$UI"; then
    restarted=1
    break
  fi
  if grep -q "Runtime start failed" <<<"$UI"; then
    echo "$UI"
    run_app_shell <<'APP_SHELL' || true
for f in files/runtime/logs/*.log; do echo "==== $f"; cat "$f"; done
APP_SHELL || true
    echo "::error::UABEA failed to restart after Stop."
    exit 1
  fi
  sleep 2
done
if [[ "$restarted" != 1 ]]; then
  dump_ui
  echo "::error::UABEA did not restart after Stop."
  exit 1
fi

echo "Verifying the localhost-only VNC server and framebuffer handshake..."
adb forward tcp:15900 tcp:5900
python3 - <<'PY'
import socket
import struct

def recv_exact(sock, count):
    chunks = bytearray()
    while len(chunks) < count:
        part = sock.recv(count - len(chunks))
        if not part:
            raise RuntimeError("VNC connection closed during handshake")
        chunks.extend(part)
    return bytes(chunks)

with socket.create_connection(("127.0.0.1", 15900), timeout=20) as sock:
    sock.settimeout(20)
    version = recv_exact(sock, 12)
    if not version.startswith(b"RFB "):
        raise RuntimeError(f"Unexpected VNC banner: {version!r}")
    sock.sendall(version)
    count = recv_exact(sock, 1)[0]
    if count == 0:
        reason_len = struct.unpack(">I", recv_exact(sock, 4))[0]
        raise RuntimeError(recv_exact(sock, reason_len).decode("utf-8", "replace"))
    security = recv_exact(sock, count)
    if 1 not in security:
        raise RuntimeError(f"VNC server did not offer local no-auth security: {security!r}")
    sock.sendall(b"\x01")
    result = struct.unpack(">I", recv_exact(sock, 4))[0]
    if result != 0:
        raise RuntimeError(f"VNC security negotiation failed: {result}")
    sock.sendall(b"\x01")
    init = recv_exact(sock, 24)
    width, height = struct.unpack(">HH", init[:4])
    name_len = struct.unpack(">I", init[20:24])[0]
    name = recv_exact(sock, name_len)
    if width < 640 or height < 400:
        raise RuntimeError(f"Unexpected Xvfb framebuffer size: {width}x{height}")
    sock.sendall(struct.pack(">BBHHHH", 3, 0, 0, 0, width, height))
    update = recv_exact(sock, 4)
    if update[0] != 0:
        raise RuntimeError(f"Expected framebuffer update, got message type {update[0]}")
    rectangles = struct.unpack(">H", update[2:4])[0]
    print(f"VNC OK: RFB {version[4:7].decode()}; {width}x{height}; desktop={name.decode('utf-8','replace')}; rectangles={rectangles}")
PY

echo "Android emulator integration smoke test passed."
