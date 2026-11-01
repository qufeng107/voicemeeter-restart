# Voicemeeter Reconnect 2.0

Windows 原生桌面版：C# 后台直接调用 Windows Core Audio 与官方 Voicemeeter Remote API，WPF 配置窗口按需运行。无需安装 Python 或第三方 Python 包。目标环境：Windows 10/11 x64、.NET Framework 4.8，以及已安装的 Voicemeeter。

## 使用

1. 双击 `VoicemeeterReconnect.exe`。默认自动监控当前绑定的全部硬件输出，启动扫描不重启音频。
2. 「运行设置」可调整策略，点击「保存并应用」立即生效，并取消旧的等待任务、重新建立状态基准。
3. 需要开机自启时，打开「登录 Windows 后自动启动」再保存。默认关闭，仅登记当前用户，无需管理员权限。它会在用户登录后进入托盘；Voicemeeter 尚未运行时自动等待连接。
4. 窗口关闭或「收起到托盘」会退出配置界面进程，独立后台继续监控。双击托盘图标或再次打开 EXE 可恢复配置窗口，平时只保留轻量后台。
5. 「退出程序」或托盘菜单「退出并停止监控」会停止后台线程并登出 Remote API。

请关闭 Voicemeeter 自带的 `Auto Restart Audio Engine (All Device)`，避免另一套机制持续重启离线设备。保留现有设备绑定、路由、音量和效果。

## 策略

| 配置 | 默认 | 范围/含义 |
|---|---:|---|
| 检测间隔 | 2 秒 | 0.5～60 秒 |
| 重连稳定等待 | 7 秒 | 1～120 秒，设备持续在线后再恢复 |
| 重启冷却 | 15 秒 | 1～300 秒，全局最短尝试间隔 |
| 失败重试间隔 | 15 秒 | 1～300 秒，同时用于等待 Voicemeeter 启动 |
| 失败重试上限 | 0 次 | 0～5 次，首次尝试之外的额外次数 |
| 启动后自动监控 | 开 | 手动打开及开机启动均遵循此设置 |
| 登录 Windows 后自动启动 | 关 | 在设置页手动开启并保存 |

例如重试上限为 2，最多尝试 3 次；实际重试要同时满足重试间隔和全局冷却。重试仅针对重启命令返回错误/抛出异常的情况。成功提交命令后不会定时重复重启，因为接口成功或端点在线都不能证明实际有声音。设备离线、状态未知、通道绑定变化或暂停监控都会取消等待与重试。

长期离线是正常情况，不触发恢复。多个输出同时就绪尽量合并成一次重启；先后就绪的设备可以分别触发，在全局冷却约束下完成恢复。新绑定的设备先建立基准，之后离线再上线才触发。

普通版、Banana、Potato 分别识别 1/3/5 个硬件输出；当前实机为 Potato。匹配 Windows 完整输出名称，区分免提与立体声；ASIO 或特殊驱动若不对应 Windows 端点名称，可能无法按当前方式识别。

## 文件与隐私

- 配置：`%LOCALAPPDATA%\VoicemeeterReconnect\settings.json`，保存采用临时文件替换，并保留 `.bak`。
- 事件日志：同目录 `app.log`，单文件约 512 KiB，保留一个轮转文件。内存只保留最近 100 条。
- DLL：自动查找程序旁的 `VoicemeeterRemote64.dll` 或官方默认安装目录；自定义安装可在设置中选择官方 DLL。发布包不包含第三方 DLL。
- 程序不联网，无遥测，不更改 Windows 默认音频设备、输入通道或 Voicemeeter 路由。

## 构建与验证

项目利用 Windows 已有的 .NET Framework C# 编译器构建，无需下载 SDK：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File desktop\build.ps1
```

产物：`dist\VoicemeeterReconnect\`。平时打开 `VoicemeeterReconnect.exe`；同目录的 `VoicemeeterReconnect.Background.exe` 与两个 `.exe.config` 也需保留。请整体移动发布文件夹；启用自启后更换路径，需要在新位置重新登记自启。

```powershell
# 纯逻辑验证，不访问音频或改变开机自启
VoicemeeterReconnect.exe --self-test --data-dir D:\Temp\ReconnectTests
# 只读获取真实绑定及端点，不发送重启命令
VoicemeeterReconnect.exe --probe --data-dir D:\Temp\ReconnectTests
# 暂停状态打开界面
VoicemeeterReconnect.exe --paused
# 托盘启动 / 正常退出已有实例
VoicemeeterReconnect.exe --tray
VoicemeeterReconnect.exe --quit
```

`--self-test` 报告写入指定目录的 `self-test.txt`，`--probe` 写入 `probe.txt`。启动失败写入 `startup-error.log`。自测覆盖重连延迟、断线不重启、状态不确定、设备/通道变更、重试上限、间隔与冷却、配置保存、配置兼容和日志边界等 33 项检查。原 Python 测试保留，方便对照已验收行为。

运行 `desktop/test.ps1` 可执行真实 Windows 集成测试：使用独立测试配置，结束后恢复正式配置及自启登记。添加 `-RealRestart` 会使用模拟重连事件驱动一次真实音频引擎重启，短暂中断声音，并核对输出绑定保留；它不等同于真实蓝牙关机/开机或实际出声验收。测试需要在已有正式后台运行时执行，结果写入 `test-artifacts/integration/integration-report.txt`。本机已通过 36 项集成检查，覆盖重复启动、连续暂停/恢复、配置热应用、错误配置/DLL恢复、自启登记与撤销、含空格及中文路径、正常退出等。

启用自启后更换程序路径，在新位置打开设置、保持自启开启并重新保存，即可更新启动路径。

程序采用同用户单实例保护。再次打开会唤起已有窗口。开发目录额外检查旧 Python 测试的 PID 标记，避免与仍在运行的旧版同时监控。

后台与配置窗口通过只允许当前用户访问的本机命名管道通信；关闭配置窗口不会断开音频监控。`--quit` 会正常停止两者。运行参数 `--data-dir` 用于指定配置/日志目录，一位 Windows 用户同时仅有一个后台实例。

2026-11-01 最终修正版本机测量：关闭配置窗口后的后台工作集约 44.2 MiB、私有内存约 29.7 MiB，30 秒内单核 CPU 平均约 0.260%。这是一轮采样，随系统及设备变化。33 项原生逻辑、36 项真实 Windows 集成及 9 项 Python 回归检查均通过。自动测试调用真实原生接口重启一次并确认输出绑定不变；用户随后确认原生桌面版设备测试成功。完整范围见发布文件夹的 TEST_REPORT-2026-11-01.md。

## 参考

- [Microsoft WPF 概览](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)
- [Windows Core Audio 端点状态](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-state-xxx-constants)
- [当前用户登录启动项](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)
