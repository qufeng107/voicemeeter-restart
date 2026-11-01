using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;
using Reconnect;

public static class IntegrationTests
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "VoicemeeterReconnect";
    static string exe, directory, profile, reportPath;
    static readonly List<string> report = new List<string>();
    static int passed;
    static void Record(string text)
    { report.Add(DateTime.Now.ToString("HH:mm:ss") + " " + text); File.WriteAllLines(reportPath, report.ToArray()); Console.WriteLine(text); }
    static void Check(bool value, string name)
    { if (!value) throw new Exception("FAIL: " + name); passed++; Record("PASS: " + name); }
    static string Arguments(string data, bool paused = false)
    { return "--tray --data-dir \"" + data + "\"" + (paused ? " --paused" : ""); }
    static Process Launch(string path, string args)
    { return Process.Start(new ProcessStartInfo(path, args) { UseShellExecute = false, CreateNoWindow = true }); }
    static Process[] Processes(string name)
    { int session = Process.GetCurrentProcess().SessionId; return Process.GetProcessesByName(name).Where(p => p.SessionId == session).ToArray(); }
    static WireStatus Wait(Func<WireStatus, bool> predicate, string label, int timeout = 8000)
    {
        var clock = Stopwatch.StartNew(); Exception last = null;
        while (clock.ElapsedMilliseconds < timeout)
        {
            try { var status = DaemonClient.Send("STATUS"); if (predicate(status)) return status; }
            catch (Exception ex) { last = ex; }
            Thread.Sleep(100);
        }
        throw new Exception("Timed out: " + label, last);
    }
    static void StopAll()
    {
        using (var process = Launch(exe, "--quit")) if (!process.WaitForExit(6000)) throw new Exception("Quit launcher did not exit");
        foreach (var process in Processes("VoicemeeterReconnect.Background").Concat(Processes("VoicemeeterReconnect")))
            using (process) if (!process.WaitForExit(8000)) throw new Exception("Application did not stop normally");
    }
    static void Start(string data, bool paused = false, string path = null)
    {
        using (var process = Launch(path ?? exe, Arguments(data, paused)))
            if (!process.WaitForExit(8000) || process.ExitCode != 0) throw new Exception("Tray launcher failed");
        Wait(s => s.DataDirectory == data, "background profile");
    }
    static object RegistryValue()
    { using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) return key == null ? null : key.GetValue(RunName, null, RegistryValueOptions.DoNotExpandEnvironmentNames); }

    [STAThread]
    public static int Main(string[] args)
    {
        exe = Path.GetFullPath(args[0]); directory = Path.GetFullPath(args[1]); Directory.CreateDirectory(directory);
        profile = Path.Combine(directory, "配置 with spaces"); Directory.CreateDirectory(profile); reportPath = Path.Combine(directory, "integration-report.txt");
        WireStatus original = DaemonClient.Send("STATUS");
        bool hadUi = Processes("VoicemeeterReconnect").Length > 0;
        string originalConfig = Path.Combine(original.DataDirectory, "settings.json");
        byte[] configBefore = File.Exists(originalConfig) ? File.ReadAllBytes(originalConfig) : null;
        object startupBefore = RegistryValue(); RegistryValueKind startupKind = RegistryValueKind.String;
        if (startupBefore != null) using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) startupKind = key.GetValueKind(RunName);
        bool success = false;
        try
        {
            Record("BEGIN: real Windows integration checks; production settings preserved");
            StopAll(); Check(Processes("VoicemeeterReconnect.Background").Length == 0, "Normal quit stops the background process");
            var cfg = new Settings { PollSeconds = 0.5, RetrySeconds = 1, SettleSeconds = 1, CooldownSeconds = 1 };
            string settingsPath = Path.Combine(profile, "settings.json"); SettingsFile.Save(settingsPath, cfg);
            Start(profile);
            var status = Wait(s => s.Snapshot.Connected && s.Snapshot.Outputs.Count > 0, "real endpoint detection");
            Check(status.Snapshot.Outputs.All(o => o.Status == "在线" || o.Status == "离线" || o.Status == "状态未知"), "Real Windows endpoint status is readable");
            Check(status.Snapshot.Restarts == 0, "Tray startup does not restart audio");
            Check(Processes("VoicemeeterReconnect").Length == 0, "Tray startup leaves no configuration process");
            var duplicates = Enumerable.Range(0, 3).Select(i => Launch(exe, Arguments(profile))).ToArray();
            foreach (var process in duplicates) using (process) Check(process.WaitForExit(8000) && process.ExitCode == 0, "Duplicate launcher exits successfully");
            Check(Processes("VoicemeeterReconnect.Background").Length == 1, "Concurrent launches keep exactly one background instance");

            for (int i = 0; i < 5; i++)
            {
                DaemonClient.Send("PAUSE"); status = Wait(s => !s.Enabled && s.Snapshot.State == "监控已暂停", "pause");
                Check(!status.Snapshot.Connected, "Pause cycle " + (i + 1) + " releases the live connection");
                DaemonClient.Send("START"); status = Wait(s => s.Enabled && s.Snapshot.Connected, "resume");
                Check(status.Snapshot.Restarts == 0, "Resume cycle " + (i + 1) + " reconnects without restart or leaked API slots");
            }
            cfg.PollSeconds = 1; SettingsFile.Save(settingsPath, cfg); DaemonClient.Send("APPLY");
            status = Wait(s => s.Snapshot.Detail.Contains("每 1 秒"), "hot apply");
            Check(status.Snapshot.Restarts == 0, "Hot apply changes the live interval without audio restart");
            status = DaemonClient.Send("UNRECOGNIZED_TEST_COMMAND"); Check(status.Error != null, "Unsupported IPC command is rejected");

            cfg.DllPath = Path.Combine(directory, "missing-test.dll"); SettingsFile.Save(settingsPath, cfg); DaemonClient.Send("APPLY");
            status = Wait(s => s.Enabled && !s.Snapshot.Connected && s.Snapshot.State == "等待连接", "missing DLL");
            Check(status.Snapshot.Detail.Contains("DLL"), "Missing DLL is reported without crashing");
            cfg.DllPath = ""; SettingsFile.Save(settingsPath, cfg); DaemonClient.Send("APPLY");
            status = Wait(s => s.Snapshot.Connected, "DLL recovery"); Check(status.Snapshot.Restarts == 0, "Correcting DLL path restores monitoring without restart");

            StopAll(); File.WriteAllText(settingsPath, "{broken"); Start(profile);
            status = Wait(s => !s.Enabled && s.Snapshot.State == "监控已暂停", "invalid config pause");
            Check(status.Lines.Any(l => l.Contains("配置无效")), "Invalid startup configuration pauses safely and records the cause");
            SettingsFile.Save(settingsPath, cfg); DaemonClient.Send("APPLY"); DaemonClient.Send("START");
            Check(Wait(s => s.Snapshot.Connected, "fixed config").Snapshot.Restarts == 0, "Corrected configuration can recover without restarting the app");

            StopAll(); cfg.StartMonitoring = false; SettingsFile.Save(settingsPath, cfg); Start(profile);
            status = Wait(s => !s.Enabled && s.Snapshot.State == "监控已暂停", "startup paused");
            Check(!status.Snapshot.Connected, "StartMonitoring=false is respected in tray mode");
            DaemonClient.Send("START"); Check(Wait(s => s.Snapshot.Connected, "manual start").Enabled, "Manual start works with startup auto-monitor disabled");

            Startup.Set(true, exe, profile);
            Check(Startup.Enabled && (string)RegistryValue() == Startup.BuildCommand(exe, profile), "Startup enable writes the exact quoted current-user command");
            string moved = Path.Combine(directory, "moved app", "VoicemeeterReconnect.exe");
            Startup.Set(true, moved, profile); Check((string)RegistryValue() == Startup.BuildCommand(moved, profile), "Startup re-registration updates a moved executable path");
            Startup.Set(false, exe, profile); Check(!Startup.Enabled, "Startup disable removes only this application's value");

            StopAll(); cfg.StartMonitoring = true; SettingsFile.Save(settingsPath, cfg);
            string portable = Path.Combine(directory, "portable app with spaces"); Directory.CreateDirectory(portable);
            foreach (string file in Directory.GetFiles(Path.GetDirectoryName(exe))) File.Copy(file, Path.Combine(portable, Path.GetFileName(file)), true);
            Start(profile, false, Path.Combine(portable, Path.GetFileName(exe)));
            status = Wait(s => s.Snapshot.Connected, "portable path");
            Check(status.DataDirectory == profile && status.Snapshot.Restarts == 0, "Package starts from a path with spaces and a Unicode data directory");

            if (args.Contains("--real-restart"))
            {
                Record("BEGIN: one real Remote API restart using a synthetic reconnect transition; no Bluetooth power toggle");
                DaemonClient.Send("PAUSE"); Wait(s => !s.Enabled && s.Snapshot.State == "监控已暂停", "pause before native API smoke test");
                using (var vm = new VmRemote(""))
                using (var audio = new AudioEndpoints())
                {
                    Thread.Sleep(250);
                    var bindings = vm.Bindings(); var endpoints = audio.Read();
                    var target = bindings.Last(b => endpoints.Any(e => e.Name == b.Value && (e.State & 1) != 0));
                    var offline = endpoints.Select(e => new Endpoint { Name = e.Name, State = e.Name == target.Value ? 8u : e.State, Unreadable = e.Unreadable }).ToList();
                    int calls = 0; var state = new ReconnectState(); Action<string> quiet = delegate { };
                    Func<bool> realRestart = delegate { calls++; return vm.Restart(); };
                    var smokeConfig = new Settings();
                    state.Update(offline, bindings, 0, smokeConfig, realRestart, quiet);
                    state.Update(endpoints, bindings, 1, smokeConfig, realRestart, quiet);
                    state.Update(endpoints, bindings, 9, smokeConfig, realRestart, quiet);
                    state.Update(endpoints, bindings, 100, smokeConfig, realRestart, quiet);
                    Check(calls == 1 && state.Restarts == 1, "Synthetic transition submits exactly one real native restart command");
                    bool unchanged = false;
                    for (int i = 0; i < 10 && !unchanged; i++)
                    { Thread.Sleep(500); unchanged = vm.Bindings().OrderBy(b => b.Key).SequenceEqual(bindings.OrderBy(b => b.Key)); }
                    Check(unchanged, "Real restart preserves all hardware output bindings");
                    Check(audio.Read().Any(e => e.Name == target.Value && (e.State & 1) != 0), "Target Windows endpoint remains active after the real command");
                }
                DaemonClient.Send("START"); Wait(s => s.Snapshot.Connected, "resume after native API smoke test");
            }
            success = true;
        }
        catch (Exception ex) { Record(ex.ToString()); }
        finally
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                { if (startupBefore == null) key.DeleteValue(RunName, false); else key.SetValue(RunName, startupBefore, startupKind); }
                Check(object.Equals(RegistryValue(), startupBefore), "Original startup registration restored exactly");
                StopAll(); Start(original.DataDirectory, !original.Enabled);
                if (original.Enabled) DaemonClient.Send("START");
                Wait(s => s.Enabled == original.Enabled, "restore original monitoring state");
                byte[] configAfter = File.Exists(originalConfig) ? File.ReadAllBytes(originalConfig) : null;
                Check(configBefore == null ? configAfter == null : configAfter != null && configBefore.SequenceEqual(configAfter), "Production settings file is unchanged");
                Check(DaemonClient.Send("STATUS").DataDirectory == original.DataDirectory, "Original production profile and background restored");
                if (hadUi) Launch(exe, "--data-dir \"" + original.DataDirectory + "\"");
                Record((success ? "ALL " + passed + " INTEGRATION CHECKS PASSED" : "INTEGRATION FAILED; restoration completed"));
            }
            catch (Exception ex) { success = false; Record("RESTORATION ERROR: " + ex); }
        }
        return success ? 0 : 1;
    }
}
