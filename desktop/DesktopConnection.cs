using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Reconnect
{
    public static class Bootstrap
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Contains("--self-test") || args.Contains("--probe")) return OpenDesktop(args);
            if (args.Contains("--quit"))
            {
                try { DaemonClient.Send("QUIT"); } catch { }
                try { using (var signal = EventWaitHandle.OpenExisting(@"Local\VoicemeeterReconnect.Quit." + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value)) signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
                return 0;
            }
            int index = Array.IndexOf(args, "--data-dir");
            string directory = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoicemeeterReconnect");
            try { DaemonClient.Send("STATUS"); }
            catch
            {
                string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VoicemeeterReconnect.Background.exe");
                Process.Start(new ProcessStartInfo(exe, "--data-dir \"" + directory + "\"" + (args.Contains("--paused") ? " --paused" : "")) { UseShellExecute = false, CreateNoWindow = true });
                for (int attempt = 0; attempt < 20; attempt++)
                { try { DaemonClient.Send("STATUS"); break; } catch { Thread.Sleep(100); } }
            }
            return args.Contains("--tray") ? 0 : OpenDesktop(args);
        }
        [MethodImpl(MethodImplOptions.NoInlining)] static int OpenDesktop(string[] args) { return DesktopApp.RunDesktop(args); }
    }
    public sealed class DesktopConnection : IDisposable
    {
        readonly Thread thread;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool stopping;
        public Snapshot Latest = new Snapshot();
        public string[] RecentLines = new string[0];
        public bool Enabled { get; private set; }
        public event Action<Snapshot> Updated;
        public DesktopConnection()
        { thread = new Thread(Loop); thread.IsBackground = true; thread.Start(); }
        void Loop()
        {
            while (!stopping)
            {
                try
                {
                    var status = DaemonClient.Send("STATUS"); Latest = status.Snapshot; Enabled = status.Enabled; RecentLines = status.Lines ?? new string[0];
                }
                catch (Exception ex) { Latest = new Snapshot { State = "后台连接中断", Detail = "请退出后重新打开程序。" + ex.Message }; Enabled = false; }
                var handler = Updated; if (handler != null && !stopping) handler(Latest);
                wake.WaitOne(2000);
            }
        }
        public void SetEnabled(bool value) { var result = DaemonClient.Send(value ? "START" : "PAUSE"); if (result.Error != null) throw new Exception(result.Error); Enabled = value; wake.Set(); }
        public void Apply(Settings value) { var result = DaemonClient.Send("APPLY"); if (result.Error != null) throw new Exception(result.Error); wake.Set(); }
        public void Dispose() { stopping = true; wake.Set(); if (thread.Join(3000)) wake.Dispose(); }
    }
}
