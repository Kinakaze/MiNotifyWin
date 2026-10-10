# 发布

公开仓库为 Kinakaze/MiNotifyWin。先完成本地构建、检查与打包，再提交对应源码、推送版本标签并创建 Release。

在交互式 Windows 桌面运行以下命令。界面检查与原生通知检查使用隔离数据和 Preview 通知身份，应顺序执行。

```powershell
pwsh -File scripts/Test.ps1
pwsh -File scripts/Publish.ps1
pwsh -File scripts/Check-Interface.ps1
pwsh -File scripts/Test.ps1 -Native
pwsh -File scripts/Package.ps1 -SkipPublish
```

版本来自 `src/MiPushDesk/MiPushDesk.csproj`，标签为 `v<Version>`；上传 `artifacts/releases/<Version>/` 内的 ZIP、`SHA256SUMS.txt` 和 `RELEASE-NOTES.md`。

提交署名为 mitsukina <mitsukazee@outlook.com>。远端操作使用具有 Kinakaze 相应权限的 GitHub 登录，凭据保存在仓库之外。

构建产物、用户数据、账号密钥、通知历史与抓包不进入 Git。当前 CI 只生成构建附件，不自动创建 Release。
