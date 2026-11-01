using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace Reconnect
{
    public sealed class BackgroundContext : ApplicationContext
    {
        readonly MonitorEngine engine;
        readonly EventLog log;
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem toggle;
        readonly Control dispatch = new Control();
        readonly string directory;
        volatile bool stopping;
        NamedPipeServerStream server;

        [STAThread]
        public static int Main(string[] args)
        {
            int index = Array.IndexOf(args, "--data-dir");
            string directory = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoicemeeterReconnect");
            bool created;
            using (var mutex = new Mutex(true, @"Local\VoicemeeterReconnect.Background." + WindowsIdentity.GetCurrent().User.Value, out created))
            {
                if (!created) return 0;
                try
                {
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    using (var context = new BackgroundContext(directory, args.Contains("--paused"))) Application.Run(context);
                    mutex.ReleaseMutex(); return 0;
                }
                catch (Exception ex)
                { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "background-error.log"), ex.ToString()); return 1; }
            }
        }
        BackgroundContext(string directory, bool paused)
        {
            this.directory = directory; Directory.CreateDirectory(directory); log = new EventLog(directory);
            Settings settings; bool valid = true;
            try { settings = SettingsFile.Load(Path.Combine(directory, "settings.json")); }
            catch (Exception ex) { valid = false; settings = new Settings(); log.Write("配置无效，已暂停：" + ex.Message); }
            string legacy = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "monitor.pid");
            for (var folder = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); folder != null; folder = folder.Parent)
                if (File.Exists(Path.Combine(folder.FullName, "autorestart-audio-engine.py"))) { legacy = Path.Combine(folder.FullName, "logs", "monitor.pid"); break; }
            dispatch.CreateControl(); var handle = dispatch.Handle;
            engine = new MonitorEngine(settings, log, legacy);
            tray = new NotifyIcon();
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico")) tray.Icon = new System.Drawing.Icon(stream);
            tray.Text = "Reconnect · 正在启动";
            var menu = new ContextMenuStrip(); menu.Items.Add("打开配置窗口", null, delegate { OpenUi(); });
            toggle = new ToolStripMenuItem("开始监控", null, delegate { engine.SetEnabled(!engine.Enabled); }); menu.Items.Add(toggle);
            menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("退出并停止监控", null, delegate { Stop(); });
            tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { OpenUi(); }; tray.Visible = true;
            engine.Updated += delegate(Snapshot snapshot)
            {
                if (!stopping) dispatch.BeginInvoke(new Action(delegate { if (!stopping) { tray.Text = "Reconnect · " + snapshot.State; toggle.Text = engine.Enabled ? "暂停监控" : "开始监控"; } }));
            };
            var listener = new Thread(Listen); listener.IsBackground = true; listener.Start();
            Microsoft.Win32.SystemEvents.SessionEnding += SessionEnding;
            log.Write("原生后台 2.0 已启动，配置窗口按需运行");
            engine.SetEnabled(valid && settings.StartMonitoring && !paused);
        }
        void OpenUi()
        {
            try { Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VoicemeeterReconnect.exe"), "--data-dir \"" + directory + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { log.Write("无法打开配置窗口：" + ex.Message); }
        }
        void Listen()
        {
            while (!stopping)
            {
                try
                {
                    var security = new PipeSecurity();
                    security.SetAccessRuleProtection(true, false);
                    security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                    using (var pipe = new NamedPipeServerStream(DaemonClient.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None, 4096, 65536, security))
                    {
                        server = pipe; pipe.WaitForConnection();
                        string command;
                        using (var reader = new BinaryReader(pipe, System.Text.Encoding.UTF8, true)) command = reader.ReadString();
                        string error = null;
                        try
                        {
                            if (command == "START") engine.SetEnabled(true);
                            else if (command == "PAUSE") engine.SetEnabled(false);
                            else if (command == "APPLY") engine.Apply(SettingsFile.Load(Path.Combine(directory, "settings.json")));
                            else if (command != "STATUS" && command != "QUIT") throw new ArgumentException("未知后台命令。");
                        }
                        catch (Exception ex) { error = ex.Message; }
                        DaemonClient.Reply(pipe, new WireStatus { Snapshot = engine.Latest, Lines = log.Lines, Enabled = engine.Enabled, Error = error, DataDirectory = directory });
                        if (command == "QUIT") dispatch.BeginInvoke(new Action(Stop));
                    }
                }
                catch (Exception ex) { if (!stopping) { log.Write("本地配置连接中断：" + ex.Message); Thread.Sleep(250); } }
                finally { server = null; }
            }
        }
        void SessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e) { if (!stopping) dispatch.Invoke(new Action(Stop)); }
        void Stop()
        {
            if (stopping) return; stopping = true;
            Microsoft.Win32.SystemEvents.SessionEnding -= SessionEnding;
            engine.Dispose();
            var pipe = server; if (pipe != null) pipe.Dispose();
            tray.Visible = false; tray.Icon.Dispose(); tray.Dispose(); dispatch.Dispose(); ExitThread();
        }
        protected override void Dispose(bool disposing) { if (disposing && !stopping) Stop(); base.Dispose(disposing); }
    }
}
