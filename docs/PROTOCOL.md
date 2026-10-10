# 协议与数据

接收、心跳、回执、消息解析和应用密钥获取均由 C# 在应用进程内完成。退出会关闭连接，没有独立 Python 后台。

## 解密需要什么

网络帧先使用设备握手得到的会话信息解析 Slim 通道；SECMSG 还需要通道账号的 security。内层 pushAction 的 AES 正文使用应用自己的 regSecret。appid 用来定位注册应用，regId 标识安装注册，它们本身不是 AES 密钥。

只解密已经提取的 pushAction，需要该段密文字节和对应 regSecret。若手里是完整抓包，还需完整握手与通道解密材料。通道 security 不能替代应用 regSecret；业务 payload 若再次加密，还需要业务自己的密钥。

接收器在收到带 appId 且缺少对应密钥的加密消息后，复用已认证连接发送 push_data_recover。获取中的请求合并，按服务端 offset 分页；完整验证后保存到 app-credentials.dpapi。已有 appId 解密失败不触发自动更新，手动更新始终保留。服务端未返回某个应用时，后续该应用的新消息可以再次触发。

服务端是否返回某应用取决于通道身份和已有注册信息。获取失败保留当前密钥，恢复完成后重解未删除、未清空的历史正文，不重复弹窗。成功的应用注册回包也会保存对应凭据。

## 接收行为

消息落盘后发送送达回执，写入失败不回执。按消息 ID 去重，同应用 notify ID 或 collapse key 可以替换旧消息；支持过期与 clear_push_message 撤回。已读只在本机保存，不上报点击。

外层标题可读不代表正文已解密。JSON 的 body_status 区分 decoded、encrypted、decrypt_failed、malformed 和 unsupported。未知动作与字段保留，Android Intent、RemoteViews 和业务执行逻辑不在 Windows 执行。

KICK 与认证拒绝按用户设置重连，默认 60 秒，没有额外 300 秒等待。wait/internal-error 不能单独证明原因；同一 UUID 和 resource 被手机与桌面同时使用可能导致服务端会话冲突。

## 本地文件

默认目录 %LOCALAPPDATA%\MiPushDesk，可用 MIPUSHDESK_DATA_DIR 指定独立目录。

| 文件 | 内容 |
| --- | --- |
| settings.json | 当前外观、应用规则与窗口偏好 |
| account.dpapi | 当前 Windows 用户加密的通道账号 |
| app-credentials.dpapi | 当前 Windows 用户加密的应用密钥 |
| listener/notifications.jsonl | 实时通知历史 |
| listener/notifications.jsonl.deleted、.cleared | 删除与清空状态 |
| imported-notifications.json | 用户导入的历史通知 |
| listener/status.json | 接收、心跳、回执与密钥更新状态 |
| listener/messages.jsonl、listener/raw | 消息解析记录与原始数据 |
| icons、images | 手动图片与消息图片缓存 |

手动 JSON 导出是明文，普通设置导出不包含账号或应用密钥。图标和默认配图按内容哈希嵌入设置导出文件，导入后可以在另一台电脑使用。
