using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace Reconnect
{
    public sealed class DesktopApp : Application
    {
        Window window;
        DesktopConnection engine;
        EventLog log;
        Settings settings;
        string directory, configPath;
        bool exiting;
        EventWaitHandle showEvent;
        EventWaitHandle quitEvent;
        Thread showThread;
        T Find<T>(string name) where T : class { return window.FindName(name) as T; }

        [STAThread]
        public static int RunDesktop(string[] args)
        {
            string directory = Argument(args, "--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoicemeeterReconnect");
            directory = Path.GetFullPath(directory);
            try
            {
                Directory.CreateDirectory(directory);
                if (args.Contains("--self-test")) return NativeTests.Run(directory);
                if (args.Contains("--probe")) return Probe(directory);
                string user = WindowsIdentity.GetCurrent().User.Value;
                bool created;
                using (var mutex = new Mutex(true, @"Local\VoicemeeterReconnect." + user, out created))
                using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\VoicemeeterReconnect.Show." + user))
                using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\VoicemeeterReconnect.Quit." + user))
                {
                    if (!created) { if (args.Contains("--quit")) quit.Set(); else signal.Set(); return 0; }
                    if (args.Contains("--quit")) return 0;
                    var app = new DesktopApp(); app.directory = directory; app.showEvent = signal; app.quitEvent = quit;
                    app.Initialize(args);
                    app.Run(); mutex.ReleaseMutex();
                }
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(directory, "startup-error.log"), ex.ToString()); } catch { }
                if (!args.Contains("--self-test") && !args.Contains("--probe")) MessageBox.Show(ex.Message, "Reconnect 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
        static string Argument(string[] args, string name)
        { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        static int Probe(string directory)
        {
            using (var audio = new AudioEndpoints())
            using (var vm = new VmRemote(""))
            {
                Thread.Sleep(250);
                var bindings = vm.Bindings(); var endpoints = audio.Read();
                var lines = bindings.Select(b => "A" + (b.Key + 1) + " = " + b.Value).ToList();
                lines.AddRange(endpoints.Select(e => e.Name + " | state=" + e.State + " | unreadable=" + e.Unreadable));
                File.WriteAllLines(Path.Combine(directory, "probe.txt"), lines.ToArray());
            }
            return 0;
        }
        void Initialize(string[] args)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var remote = DaemonClient.Send("STATUS");
            if (!string.IsNullOrEmpty(remote.DataDirectory)) directory = remote.DataDirectory;
            configPath = Path.Combine(directory, "settings.json");
            log = new EventLog(directory, "ui.log");
            string configError = null;
            try { settings = SettingsFile.Load(configPath); }
            catch (Exception ex) { settings = new Settings(); configError = "配置读取失败，已暂停监控。请检查后重新保存：" + ex.Message; }
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) window = (Window)XamlReader.Load(stream);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico")) window.Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            MainWindow = window;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string legacyPid = Path.Combine(baseDir, "logs", "monitor.pid");
            var parent = Directory.GetParent(baseDir.TrimEnd(Path.DirectorySeparatorChar));
            // Development output is dist/VoicemeeterReconnect; locate the existing test helper.
            for (var folder = parent; folder != null; folder = folder.Parent)
            {
                string candidate = Path.Combine(folder.FullName, "autorestart-audio-engine.py");
                if (File.Exists(candidate)) { legacyPid = Path.Combine(folder.FullName, "logs", "monitor.pid"); break; }
            }
            engine = new DesktopConnection();
            engine.Updated += delegate(Snapshot snapshot) { QueueRefresh(); };
            log.Changed += delegate { QueueRefresh(); };
            BindControls(); FillSettings();
            window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!exiting) { e.Cancel = true; Dispatcher.BeginInvoke(new Action(delegate { ExitUi(false); })); } };
            window.IsVisibleChanged += delegate { if (window.IsVisible) Refresh(); };
            showThread = new Thread(delegate()
            {
                while (!exiting)
                {
                    int which = WaitHandle.WaitAny(new WaitHandle[] { showEvent, quitEvent });
                    if (!exiting) Dispatcher.BeginInvoke(new Action(which == 0 ? (Action)ShowWindow : ExitApp));
                }
            });
            showThread.IsBackground = true; showThread.Start();
            DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
            { log.Write("界面操作失败：" + e.Exception.Message); MessageBox.Show(window, e.Exception.Message, "操作未完成"); e.Handled = true; };
            log.Write("Reconnect 2.0 已启动");
            if (configError != null) log.Write(configError);
            if (!args.Contains("--tray")) window.Show();
        }
        void QueueRefresh()
        {
            if (exiting || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (exiting) return;
                if (window.IsVisible) Refresh();
            }));
        }
        void Refresh()
        {
            Snapshot state = engine.Latest;
            Find<TextBlock>("StateText").Text = state.State;
            Find<TextBlock>("StateDetail").Text = state.Detail;
            Find<Button>("ToggleMonitor").Content = engine.Enabled ? "暂停监控" : "开始监控";
            Find<TextBlock>("OutputCount").Text = state.Outputs.Count.ToString();
            Find<TextBlock>("RestartCount").Text = state.Restarts.ToString();
            Find<TextBlock>("LastRestart").Text = state.LastRestart.HasValue ? state.LastRestart.Value.ToString("HH:mm:ss") : "尚未触发";
            Find<ItemsControl>("OutputsList").ItemsSource = state.Outputs;
            Find<TextBlock>("EmptyOutputs").Visibility = state.Outputs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>("FooterStatus").Text = state.Connected ? "监控中" : state.Enabled ? "等待连接" : "已暂停";
            var text = Find<TextBox>("LogText"); string lines = string.Join(Environment.NewLine, engine.RecentLines.Concat(log.Lines).OrderBy(s => s.Substring(0, Math.Min(8, s.Length))).ToArray());
            if (text.Text != lines) { text.Text = lines; text.ScrollToEnd(); }
        }
        void BindControls()
        {
            Find<RadioButton>("NavOverview").Checked += delegate { Navigate("Overview"); };
            Find<RadioButton>("NavSettings").Checked += delegate { Navigate("Settings"); };
            Find<RadioButton>("NavLogs").Checked += delegate { Navigate("Logs"); };
            Find<Button>("HideButton").Click += delegate { ExitUi(false); };
            Find<Button>("ExitButton").Click += delegate { ExitApp(); };
            Find<Button>("ToggleMonitor").Click += delegate { engine.SetEnabled(!engine.Enabled); };
            Find<Button>("SaveSettings").Click += delegate { Save(); };
            Find<Button>("OpenLogFolder").Click += delegate { Process.Start("explorer.exe", "\"" + directory + "\""); };
            Find<Button>("BrowseDll").Click += delegate
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Voicemeeter Remote DLL|VoicemeeterRemote64.dll", Title = "选择官方安装目录中的 64 位 Remote DLL" };
                if (dialog.ShowDialog(window) == true) Find<TextBox>("DllInput").Text = dialog.FileName;
            };
        }
        void Navigate(string page)
        {
            Find<Grid>("OverviewPage").Visibility = page == "Overview" ? Visibility.Visible : Visibility.Collapsed;
            Find<Grid>("SettingsPage").Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            Find<Grid>("LogsPage").Visibility = page == "Logs" ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>("PageTitle").Text = page == "Overview" ? "运行概览" : page == "Settings" ? "运行设置" : "事件记录";
            Find<TextBlock>("PageSubtitle").Text = page == "Overview" ? "设备自由连接，音频自动归位。" : page == "Settings" ? "按你的设备节奏，调整恢复策略。" : "每一次连接与恢复，都有迹可循。";
            Refresh();
        }
        void FillSettings()
        {
            Find<TextBox>("PollInput").Text = settings.PollSeconds.ToString(CultureInfo.CurrentCulture);
            Find<TextBox>("SettleInput").Text = settings.SettleSeconds.ToString(CultureInfo.CurrentCulture);
            Find<TextBox>("CooldownInput").Text = settings.CooldownSeconds.ToString(CultureInfo.CurrentCulture);
            Find<TextBox>("RetryInput").Text = settings.RetrySeconds.ToString(CultureInfo.CurrentCulture);
            Find<TextBox>("MaxRetriesInput").Text = settings.MaxRetries.ToString();
            Find<TextBox>("DllInput").Text = settings.DllPath;
            Find<CheckBox>("AutoStartCheck").IsChecked = Reconnect.Startup.Enabled;
            Find<CheckBox>("StartMonitorCheck").IsChecked = settings.StartMonitoring;
        }
        double Number(string name, string label)
        {
            double value;
            if (!double.TryParse(Find<TextBox>(name).Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) throw new ArgumentException(label + "请输入有效数字。");
            return value;
        }
        void Save()
        {
            var message = Find<TextBlock>("SettingsMessage");
            try
            {
                var value = new Settings { PollSeconds = Number("PollInput", "检测间隔"), SettleSeconds = Number("SettleInput", "稳定等待"), CooldownSeconds = Number("CooldownInput", "冷却间隔"), RetrySeconds = Number("RetryInput", "重试间隔"), DllPath = Find<TextBox>("DllInput").Text.Trim(), StartMonitoring = Find<CheckBox>("StartMonitorCheck").IsChecked == true };
                int retries;
                if (!int.TryParse(Find<TextBox>("MaxRetriesInput").Text, out retries)) throw new ArgumentException("失败重试次数请输入整数。");
                value.MaxRetries = retries; value.Validate();
                if (!string.IsNullOrEmpty(value.DllPath) && !File.Exists(value.DllPath)) throw new ArgumentException("选择的 DLL 路径不存在。");
                bool auto = Find<CheckBox>("AutoStartCheck").IsChecked == true;
                SettingsFile.Save(configPath, value);
                try { if (auto || Reconnect.Startup.Enabled) Reconnect.Startup.Set(auto, Assembly.GetExecutingAssembly().Location, directory); }
                catch { SettingsFile.Save(configPath, settings); throw; }
                settings = value; engine.Apply(value); log.Write("设置已保存并应用；失败重试上限 " + value.MaxRetries + " 次");
                message.Text = "已保存并生效 · " + DateTime.Now.ToString("HH:mm:ss"); message.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 133, 107));
            }
            catch (Exception ex)
            { message.Text = ex.Message; message.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(194, 80, 73)); }
        }
        void ShowWindow() { window.Show(); if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Activate(); Refresh(); }
        void ExitApp() { ExitUi(true); }
        void ExitUi(bool stopMonitoring)
        {
            if (exiting) return; exiting = true;
            if (stopMonitoring) { try { DaemonClient.Send("QUIT"); } catch { } }
            showEvent.Set(); showThread.Join(1000); engine.Dispose();
            window.Close(); Shutdown();
        }
        protected override void OnSessionEnding(SessionEndingCancelEventArgs e) { ExitApp(); base.OnSessionEnding(e); }
    }
}
