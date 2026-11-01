using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Reconnect
{
    [DataContract]
    public sealed class Settings
    {
        [DataMember] public double PollSeconds = 2;
        [DataMember] public double SettleSeconds = 7;
        [DataMember] public double CooldownSeconds = 15;
        [DataMember] public double RetrySeconds = 15;
        [DataMember] public int MaxRetries = 0;
        [DataMember] public bool StartMonitoring = true;
        [DataMember] public string DllPath = "";
        [OnDeserializing] void Defaults(StreamingContext context)
        { PollSeconds = 2; SettleSeconds = 7; CooldownSeconds = 15; RetrySeconds = 15; MaxRetries = 0; StartMonitoring = true; DllPath = ""; }
        public Settings Copy() { return (Settings)MemberwiseClone(); }
        public void Validate()
        {
            Check(PollSeconds, 0.5, 60, "检测间隔");
            Check(SettleSeconds, 1, 120, "重连稳定等待");
            Check(CooldownSeconds, 1, 300, "重启冷却");
            Check(RetrySeconds, 1, 300, "失败重试间隔");
            if (MaxRetries < 0 || MaxRetries > 5) throw new ArgumentException("失败重试次数应为 0～5。首次尝试不计入重试次数。");
        }
        static void Check(double value, double min, double max, string label)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
                throw new ArgumentException(label + "应为 " + min + "～" + max + " 秒。");
        }
    }

    public static class SettingsFile
    {
        public static Settings Load(string path)
        {
            if (!File.Exists(path)) return new Settings();
            using (var stream = File.OpenRead(path))
            {
                var settings = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);
                if (settings == null) throw new InvalidDataException("配置文件为空。");
                settings.Validate(); return settings;
            }
        }
        public static void Save(string path, Settings settings)
        {
            settings.Validate(); Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { new DataContractJsonSerializer(typeof(Settings)).WriteObject(stream, settings); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
    }

    public static class Startup
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "VoicemeeterReconnect";
        public static bool Enabled
        {
            get { using (var key = Registry.CurrentUser.OpenSubKey(Key)) return key != null && key.GetValue(Name) != null; }
        }
        public static void Set(bool enabled, string exe, string dataDirectory)
        {
            string command = enabled ? BuildCommand(exe, dataDirectory) : null;
            using (var key = Registry.CurrentUser.CreateSubKey(Key))
            {
                if (enabled)
                {
                    key.SetValue(Name, command, RegistryValueKind.String);
                }
                else key.DeleteValue(Name, false);
            }
        }
        public static string BuildCommand(string exe, string dataDirectory)
        {
            string command = "\"" + exe + "\" --tray --data-dir \"" + dataDirectory + "\"";
            if (command.Length > 260) throw new InvalidOperationException("程序路径过长，无法登记开机自启。请移动到较短路径。");
            return command;
        }
    }

    public sealed class EventLog
    {
        readonly object gate = new object();
        readonly List<string> lines = new List<string>();
        readonly string path;
        public event Action Changed;
        public EventLog(string directory, string fileName = "app.log") { Directory.CreateDirectory(directory); path = Path.Combine(directory, fileName); }
        public string[] Lines { get { lock (gate) return lines.ToArray(); } }
        public void Write(string message)
        {
            lock (gate)
            {
                string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
                lines.Add(line); if (lines.Count > 100) lines.RemoveAt(0);
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                    { if (File.Exists(path + ".1")) File.Delete(path + ".1"); File.Move(path, path + ".1"); }
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            var handler = Changed; if (handler != null) handler();
        }
    }

    public sealed class Endpoint
    { public string Name = ""; public uint State; public bool Unreadable; }
    public sealed class OutputRow
    {
        public string Bus { get; set; }
        public string Device { get; set; }
        public string Status { get; set; }
        public string Color { get; set; }
    }
    public sealed class Snapshot
    {
        public string State = "已暂停";
        public string Detail = "开始监控后自动识别当前硬件输出";
        public bool Enabled;
        public bool Connected;
        public List<OutputRow> Outputs = new List<OutputRow>();
        public int Restarts;
        public DateTime? LastRestart;
    }

    public sealed class ReconnectState
    {
        sealed class Track
        { public string Name; public bool? Active; public double? Pending; public int Attempts; public double NextAttempt; }
        readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();
        double lastAttempt = double.NegativeInfinity;
        public int Restarts { get; private set; }
        public DateTime? LastRestart { get; private set; }
        public void Reset() { tracks.Clear(); }
        static string Norm(string s) { return (s ?? "").Trim().ToUpperInvariant(); }
        public List<OutputRow> Update(List<Endpoint> endpoints, Dictionary<int, string> bindings,
            double now, Settings settings, Func<bool> restart, Action<string> log)
        {
            foreach (int bus in tracks.Keys.Where(b => !bindings.ContainsKey(b) || string.IsNullOrWhiteSpace(bindings[b])).ToArray()) tracks.Remove(bus);
            var rows = new List<OutputRow>();
            var due = new List<Track>();
            foreach (var binding in bindings.OrderBy(b => b.Key))
            {
                int bus = binding.Key; string name = binding.Value;
                if (string.IsNullOrWhiteSpace(name)) continue;
                Track t;
                if (!tracks.TryGetValue(bus, out t) || Norm(t.Name) != Norm(name))
                { t = new Track { Name = name }; tracks[bus] = t; log("A" + (bus + 1) + " 监控设备：" + name); }
                var matches = endpoints.Where(e => Norm(e.Name) == Norm(name)).ToList();
                bool unknown = matches.Any(e => e.Unreadable) ||
                    (matches.Count == 0 && endpoints.Any(e => e.Unreadable)) ||
                    matches.Select(e => (e.State & 1) != 0).Distinct().Count() > 1;
                bool active = matches.Any(e => (e.State & 1) != 0);
                string status = unknown ? "状态未知" : active ? "在线" : "离线";
                string color = unknown ? "#D89B36" : active ? "#16856B" : "#8993A5";
                if (unknown) { t.Active = null; t.Pending = null; t.Attempts = 0; }
                else
                {
                    if (t.Active.HasValue && t.Active.Value != active)
                    {
                        if (active)
                        { t.Pending = now; t.Attempts = 0; t.NextAttempt = 0; log("A" + (bus + 1) + " 已重连，等待稳定 " + settings.SettleSeconds + " 秒"); }
                        else { t.Pending = null; t.Attempts = 0; log("A" + (bus + 1) + " 已离线，不重启"); }
                    }
                    t.Active = active;
                    if (active && t.Pending.HasValue)
                    {
                        double wait = Math.Max(t.Pending.Value + settings.SettleSeconds, Math.Max(t.NextAttempt, lastAttempt + settings.CooldownSeconds)) - now;
                        status = t.Attempts == 0 ? "等待恢复" : "等待重试 " + t.Attempts + "/" + settings.MaxRetries;
                        if (wait > 0) status += " · " + Math.Ceiling(wait) + "s";
                        color = "#4368D9";
                        if (wait <= 0) due.Add(t);
                    }
                }
                rows.Add(new OutputRow { Bus = "A" + (bus + 1), Device = name, Status = status, Color = color });
            }
            if (due.Count > 0)
            {
                lastAttempt = now;
                foreach (var t in due) t.Attempts++;
                bool success = false;
                try { success = restart(); }
                catch (Exception ex) { log("重启命令失败：" + ex.Message); }
                if (success)
                {
                    Restarts++; LastRestart = DateTime.Now;
                    log("已提交音频引擎重启（本次合并 " + due.Count + " 路输出），累计 " + Restarts + " 次");
                    foreach (var t in due) t.Pending = null;
                }
                else foreach (var t in due)
                {
                    if (t.Attempts > settings.MaxRetries)
                    { t.Pending = null; log(t.Name + " 已达重试上限；等待下一次真实重连"); }
                    else { t.NextAttempt = now + settings.RetrySeconds; log(t.Name + " 将在间隔及冷却结束后重试（" + t.Attempts + "/" + settings.MaxRetries + "）"); }
                }
            }
            return rows;
        }
    }

    public sealed class MonitorEngine : IDisposable
    {
        readonly object gate = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread thread;
        readonly EventLog log;
        readonly string legacyPid;
        Settings settings;
        volatile bool stopping, enabled, reset;
        public event Action<Snapshot> Updated;
        public Snapshot Latest = new Snapshot();
        public MonitorEngine(Settings settings, EventLog log, string legacyPid)
        {
            this.settings = settings.Copy(); this.log = log; this.legacyPid = legacyPid;
            thread = new Thread(Loop); thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start();
        }
        public bool Enabled { get { return enabled; } }
        public void SetEnabled(bool value) { lock (gate) { enabled = value; reset = true; } wake.Set(); }
        public void Apply(Settings value) { value.Validate(); lock (gate) { settings = value.Copy(); reset = true; } wake.Set(); }
        void Publish(Snapshot value)
        { Latest = value; var handler = Updated; if (handler != null) handler(value); }
        bool LegacyRunning()
        {
            try
            {
                int id;
                if (!File.Exists(legacyPid) || !int.TryParse(File.ReadAllText(legacyPid).Trim(), out id)) return false;
                using (var p = Process.GetProcessById(id))
                {
                    var markerTime = File.GetLastWriteTime(legacyPid);
                    return !p.HasExited && p.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase) && Math.Abs((p.StartTime - markerTime).TotalSeconds) < 10;
                }
            }
            catch (ArgumentException) { return false; }
            catch (IOException) { return false; }
        }
        void Loop()
        {
            var state = new ReconnectState(); var clock = Stopwatch.StartNew();
            VmRemote vm = null; AudioEndpoints audio = null; string lastError = null;
            try
            {
                while (!stopping)
                {
                    Settings cfg; bool shouldReset;
                    lock (gate) { cfg = settings.Copy(); shouldReset = reset; reset = false; }
                    if (shouldReset)
                    {
                        state.Reset();
                        if (vm != null) { vm.Dispose(); vm = null; }
                        if (audio != null) { audio.Dispose(); audio = null; }
                    }
                    if (!enabled)
                    {
                        if (vm != null) { vm.Dispose(); vm = null; }
                        if (audio != null) { audio.Dispose(); audio = null; }
                        Publish(new Snapshot { State = "监控已暂停", Detail = "暂停期间不读取设备、不发送重启命令", Restarts = state.Restarts, LastRestart = state.LastRestart });
                        wake.WaitOne(); continue;
                    }
                    try
                    {
                        if (LegacyRunning()) throw new InvalidOperationException("旧版 Python 监控仍在运行，请先正常退出旧版，避免重复重启。");
                        if (vm == null) { vm = new VmRemote(cfg.DllPath); state.Reset(); log.Write("已连接 Voicemeeter，自动监控 " + vm.OutputCount + " 个硬件输出"); }
                        if (audio == null) audio = new AudioEndpoints();
                        var endpoints = audio.Read(); var bindings = vm.Bindings();
                        List<OutputRow> rows;
                        lock (gate)
                        {
                            if (!enabled || stopping || reset) continue;
                            rows = state.Update(endpoints, bindings, clock.Elapsed.TotalSeconds, cfg, vm.Restart, log.Write);
                        }
                        Publish(new Snapshot { Enabled = true, Connected = true, State = "正在守护音频", Detail = "自动跟随当前输出绑定 · 每 " + cfg.PollSeconds + " 秒检查", Outputs = rows, Restarts = state.Restarts, LastRestart = state.LastRestart });
                        lastError = null;
                        wake.WaitOne(TimeSpan.FromSeconds(cfg.PollSeconds));
                    }
                    catch (Exception ex)
                    {
                        state.Reset();
                        if (vm != null) { vm.Dispose(); vm = null; }
                        if (audio != null) { audio.Dispose(); audio = null; }
                        if (lastError != ex.Message) log.Write(ex.Message);
                        lastError = ex.Message;
                        Publish(new Snapshot { Enabled = true, State = "等待连接", Detail = ex.Message, Restarts = state.Restarts, LastRestart = state.LastRestart });
                        wake.WaitOne(TimeSpan.FromSeconds(cfg.RetrySeconds));
                    }
                }
            }
            finally
            { if (vm != null) vm.Dispose(); if (audio != null) audio.Dispose(); log.Write("监控已停止，接口连接已释放"); }
        }
        public void Dispose()
        { stopping = true; wake.Set(); if (thread.Join(5000)) wake.Dispose(); }
    }

    public sealed class VmRemote : IDisposable
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32")] static extern bool FreeLibrary(IntPtr module);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Simple();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetTypeDelegate(out int type);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] delegate int GetStringDelegate([MarshalAs(UnmanagedType.LPStr)] string name, [Out] StringBuilder value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)] delegate int SetFloatDelegate(string name, float value);
        IntPtr library;
        Simple logout, dirty;
        GetStringDelegate getString;
        SetFloatDelegate setFloat;
        bool loggedIn;
        public int OutputCount { get; private set; }
        T Function<T>(string name) where T : class
        {
            IntPtr proc = GetProcAddress(library, name);
            if (proc == IntPtr.Zero) throw new InvalidOperationException("官方 DLL 缺少接口 " + name);
            return Marshal.GetDelegateForFunctionPointer(proc, typeof(T)) as T;
        }
        public VmRemote(string configured)
        {
            string dll = configured;
            if (string.IsNullOrWhiteSpace(dll))
            {
                string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VoicemeeterRemote64.dll");
                dll = File.Exists(local) ? local : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VB", "Voicemeeter", "VoicemeeterRemote64.dll");
            }
            if (!File.Exists(dll)) throw new FileNotFoundException("未找到官方 Remote DLL，请在设置中选择 VoicemeeterRemote64.dll。");
            library = LoadLibrary(Path.GetFullPath(dll));
            if (library == IntPtr.Zero) throw new InvalidOperationException("无法加载 64 位 Remote DLL，错误码 " + Marshal.GetLastWin32Error());
            try
            {
                var login = Function<Simple>("VBVMR_Login"); logout = Function<Simple>("VBVMR_Logout");
                dirty = Function<Simple>("VBVMR_IsParametersDirty"); getString = Function<GetStringDelegate>("VBVMR_GetParameterStringW");
                setFloat = Function<SetFloatDelegate>("VBVMR_SetParameterFloat");
                int rc = login(); loggedIn = rc >= 0;
                if (rc != 0) throw new InvalidOperationException("等待 Voicemeeter 启动（登录返回 " + rc + "）");
                int type; rc = Function<GetTypeDelegate>("VBVMR_GetVoicemeeterType")(out type);
                if (rc != 0 || type < 1 || type > 3) throw new InvalidOperationException("无法识别 Voicemeeter 版本。");
                OutputCount = type == 1 ? 1 : type == 2 ? 3 : 5;
            }
            catch { Dispose(); throw; }
        }
        public Dictionary<int, string> Bindings()
        {
            if (dirty() < 0) throw new InvalidOperationException("Voicemeeter 参数连接中断，等待重新连接。");
            var result = new Dictionary<int, string>();
            for (int bus = 0; bus < OutputCount; bus++)
            {
                var text = new StringBuilder(512); int rc = getString("Bus[" + bus + "].device.name", text);
                if (rc != 0) throw new InvalidOperationException("无法读取 A" + (bus + 1) + " 绑定，返回 " + rc);
                result[bus] = text.ToString();
            }
            return result;
        }
        public bool Restart()
        { int rc = setFloat("Command.Restart", 1); if (rc != 0) throw new InvalidOperationException("Remote API 返回 " + rc); return true; }
        public void Dispose()
        { if (loggedIn) { logout(); loggedIn = false; } if (library != IntPtr.Zero) { FreeLibrary(library); library = IntPtr.Zero; } }
    }

    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDeviceEnumerator
    {
        void EnumAudioEndpoints(int flow, uint mask, out IDeviceCollection collection);
        void GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
        void RegisterEndpointNotificationCallback(IntPtr client);
        void UnregisterEndpointNotificationCallback(IntPtr client);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDeviceCollection { void GetCount(out uint count); void Item(uint index, out IDevice device); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDevice
    {
        void Activate(ref Guid iid, uint context, IntPtr parameters, out IntPtr instance);
        void OpenPropertyStore(uint access, out IPropertyStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out uint state);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint count); void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value); void Commit();
    }
    public sealed class AudioEndpoints : IDisposable
    {
        [DllImport("ole32")] static extern int PropVariantClear(ref PropVariant value);
        IDeviceEnumerator enumerator;
        public AudioEndpoints()
        { enumerator = (IDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))); }
        public List<Endpoint> Read()
        {
            IDeviceCollection collection = null; var result = new List<Endpoint>();
            try
            {
                enumerator.EnumAudioEndpoints(0, 15, out collection); uint count; collection.GetCount(out count);
                for (uint i = 0; i < count; i++)
                {
                    IDevice device = null; IPropertyStore store = null; var endpoint = new Endpoint();
                    try
                    {
                        collection.Item(i, out device); device.GetState(out endpoint.State); device.OpenPropertyStore(0, out store);
                        var key = new PropertyKey { Format = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = 14 };
                        PropVariant value; store.GetValue(ref key, out value);
                        try { if (value.Type == 31) endpoint.Name = Marshal.PtrToStringUni(value.Pointer) ?? ""; }
                        finally { PropVariantClear(ref value); }
                        endpoint.Unreadable = string.IsNullOrWhiteSpace(endpoint.Name);
                    }
                    catch (Exception) { endpoint.Unreadable = true; }
                    finally { Release(store); Release(device); }
                    result.Add(endpoint);
                }
            }
            finally { Release(collection); }
            return result;
        }
        static void Release(object obj) { if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj); }
        public void Dispose() { Release(enumerator); enumerator = null; }
    }
}
