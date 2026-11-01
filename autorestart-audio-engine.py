"""Voicemeeter output reconnect helper (Windows, Python 3.10+).

Observes selected hardware outputs and restarts the engine on confirmed
Windows Core Audio endpoint transitions from inactive to active.
Does NOT alter device bindings, audio routing, volume, or Windows defaults.
"""
from __future__ import annotations

import argparse
import ctypes
import json
import logging
import os
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

ROOT = Path(__file__).resolve().parent
CONFIG_PATH = ROOT / "config.json"
DEFAULTS = {
    "poll_seconds": 2,
    "settle_seconds": 7,
    "cooldown_seconds": 15,
    "auto_monitor_outputs": True,
    "devices": [],
}


def load_config():
    config = json.loads(CONFIG_PATH.read_text(encoding="utf-8")) if CONFIG_PATH.exists() else DEFAULTS
    for field in ("poll_seconds", "settle_seconds", "cooldown_seconds"):
        if not isinstance(config[field], (int, float)) or config[field] <= 0:
            raise ValueError(f"Invalid {field}; must be > 0")
    if not isinstance(config.get("auto_monitor_outputs", False), bool):
        raise ValueError("auto_monitor_outputs must be true or false")
    for d in config.get("devices", []):
        if not d.get("match") or not isinstance(d.get("bus"), int) or d["bus"] not in range(5):
            raise ValueError(f"Invalid device specification: {d}")
    return config


def norm(s):
    return str(s or "").casefold().strip()


@dataclass
class DeviceStatus:
    active: bool
    present: bool
    ambiguous: bool = False
    detail: str = ""


def classify_endpoints(devices, match, exact=False):
    """Conservative: never restart when multiple same-named endpoints disagree.

    Windows render endpoint list is authoritative for ACTIVE vs UNPLUGGED;
    absence counts as inactive. Ambiguous matching is treated as unknown.
    """
    entries = [d for d in devices if (norm(match) == norm(d["name"]) if exact
                                     else norm(match) in norm(d["name"]))]
    if not entries:
        if any(d.get("unreadable") for d in devices):
            return DeviceStatus(False, False, True, "endpoint information incomplete")
        return DeviceStatus(False, False, detail="not listed by Windows")
    if any(d.get("unreadable") for d in entries):
        return DeviceStatus(False, True, True, "matching endpoint unreadable")
    states = {bool(d["state"] & 1) for d in entries}
    if len(states) > 1:
        return DeviceStatus(False, True, True, "multiple matching endpoints with conflicting states")
    return DeviceStatus(True in states, True, detail=", ".join(f"{d['name']} (state={d['state']})" for d in entries))


def audio_state_bits(state):
    """Convert pycaw AudioDeviceState enum/flag or integer to Windows bit mask.

    Some pycaw releases return an Enum rather than an int, so int(state)
    raises TypeError even when the device state is perfectly valid.
    """
    value = getattr(state, "value", state)
    return int(value)


class CoreAudio:
    def __init__(self):
        # Windows-only dependencies, installed via requirements.txt.
        import comtypes  # type: ignore
        from pycaw.pycaw import AudioUtilities  # type: ignore
        from pycaw.constants import DEVICE_STATE, EDataFlow  # type: ignore
        from pycaw.api.mmdeviceapi import PROPERTYKEY  # type: ignore
        self.comtypes = comtypes
        self.audio_utilities = AudioUtilities
        self.mask = DEVICE_STATE.MASK_ALL.value
        self.flow = EDataFlow.eRender.value
        self.name_key = PROPERTYKEY()
        self.name_key.fmtid = comtypes.GUID("{a45c254e-df1c-4efd-8020-67d146a850e0}")
        self.name_key.pid = 14
        comtypes.CoInitialize()

    def list_output_endpoints(self):
        # Read only state, ID and friendly name, rather than every COM property.
        collection = self.audio_utilities.GetDeviceEnumerator().EnumAudioEndpoints(self.flow, self.mask)
        entries = []
        for i in range(collection.GetCount()):
            entry = {"name": "", "state": 0, "id": "", "unreadable": False}
            try:
                device = collection.Item(i)
                entry["id"] = device.GetId()
                entry["state"] = audio_state_bits(device.GetState())
                store = device.OpenPropertyStore(0)
                value = store.GetValue(ctypes.byref(self.name_key))
                try:
                    entry["name"] = value.GetValue() or ""
                finally:
                    value.clear()
                if not entry["name"]:
                    entry["unreadable"] = True
            except Exception:
                entry["unreadable"] = True
            entries.append(entry)
        return entries

    def close(self):
        self.comtypes.CoUninitialize()


class Voicemeeter:
    def __init__(self):
        dll = ROOT / "VoicemeeterRemote64.dll"
        if not dll.exists():
            raise FileNotFoundError(f"Missing {dll}. Copy from the official Voicemeeter installation.")
        self.api = ctypes.WinDLL(str(dll))
        self.api.VBVMR_Login.restype = ctypes.c_long
        self.api.VBVMR_Logout.restype = ctypes.c_long
        self.api.VBVMR_IsParametersDirty.restype = ctypes.c_long
        self.api.VBVMR_GetVoicemeeterType.argtypes = [ctypes.POINTER(ctypes.c_long)]
        self.api.VBVMR_GetVoicemeeterType.restype = ctypes.c_long
        self.api.VBVMR_GetParameterStringA.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
        self.api.VBVMR_GetParameterStringA.restype = ctypes.c_long
        self.api.VBVMR_SetParameterFloat.argtypes = [ctypes.c_char_p, ctypes.c_float]
        self.api.VBVMR_SetParameterFloat.restype = ctypes.c_long
        rc = self.api.VBVMR_Login()
        if rc != 0:
            raise RuntimeError(f"Voicemeeter login failed: {rc}; start Potato before running")
        variant = ctypes.c_long()
        rc = self.api.VBVMR_GetVoicemeeterType(ctypes.byref(variant))
        if rc != 0 or variant.value not in (1, 2, 3):
            self.api.VBVMR_Logout()
            raise RuntimeError(f"Unsupported Voicemeeter type {variant.value}, rc={rc}")
        self.output_count = {1: 1, 2: 3, 3: 5}[variant.value]
        logging.info("Connected to Voicemeeter type %d (%d hardware outputs)", variant.value, self.output_count)

    def selected_output(self, bus):
        # Refresh the Remote API's local parameter cache before reading it.
        # Login succeeds before the initial parameter snapshot is available.
        rc = self.api.VBVMR_IsParametersDirty()
        if rc < 0:
            raise RuntimeError(f"Failed to synchronize Voicemeeter parameters: {rc}")
        buffer = ctypes.create_string_buffer(512)
        rc = self.api.VBVMR_GetParameterStringA(f"Bus[{bus}].device.name".encode("ascii"), buffer)
        if rc != 0:
            raise RuntimeError(f"Failed to read A{bus+1} device binding: {rc}")
        return buffer.value.decode("mbcs", errors="replace")

    def restart(self):
        rc = self.api.VBVMR_SetParameterFloat(b"Command.Restart", ctypes.c_float(1))
        if rc != 0:
            raise RuntimeError(f"Voicemeeter restart command returned {rc}")
        logging.warning("Sent Command.Restart = 1 (audio will briefly interrupt)")

    def close(self):
        self.api.VBVMR_Logout()


class ReconnectMonitor:
    def __init__(self, config, restart: Callable[[], None]):
        self.config = config
        self.restart = restart
        self.current = {}
        self.pending = {}
        self.last_restart = float('-inf')
        self.started = False
        self.identities = {}

    def update(self, endpoints, bindings, now):
        """State machine; no restart on first scan, disconnection, or ambiguity."""
        statuses = {}
        definitions = self.config.get("devices", [])
        if self.config.get("auto_monitor_outputs", False):
            definitions = [{"label": f"A{bus+1}", "match": name, "bus": bus, "exact": True}
                           for bus, name in bindings.items() if norm(name)]
        labels = {d["label"] for d in definitions}
        for label in set(self.current) - labels:
            self.current.pop(label, None)
            self.pending.pop(label, None)
            self.identities.pop(label, None)
        for d in definitions:
            label, match, bus = d["label"], d["match"], d["bus"]
            binding = bindings.get(bus, "")
            identity = (bus, norm(binding), norm(match))
            if self.identities.get(label) != identity:
                # Changing the selected device creates a fresh baseline.
                self.current.pop(label, None)
                self.pending.pop(label, None)
                self.identities[label] = identity
            if norm(match) not in norm(binding):
                statuses[label] = None
                self.pending.pop(label, None)
                continue
            statuses[label] = classify_endpoints(endpoints, match, d.get("exact", False))
        self.statuses = statuses

        if not self.started:
            for label, status in statuses.items():
                if status is not None and not status.ambiguous:
                    self.current[label] = status.active
            self.started = True
            logging.info("Initial states: %s (no restart on startup)", self._format(statuses))
            return

        for label, status in statuses.items():
            if status is None:
                if label in self.current:
                    logging.info("%s: no longer selected on configured bus; ignoring", label)
                self.current.pop(label, None)
                continue
            if status.ambiguous:
                logging.warning("%s: ambiguous endpoint state; skip: %s", label, status.detail)
                self.pending.pop(label, None)
                self.current.pop(label, None)
                continue
            previous = self.current.get(label)
            active = status.active
            if previous is None:
                self.current[label] = active
                logging.info("%s: tracking %s", label, "ACTIVE" if active else "OFFLINE")
            elif previous != active:
                self.current[label] = active
                if not active:
                    self.pending.pop(label, None)
                    logging.info("%s: OFFLINE; do not restart", label)
                else:
                    self.pending[label] = now
                    logging.info("%s: ACTIVE; waiting %.1fs before restart", label, self.config["settle_seconds"])

        ready = [label for label, first_seen in self.pending.items()
                 if self.current.get(label) and now - first_seen >= self.config["settle_seconds"]]
        if not ready:
            return
        if now - self.last_restart < self.config["cooldown_seconds"]:
            return
        # Only one restart for all ready devices. Do not retry in a loop.
        logging.warning("Recovered output(s): %s", ", ".join(ready))
        # Consume the attempt even if the API raises: no automatic retry loop.
        self.last_restart = now
        for label in ready:
            self.pending.pop(label, None)
        self.restart()

    @staticmethod
    def _format(statuses):
        return {k: "NOT SELECTED" if s is None else "UNKNOWN" if s.ambiguous else "ACTIVE" if s.active else "OFFLINE" for k, s in statuses.items()}


def run(args):
    if os.name != "nt":
        raise RuntimeError("This utility requires Windows")
    config = load_config()
    core = CoreAudio()
    vm = None
    try:
        vm = Voicemeeter()
        monitor = ReconnectMonitor(config, lambda: vm.restart())
        last_report = 0
        while True:
            try:
                endpoints = core.list_output_endpoints()
                buses = range(vm.output_count) if config.get("auto_monitor_outputs", False) else {d["bus"] for d in config["devices"]}
                bindings = {bus: vm.selected_output(bus) for bus in buses}
                now = time.monotonic()
                monitor.update(endpoints, bindings, now)
                if args.diagnostic and now - last_report >= 10:
                    last_report = now
                    logging.info("States: %s; bindings: %s", monitor._format(monitor.statuses), bindings)
                    for label, status in monitor.statuses.items():
                        if status is not None:
                            logging.info("  %s: %s", label, status.detail)
            except Exception:
                # A failed scan cannot count toward continuous readiness.
                monitor.pending.clear()
                monitor.current.clear()
                logging.exception("Check failed; will retry without changing audio routing")
            time.sleep(config["poll_seconds"])
    finally:
        if vm is not None:
            vm.close()
        core.close()


def self_test():
    cfg = {"devices": [{"label": "JBL", "match": "JBL", "bus": 1}], "settle_seconds": 7, "cooldown_seconds": 15}
    calls = []
    monitor = ReconnectMonitor(cfg, lambda: calls.append(True))
    online = [{"name": "Headphones (JBL)", "state": 1}]
    offline = [{"name": "Headphones (JBL)", "state": 8}]
    bindings = {1: "JBL"}
    monitor.update(online, bindings, 0)           # no restart at startup
    monitor.update(offline, bindings, 2)          # disconnect no restart
    monitor.update(offline, bindings, 20)
    monitor.update(online, bindings, 22)          # reconnect
    monitor.update(online, bindings, 28)          # too early
    assert not calls
    monitor.update(online, bindings, 30)          # stable, restart once
    monitor.update(online, bindings, 50)          # no repeat
    assert len(calls) == 1, calls
    # pycaw returns an AudioDeviceState enum in recent versions; it is
    # not directly convertible with int(enum) unless it subclasses IntEnum.
    from enum import Enum
    class FakeAudioDeviceState(Enum):
        ACTIVE = 1
        UNPLUGGED = 8
    assert audio_state_bits(FakeAudioDeviceState.ACTIVE) == 1
    assert audio_state_bits(FakeAudioDeviceState.UNPLUGGED) == 8
    assert audio_state_bits(1) == 1
    print("SELF-TEST PASS: startup/disconnect ignored, reconnect delayed, no repeated restarts")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--diagnostic", action="store_true", help="show Core Audio endpoints and bindings every 10 seconds")
    parser.add_argument("--self-test", action="store_true", help="run deterministic logic tests without Windows or DLL")
    opts = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", datefmt="%H:%M:%S", handlers=[logging.StreamHandler(sys.stdout)])
    if opts.self_test:
        self_test()
    else:
        try:
            run(opts)
        except KeyboardInterrupt:
            print("\nStopped by user")
        except Exception as e:
            logging.error("Cannot start: %s", e)
            sys.exit(1)
