# MiNotifyWin

MiPush Desk 原生 Windows 客户端：接收 MiPush、按需获取应用密钥，并使用 Windows 通知或托盘气泡提醒。当前版本 **1.4.0**，Windows 10 1809 起，x64。

## 运行

解压 MiNotifyWin-1.4.0-win-x64.zip，运行 MiPushDesk.exe，保留同目录文件。发布包包含 .NET 与 Windows App SDK 运行时。通过「设置 → 会话」导入账号 JSON；会话由配套 [MiNoitifyApp](https://github.com/Kinakaze/MiNoitifyApp) 提取并核验。

- 「通知」支持搜索、按应用筛选、详情、复制、单条删除和清空。暂停提醒仍继续保存消息。
- 「应用」管理名称、图标和静音，图标库位于标题旁菜单中。
- 「外观」设置桌面提醒、提醒内容、Logo、配图、提示音和应用界面样式。「测试通知」跟随当前提醒模式和内容。
- 「设置 → 应用密钥」显示本地密钥数量，可手动更新。仅收到带 appId 且缺少对应密钥的加密消息时自动获取；启动、重连和已有密钥的解密失败不会触发刷新。

配图顺序为 **消息图片 → 自选默认图片 → 无图**。未设置默认图且消息无图时不生成占位配图；测试通知同样遵循此规则。只有开启配图并选用图文提醒时，Windows 通知才显示配图。

桌面提醒可选 Windows 通知、托盘气泡、仅应用内记录；提醒内容可选图文与按钮、标题摘要、精简摘要。测试通知允许主动测试暂停和免打扰中的设置，但仍遵循所选提醒模式；仅应用内记录模式不会弹窗。底部预览展示应用内通知列表，系统弹窗由 Windows 控制外观。

默认心跳 30 秒、重连 60 秒，可在设置中调整。断线、KICK 和认证拒绝均按所设重连间隔处理。KICK 的 wait/internal-error 是服务端通用错误；桌面与手机使用同一通道身份并发在线可能冲突，需要手机离线对照才能确认。

关闭窗口默认进入托盘；托盘菜单可暂停、打开窗口、设置自启动或退出。数据保存在 %LOCALAPPDATA%\MiPushDesk，账号与应用密钥由当前 Windows 用户的 DPAPI 加密。更新程序时保留该数据目录。

## 构建与检查

需要 Windows、PowerShell 7 和 .NET SDK 9.0.3xx，检查程序运行于 .NET 8。项目使用 WinUI 3，当前仅构建 x64。

```powershell
pwsh -File scripts/Test.ps1
pwsh -File scripts/Publish.ps1
pwsh -File scripts/Check-Interface.ps1
pwsh -File scripts/Test.ps1 -Native
pwsh -File scripts/Package.ps1 -SkipPublish
```

界面检查与原生通知检查需要交互桌面，使用隔离数据目录和 Preview 通知身份，应顺序运行。CI 执行核心检查并生成 ZIP，不发布 Release。

发布目录为 artifacts/publish/win-x64，ZIP、SHA-256 清单和版本说明在 artifacts/releases/1.4.0。

## 目录

- src/MiPushDesk：WinUI 界面与 Windows 通知。
- src/MiPushDesk.Core：协议、接收、解密、历史和设置。
- tests：核心协议、原生功能检查。
- scripts：构建、验证、打包与图标资源工具。
- docs：共享格式、协议说明、依赖和版本说明。

共享文件格式见 [MiPush JSON](docs/exchange/README.md)，行为细节见 [协议与数据](docs/PROTOCOL.md)，本次改动见 [1.4.0 说明](docs/releases/1.4.0.md)。第三方资源授权见 [THIRD-PARTY.md](docs/THIRD-PARTY.md)；第一方代码的许可证尚未指定。
