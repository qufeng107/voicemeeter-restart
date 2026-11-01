"""Deterministic monitoring tests; no audio hardware or dependencies required."""
import importlib.util
import sys
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("vm_helper", Path(__file__).with_name("autorestart-audio-engine.py"))
m = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = m
spec.loader.exec_module(m)


class MonitorTests(unittest.TestCase):
    def setUp(self):
        self.calls = []
        self.config = {"auto_monitor_outputs": True, "settle_seconds": 7, "cooldown_seconds": 15}
        self.monitor = m.ReconnectMonitor(self.config, lambda: self.calls.append(True))

    def scan(self, now, states, bindings=None):
        endpoints = [{"name": name, "state": state} for name, state in states.items()]
        self.monitor.update(endpoints, bindings if bindings is not None else {0: "Speaker"}, now)

    def test_offline_start_and_delayed_reconnect(self):
        self.scan(0, {"Speaker": 8})
        self.scan(60, {"Speaker": 8})
        self.scan(62, {"Speaker": 1})
        self.scan(68, {"Speaker": 1})
        self.assertEqual(self.calls, [])
        self.scan(70, {"Speaker": 1})
        self.scan(100, {"Speaker": 1})
        self.assertEqual(len(self.calls), 1)

    def test_binding_change_cancels_pending(self):
        self.scan(0, {"Speaker": 8})
        self.scan(2, {"Speaker": 1})
        self.scan(10, {"New Device": 1}, {0: "New Device"})
        self.scan(30, {"New Device": 1}, {0: "New Device"})
        self.assertEqual(self.calls, [])

    def test_removed_channel_cancels_pending(self):
        self.scan(0, {"Speaker": 8})
        self.scan(2, {"Speaker": 1})
        self.scan(10, {}, {})
        self.scan(20, {"Speaker": 1})
        self.assertEqual(self.calls, [])

    def test_new_channel_starts_without_restart(self):
        self.scan(0, {"Speaker": 1})
        self.scan(10, {"Other": 1}, {4: "Other"})
        self.scan(30, {"Other": 1}, {4: "Other"})
        self.assertEqual(self.calls, [])
        self.assertEqual(self.monitor.current, {"A5": True})

    def test_exact_matching_ignores_hands_free(self):
        state = m.classify_endpoints([
            {"name": "Headphones", "state": 1},
            {"name": "Headphones Hands-Free", "state": 8}], "Headphones", exact=True)
        self.assertTrue(state.active)
        self.assertFalse(state.ambiguous)

    def test_unknown_cancels_pending_and_rebaselines(self):
        self.scan(0, {"Speaker": 8})
        self.scan(2, {"Speaker": 1})
        self.monitor.update([{"name": "Speaker", "state": 1}, {"name": "Speaker", "state": 8}], {0: "Speaker"}, 8)
        self.scan(20, {"Speaker": 1})
        self.scan(40, {"Speaker": 1})
        self.assertEqual(self.calls, [])
        state = m.classify_endpoints([{"name": "", "state": 0, "unreadable": True}], "Speaker", exact=True)
        self.assertTrue(state.ambiguous)

    def test_flapping_requires_new_settle_window(self):
        for now, state in [(0, 8), (2, 1), (6, 8), (8, 1), (14, 1)]:
            self.scan(now, {"Speaker": state})
        self.assertEqual(self.calls, [])
        self.scan(16, {"Speaker": 1})
        self.assertEqual(len(self.calls), 1)

    def test_simultaneous_recovery_and_cooldown(self):
        bindings = {0: "Speaker", 2: "Other"}
        self.scan(0, {"Speaker": 8, "Other": 8}, bindings)
        self.scan(2, {"Speaker": 1, "Other": 1}, bindings)
        self.scan(10, {"Speaker": 1, "Other": 1}, bindings)
        self.assertEqual(len(self.calls), 1)
        self.scan(12, {"Speaker": 8, "Other": 1}, bindings)
        self.scan(14, {"Speaker": 1, "Other": 1}, bindings)
        self.scan(22, {"Speaker": 1, "Other": 1}, bindings)
        self.assertEqual(len(self.calls), 1)
        self.scan(26, {"Speaker": 1, "Other": 1}, bindings)
        self.assertEqual(len(self.calls), 2)

    def test_failed_restart_is_not_retried(self):
        def fail():
            self.calls.append(True)
            raise RuntimeError("API failure")
        self.monitor.restart = fail
        self.scan(0, {"Speaker": 8})
        self.scan(2, {"Speaker": 1})
        with self.assertRaises(RuntimeError):
            self.scan(10, {"Speaker": 1})
        self.scan(30, {"Speaker": 1})
        self.assertEqual(len(self.calls), 1)


if __name__ == "__main__":
    unittest.main()
