# Voicemeeter Reconnect

轻量的 Windows 原生音频重连助手。设备重新连接并稳定后，自动重启 Voicemeeter 音频引擎，恢复硬件输出。

[下载 v2.0.0](https://github.com/qufeng107/voicemeeter-restart/releases/tag/v2.0.0) · [English](Readme.md) · [详细配置与开发说明](desktop/README.md)

## 使用

1. 从 Release 下载 `VoicemeeterReconnect-2.0.0-win-x64.zip`。
2. 将整个文件夹解压到固定位置。
3. 启动 Voicemeeter，双击 `VoicemeeterReconnect.exe`。
4. 在「运行设置」调整恢复时间，需要时开启「登录 Windows 后自动启动」并保存。

请保留同目录的后台 EXE 和两个 `.exe.config` 文件。关闭配置窗口后，独立后台和托盘继续运行；点击「退出程序」或托盘菜单「退出并停止监控」才会结束监控并释放接口。

运行环境：Windows 10/11 x64、.NET Framework 4.8、已安装的 Voicemeeter。程序自动从官方安装目录查找 `VoicemeeterRemote64.dll`，自定义安装可在设置中指定路径。发布包不包含第三方 DLL。

## 功能

- C# 原生后台，WPF 配置窗口按需运行。
- 自动跟随当前硬件输出绑定，设备和 A 通道可以更换。
- 启动扫描不重启，设备长期关机不循环重启。
- 检测离线到在线的转换，等待稳定后恢复，并遵守全局冷却。
- 配置检测间隔、稳定等待、冷却、失败重试间隔与最大额外重试次数。
- 支持当前用户登录自启、托盘控制、同用户单实例、配置热应用。
- 日志限制大小，正常退出会登出 Remote API。

程序保留设备绑定、路由、音量及效果。请关闭 Voicemeeter 内置的 `Auto Restart Audio Engine (All Device)`，避免两套机制同时恢复离线设备。

## 恢复时间在哪调

打开「运行设置」，调整后点「保存并应用」。

| 配置 | 默认值 | 说明 |
|---|---:|---|
| 检测间隔 | 2 秒 | 检查设备的周期 |
| 重连稳定等待 | 7 秒 | 设备持续在线后再重启 |
| 重启冷却 | 15 秒 | 所有输出共用的最短重启间隔 |
| 失败重试间隔 | 15 秒 | 命令失败后的重试间隔，也用于等待 Voicemeeter 启动 |
| 最大额外重试次数 | 0 次 | 可设为 0～5，首次尝试不计入 |
| 启动后自动监控 | 开启 | 程序启动时开始监控 |
| 登录 Windows 后自动启动 | 关闭 | 需要时手动开启并保存 |

例如重试上限为 2，最多尝试 3 次。重试仅用于重启命令失败；成功提交后停止重试。端点在线或接口成功不能证明实际出声，因此不会据此定时反复重启。

如果 A2 刚恢复、A3 接着等待，还会受「重启冷却」限制。部分蓝牙设备就绪较慢，缩短等待时间后应验证恢复是否可靠。

配置与日志位于 `%LOCALAPPDATA%\VoicemeeterReconnect`。启用自启后更换程序位置，在新位置保持自启开关开启并重新保存，即可更新启动路径。

## 验证范围

33 项原生逻辑检查、36 项 Windows 集成检查、9 项 Python 回归测试通过，共 78 项。用户随后确认桌面版设备测试成功。[测试报告](TEST_REPORT-2026-11-01.md) 记录了自动化、真实接口调用和用户验收的区别。

本机 30 秒后台采样：工作集约 44 MiB、私有内存约 30 MiB、单核 CPU 约 0.26%，配置进程数为 0。此结果随系统与设备变化。

程序识别普通版、Banana、Potato 的硬件输出数量，当前实机环境为 Potato。ASIO 或特殊驱动若无法对应 Windows 端点完整名称，可能需要进一步适配。

## 开发与兼容脚本

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File desktop\build.ps1
py -m unittest -q test_monitor
```

构建产物位于 `dist\VoicemeeterReconnect`，详细测试命令见 [desktop/README.md](desktop/README.md)。原 Python 入口和 BAT/VBS 兼容文件仍保留；日常使用以桌面 EXE 为入口，请只运行一套监控。

## 致谢

项目基于 Arne Westphal 的 [voicemeeter-auto-restart](https://github.com/westarne/voicemeeter-auto-restart) 改进，保留原项目的 [MIT 许可与版权声明](LICENSE)。
