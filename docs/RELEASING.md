# 发布

公开仓库为 Kinakaze/MiNotifyWin。先完成本地构建、检查与打包，再提交对应源码、推送版本标签并创建 Release。

在交互式 Windows 桌面运行核心、界面与原生检查，再执行 scripts/Package.ps1。版本来自 src/MiPushDesk/MiPushDesk.csproj，标签为 v1.4.0；上传 artifacts/releases/1.4.0/ 内的 ZIP、SHA256SUMS.txt 和 RELEASE-NOTES.md。

提交署名为 mitsukina <mitsukazee@outlook.com>。远端操作使用具有 Kinakaze 相应权限的 GitHub 登录，凭据保存在仓库之外。

构建产物、用户数据、账号密钥、通知历史与抓包不进入 Git。当前 CI 只生成构建附件，不自动创建 Release。
