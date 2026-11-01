using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Reconnect
{
    public static class NativeTests
    {
        static int passed;
        static readonly List<string> report = new List<string>();
        static void Assert(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; report.Add("PASS: " + name); }
        static List<Endpoint> E(uint state) { return new List<Endpoint> { new Endpoint { Name = "Speaker", State = state } }; }
        static Dictionary<int, string> B() { return new Dictionary<int, string> { { 0, "Speaker" } }; }
        public static int Run(string directory)
        {
            try
            {
                var cfg = new Settings(); Action<string> log = delegate { };
                int calls = 0; Func<bool> restart = delegate { calls++; return true; };
                var m = new ReconnectState();
                m.Update(E(1), B(), 0, cfg, restart, log); m.Update(E(8), B(), 2, cfg, restart, log); m.Update(E(8), B(), 60, cfg, restart, log);
                Assert(calls == 0, "Startup and persistent offline do not restart");
                m.Update(E(1), B(), 62, cfg, restart, log); m.Update(E(1), B(), 68, cfg, restart, log);
                Assert(calls == 0, "Reconnect waits for settle window");
                m.Update(E(1), B(), 70, cfg, restart, log); m.Update(E(1), B(), 100, cfg, restart, log);
                Assert(calls == 1, "Exactly one restart on successful reconnect");

                calls = 0; m = new ReconnectState();
                m.Update(E(8), B(), 0, cfg, restart, log); m.Update(E(1), B(), 2, cfg, restart, log);
                m.Update(new List<Endpoint> { new Endpoint { Name = "New", State = 1 } }, new Dictionary<int, string> { { 0, "New" } }, 10, cfg, restart, log);
                Assert(calls == 0, "Changing device cancels old pending recovery");
                m.Update(E(1), new Dictionary<int, string> { { 4, "Speaker" } }, 30, cfg, restart, log);
                Assert(calls == 0, "Changing channel establishes a new baseline");

                m = new ReconnectState(); m.Update(E(8), B(), 0, cfg, restart, log); m.Update(E(1), B(), 2, cfg, restart, log);
                m.Update(new List<Endpoint>(), new Dictionary<int, string>(), 10, cfg, restart, log);
                m.Update(E(1), B(), 20, cfg, restart, log); Assert(calls == 0, "Removed output cancels pending restart");

                m = new ReconnectState(); m.Update(E(8), B(), 0, cfg, restart, log); m.Update(E(1), B(), 2, cfg, restart, log);
                var ambiguous = E(1); ambiguous.Add(new Endpoint { Name = "Speaker", State = 8 });
                m.Update(ambiguous, B(), 8, cfg, restart, log); m.Update(E(1), B(), 20, cfg, restart, log);
                Assert(calls == 0, "Ambiguity clears pending and rebaselines");
                var unknown = new List<Endpoint> { new Endpoint { Unreadable = true } };
                var rows = m.Update(unknown, B(), 30, cfg, restart, log);
                Assert(rows[0].Status == "状态未知", "Unreadable endpoint is not classified as offline");
                var mixed = E(1); mixed.Add(new Endpoint { Name = "Speaker Hands-Free", State = 8 });
                rows = m.Update(mixed, B(), 40, cfg, restart, log);
                Assert(rows[0].Status == "在线", "Exact matching separates hands-free endpoint");

                calls = 0; cfg = new Settings { MaxRetries = 2, CooldownSeconds = 5, RetrySeconds = 10 };
                Func<bool> fail = delegate { calls++; throw new Exception("mock API failure"); };
                m = new ReconnectState(); m.Update(E(8), B(), 0, cfg, fail, log); m.Update(E(1), B(), 1, cfg, fail, log);
                m.Update(E(1), B(), 8, cfg, fail, log); m.Update(E(1), B(), 17, cfg, fail, log);
                Assert(calls == 1, "Failed restart obeys retry interval");
                m.Update(E(1), B(), 18, cfg, fail, log); m.Update(E(1), B(), 28, cfg, fail, log); m.Update(E(1), B(), 100, cfg, fail, log);
                Assert(calls == 3, "Two retries means three total attempts, then stop");
                calls = 0; cfg.MaxRetries = 0; m = new ReconnectState();
                m.Update(E(8), B(), 0, cfg, fail, log); m.Update(E(1), B(), 1, cfg, fail, log); m.Update(E(1), B(), 8, cfg, fail, log); m.Update(E(1), B(), 100, cfg, fail, log);
                Assert(calls == 1, "Zero retries preserves tested one-attempt behavior");

                calls = 0; cfg.MaxRetries = 2; m = new ReconnectState();
                m.Update(E(8), B(), 0, cfg, fail, log); m.Update(E(1), B(), 1, cfg, fail, log); m.Update(E(1), B(), 8, cfg, fail, log);
                m.Update(E(8), B(), 10, cfg, fail, log); m.Update(E(8), B(), 100, cfg, fail, log);
                Assert(calls == 1, "Disconnect cancels scheduled retries");

                calls = 0; cfg = new Settings(); m = new ReconnectState();
                var bindings = new Dictionary<int, string> { { 0, "Speaker" }, { 2, "Other" } };
                var offline = E(8); offline.Add(new Endpoint { Name = "Other", State = 8 });
                var online = E(1); online.Add(new Endpoint { Name = "Other", State = 1 });
                m.Update(offline, bindings, 0, cfg, restart, log); m.Update(online, bindings, 1, cfg, restart, log); m.Update(online, bindings, 8, cfg, restart, log);
                Assert(calls == 1, "Simultaneous reconnects share one engine restart");
                m.Update(offline, bindings, 9, cfg, restart, log); m.Update(online, bindings, 10, cfg, restart, log); m.Update(online, bindings, 18, cfg, restart, log);
                Assert(calls == 1, "Global cooldown prevents early repeat restart");
                m.Update(online, bindings, 23, cfg, restart, log); Assert(calls == 2, "Queued reconnect runs at cooldown boundary");

                string path = Path.Combine(directory, "test-settings.json");
                cfg = new Settings { MaxRetries = 3, RetrySeconds = 22 }; SettingsFile.Save(path, cfg);
                var loaded = SettingsFile.Load(path); Assert(loaded.MaxRetries == 3 && loaded.RetrySeconds == 22, "Settings persist and round-trip");
                cfg.PollSeconds = 3; SettingsFile.Save(path, cfg); Assert(SettingsFile.Load(path).PollSeconds == 3 && File.Exists(path + ".bak"), "Atomic replacement keeps previous config backup");
                bool rejected = false; try { new Settings { PollSeconds = double.NaN }.Validate(); } catch (ArgumentException) { rejected = true; }
                Assert(rejected, "Non-finite input rejected");
                rejected = false; try { new Settings { MaxRetries = 6 }.Validate(); } catch (ArgumentException) { rejected = true; }
                Assert(rejected, "Unbounded retry input rejected");

                calls = 0; cfg = new Settings(); m = new ReconnectState();
                m.Update(E(8), B(), 0, cfg, restart, log); m.Update(E(1), B(), 1, cfg, restart, log);
                m.Update(E(8), B(), 6, cfg, restart, log); m.Update(E(1), B(), 7, cfg, restart, log); m.Update(E(1), B(), 13, cfg, restart, log);
                Assert(calls == 0, "Flapping cancels the previous settle window");
                m.Update(E(1), B(), 14, cfg, restart, log); Assert(calls == 1, "Recovery waits for a fresh full settle window");

                calls = 0; cfg = new Settings { MaxRetries = 5, RetrySeconds = 3, CooldownSeconds = 10 }; m = new ReconnectState();
                Func<bool> succeedOnRetry = delegate { calls++; return calls > 1; };
                m.Update(E(8), B(), 0, cfg, succeedOnRetry, log); m.Update(E(1), B(), 1, cfg, succeedOnRetry, log); m.Update(E(1), B(), 8, cfg, succeedOnRetry, log);
                m.Update(E(1), B(), 11, cfg, succeedOnRetry, log); Assert(calls == 1, "Retry interval cannot bypass global cooldown");
                m.Update(E(1), B(), 18, cfg, succeedOnRetry, log); m.Update(E(1), B(), 100, cfg, succeedOnRetry, log);
                Assert(calls == 2 && m.Restarts == 1, "A successful retry ends the recovery episode");

                calls = 0; m = new ReconnectState();
                m.Update(E(8), B(), 0, cfg, fail, log); m.Update(E(1), B(), 1, cfg, fail, log); m.Update(E(1), B(), 8, cfg, fail, log);
                m.Update(unknown, B(), 9, cfg, fail, log); m.Update(E(1), B(), 100, cfg, fail, log);
                Assert(calls == 1, "Unknown state cancels a scheduled failure retry");

                calls = 0; m = new ReconnectState();
                m.Update(E(8), B(), 0, cfg, restart, log); m.Update(E(1), B(), 1, cfg, restart, log); m.Reset(); m.Update(E(1), B(), 100, cfg, restart, log);
                Assert(calls == 0, "Reset cancels pending recovery without startup restart");
                var partial = E(1); partial.Add(new Endpoint { Unreadable = true });
                rows = m.Update(partial, B(), 101, cfg, restart, log); Assert(rows[0].Status == "在线", "Unrelated unreadable endpoint does not hide a known output");

                string migration = Path.Combine(directory, "partial-settings.json");
                File.WriteAllText(migration, "{\"MaxRetries\":1}"); loaded = SettingsFile.Load(migration);
                Assert(loaded.PollSeconds == 2 && loaded.SettleSeconds == 7 && loaded.StartMonitoring, "Older partial settings retain safe defaults");
                File.WriteAllText(migration, "{broken"); rejected = false;
                try { SettingsFile.Load(migration); } catch { rejected = true; }
                Assert(rejected, "Malformed configuration is rejected rather than silently applied");
                File.WriteAllText(migration, "null"); rejected = false;
                try { SettingsFile.Load(migration); } catch { rejected = true; }
                Assert(rejected, "Null configuration is rejected");
                Assert(Startup.BuildCommand(@"D:\Apps with spaces\Reconnect.exe", @"D:\我的配置") == "\"D:\\Apps with spaces\\Reconnect.exe\" --tray --data-dir \"D:\\我的配置\"", "Startup command quotes spaces and Unicode paths");
                rejected = false; try { Startup.BuildCommand(new string('x', 270), "test"); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "Oversized startup command is rejected");

                var boundedLog = new EventLog(directory, "bounded-test.log");
                for (int i = 0; i < 140; i++) boundedLog.Write(new string('x', 5000));
                Assert(boundedLog.Lines.Length == 100 && File.Exists(Path.Combine(directory, "bounded-test.log.1")), "Log memory and file retention are bounded");
                report.Add("ALL " + passed + " CHECKS PASSED");
                File.WriteAllLines(Path.Combine(directory, "self-test.txt"), report.ToArray()); return 0;
            }
            catch (Exception ex)
            { report.Add(ex.ToString()); File.WriteAllLines(Path.Combine(directory, "self-test.txt"), report.ToArray()); return 1; }
        }
    }
}
