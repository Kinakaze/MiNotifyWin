# MiNotifyWin

在 Windows 上接收 MiPush 通知，支持 Windows 通知、托盘气泡、消息历史和应用管理。需要 Windows 10 1809 或更新版本，x64。

[下载最新版](https://github.com/Kinakaze/MiNotifyWin/releases/latest)

## 使用

1. 解压发布 ZIP，运行 `MiPushDesk.exe`，保留同目录文件。安装包已包含运行时。
2. 用 [MiNoitifyApp](https://github.com/Kinakaze/MiNoitifyApp) 抓包，将分享的 JSON 文件传到电脑。
3. 在「设置 → 会话」导入文件，或把 JSON 拖入窗口。缺少 `security` 时，可手动填写，或展开「从手机提取」。
4. USB 连接需开启调试并在手机授权；Wi-Fi 连接可在窗口内配对。点击「提取 security」，随后「验证并保存」。程序已内置 ADB。
5. 保存后自动连接；在「外观」调整提醒，在「应用」管理名称、图标和静音。

提取优先读取匹配日志；没有记录时会启动手机诊断，需要 MiNoitifyApp 0.5.0 或更新版本。抓包签名用于验证 `security`；不含签名的账号会通过登录验证。应用正文密钥 `regSecret` 由桌面按需获取。

配图优先使用消息图片，其次使用自选默认图，两者都没有则无图。测试通知跟随当前提醒模式和内容。

关闭窗口默认进入托盘。数据保存在 `%LOCALAPPDATA%\MiPushDesk`，账号与应用密钥由 Windows DPAPI 加密；更新程序时保留该目录。

## 构建

需要 Windows、PowerShell 7、.NET SDK 9.0.3xx 和 .NET 8 运行时。

```powershell
pwsh -File scripts/Test.ps1
pwsh -File scripts/Publish.ps1
pwsh -File scripts/Package.ps1 -SkipPublish
```

产物位于 `artifacts/releases/`。更多信息：[检查与发布](docs/RELEASING.md) · [JSON 格式](docs/exchange/README.md) · [协议与数据](docs/PROTOCOL.md) · [第三方授权](docs/THIRD-PARTY.md)。
