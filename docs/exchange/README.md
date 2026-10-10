# MiPush JSON

Windows 和 Android 共用 `mipush-desk / 1`，使用 UTF-8 `.json` 文件。粘贴、文件选择和拖放使用相同解析规则，不按来源猜测格式。

```json
{
  "schema": "mipush-desk",
  "version": 1,
  "exportedAt": "2026-10-09T12:00:00Z",
  "analysis": { "credentials": { "security": null } }
}
```

`schema`、`version` 必填，`exportedAt` 为带时区的 ISO 8601 时间。以下数据段至少提供一个；导出时省略没有的数据段。

| 数据段 | 内容与导入行为 |
| --- | --- |
| `account` | 完整通道账号；Windows 使用当前用户 DPAPI 保存，按本机自动连接设置重新连接 |
| `appCredentials` | 包名到 `{appId, regId, regSecret}` 的映射；Windows 单独用 DPAPI 加密，供各应用 AES 正文解密 |
| `settings` | 单套外观、应用规则、通知设置，内部版本为 `2` |
| `icons` | PNG/JPEG 图标，键为内容 SHA-256 小写十六进制文件名，值为 Base64 |
| `notifications` | 历史通知；按 `message_key` 去重，标为已读，不触发弹窗 |
| `analysis` | Android 抓包快照，包括凭据状态、Session、BIND 字段、事件和计数 |

只有 `account` 用于登录。`analysis.credentials` 中缺失的 `security` 保留为 `null`，Windows 显示“尚缺 security”，不会替换已有账号。Android 导入文件后保留其账号、应用密钥、设置、图标和通知；分享 Session 时将 `analysis` 更新为当前抓包快照。Android 不应用 Windows 外观设置。

## 账号

`account` 必须有非空字符串 `uuid`、`token`、`security`、`device_uuid`。`uuid` 为 `数字@xiaomi.com/资源`，数字部分在正 64 位有符号整数范围内；`security` 为 Base64，解码后 8～512 字节。可选 `client_attrs`、`cloud_attrs` 必须为字符串。其他协议字段原样保留。

普通 Session 抓包不携带 security。诊断工具只在日志 token 匹配、BIND 签名核验通过且账号唯一时生成 `account`。它保留原始 `analysis`，不会把分析快照伪装成已恢复凭据。

```powershell
python tools/recover_security.py session.json diagnostic.log --output account.json
```

`session.json` 使用 Android 的“分享 Session JSON”获得；同一命令也接受“保存抓包 ZIP”的文件，ZIP 路径会额外独立校验 PCAP。诊断日志仍需通过已验证的提取流程获得。

## 外观与 Logo

`settings.appearance` 包含 `theme`、`colors`、`layout`、`toastMode`、`toastStyle`、`useAppIcons`、`showImages`、`defaultImage`、`useMessageColors`、`playSound`、`fontSize`、`cornerRadius`。默认白色、无配图、关闭通知原色，没有方案名称、ID 或多方案列表。

`settings.apps` 以 Android 包名为键，值包含 `name`、可选 `icon`、`muted`。例如 `icon` 为 `<64 位 SHA-256>.png`，对应字节放在 `icons` 中。Windows 导出设置会一并嵌入用到的 Logo 和默认配图。`defaultImage` 可省略；开启配图后优先用消息图片，消息无图才用默认图，两者皆无则不显示图片。

`settings.libraryIcons` 独立保存包名到图标文件名的映射，`iconLibraryName` 为来源名称；`useBuiltInIcons`、`hiddenApps`、`useXiaomiMetadata` 控制内置图标、列表隐藏和可选商店查询。导入图标库不会把每个包名加入应用管理页。`reconnectSeconds` 默认 60，允许 5～86400 秒；`heartbeatSeconds` 默认 30，允许 5～300 秒。

`appCredentials` 最多 2048 个应用，`regSecret` 是 Base64 的 16、24 或 32 字节 AES 密钥；`appId`、`regId` 可省略。会话导出可包含已有的应用密钥，普通设置导出不包含此段。通道 security 不等于应用 regSecret。

桌面 1.4.0 收到缺少对应 appId 密钥的加密消息时，在已认证通道上按需获取，设置页可手动刷新；启动和重连本身不触发全量获取。服务端数字 app ID 无损转成字符串；恢复接口不返回 regId，新条目的 regId 留空。已有同一注册的 regId 会保留，成功取得的凭据仍按本格式导出，格式版本不变。

`useXiaomiMetadata` 默认 `true`。成功查询的名称与图标永久缓存，可手动更新、清除；图标库优先于小米缓存。内置压缩图标库随程序分发，不重复嵌入设置导出。

设置导入保留本机的自动连接、关闭到托盘、心跳间隔和注册表自启动项。其他设置按文件应用。本地设置与导入均使用 version 2，不再迁移旧版多方案格式。

## 通知与分析

通知使用接收器的字段名：`message_key`、`received_at`、`message_ts`、`package`、`title`、`description`、`url`、`extra`、`encrypted_action`、`raw_path`、`payload_text`、`is_notification`、`duplicate`。有效记录必须有标识、包名、接收时间，且 `is_notification=true`、`duplicate=false`。`extra` 是字符串到字符串的映射，包含配图和操作按钮等原始参数。

桌面 1.1 还保留 `action`、`action_name`、`app_id`、`message_id`、`body`、`body_status`、`metadata`、`notify_id`、`notify_type`、`pass_through`、`topic`、`collapse_key`、`expires_at`、`replayed` 及未知通知字段。正文状态包括 `decoded`、`encrypted`、`decrypt_failed`、`malformed`、`unsupported`，不能将外层摘要可读视为正文已解密。协议控制记录经 `analysis` 导出，不作为通知导入。

导入历史保存于独立文件，不写接收器正在追加的 JSONL。通知页“×”同时清除可见历史、导入历史与本应用系统提醒；用户再次明确导入时可恢复旧记录。

Android 将每个真实 Session 的 `device_uuid`、`challenge`、`packet_id`、`kick`、`client_attrs`、`cloud_attrs`、`signature` 连同 uuid/token 保存在 `analysis.sessions[].bind`。账号分开保存，不混用分身与自测身份；`source=phone_xmsf` 表示手机 MiPush。

## 限制与示例

桌面单文档最多 64 MiB，最多 10000 条通知、10000 个图标；单图最多 4 MiB。Windows 校验图标签名及内容哈希，再写入本地图标库。

Android 0.4.0 保留和转存 `appCredentials`、外观与图片数据，移除旧的 1000 图标限制。Android 仅应用分析数据，其余数据段在导入、分享时保持原样。

JSON 中的账号、应用密钥和通知内容是明文；分享、复制、导出均由用户主动点击。Windows 设置导出不包含账号或应用密钥。示例只有合成数据，无法登录真实账号：

- [Session 分析](examples/session.json)
- [白色外观与应用规则](examples/settings.json)
- [复杂通知](examples/notifications.json)

机器可读定义：[schema.json](schema.json)。
