using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MiPushDesk.Core.Protocol;

public sealed class ChannelAccount
{
    public required string Uuid { get; init; }
    public required string Token { get; init; }
    public required string Security { get; init; }
    public required string DeviceUuid { get; init; }
    public string ClientAttributes { get; init; } = "";
    public string CloudAttributes { get; init; } = "";
    public string User => Uuid.Split('@')[0];
    public string Server => Uuid.Split('@')[1].Split('/')[0];
    public string Resource => Uuid.Split('/')[1];
    public static ChannelAccount Parse(string json)
    {
        using var document = JsonDocument.Parse(AppStore.ValidateAccount(json));
        var root = document.RootElement;
        return new()
        {
            Uuid = root.GetProperty("uuid").GetString()!, Token = root.GetProperty("token").GetString()!,
            Security = root.GetProperty("security").GetString()!, DeviceUuid = root.GetProperty("device_uuid").GetString()!,
            ClientAttributes = root.TryGetProperty("client_attrs", out var client) ? client.GetString()! : "",
            CloudAttributes = root.TryGetProperty("cloud_attrs", out var cloud) ? cloud.GetString()! : ""
        };
    }
    public string Redact(string value)
    {
        foreach (var secret in new[] { Uuid, Token, Security, DeviceUuid }) value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return value[..Math.Min(value.Length, 1024)];
    }
}

public sealed record DecodedPush(ThriftFields Envelope, ThriftFields Body, ThriftFields Meta, PushRecord Record,
    bool Acknowledge, AppCredential? Registration = null);

public static class MiPushMessage
{
    public static readonly byte[] AppIv = [100, 23, 84, 114, 72, 0, 4, 97, 73, 97, 2, 52, 84, 102, 18, 32];
    public static readonly IReadOnlyDictionary<int, string> Actions = new Dictionary<int, string>
    {
        [1] = "Registration", [2] = "UnRegistration", [3] = "Subscription", [4] = "UnSubscription",
        [5] = "SendMessage", [6] = "AckMessage", [7] = "SetConfig", [8] = "ReportFeedback", [9] = "Notification",
        [10] = "Command", [11] = "MultiConnectionBroadcast", [12] = "MultiConnectionResult", [13] = "ConnectionKick",
        [14] = "ApnsMessage", [15] = "IOSDeviceTokenWrite", [16] = "SaveInvalidRegId", [17] = "ApnsCertChanged",
        [18] = "RegisterDevice", [19] = "ExpandTopicInXmq", [22] = "SendMessageNew", [23] = "ExpandTopicInXmqNew",
        [24] = "DeleteInvalidMessage", [99] = "BadAction", [100] = "Presence", [101] = "FetchOfflineMessage",
        [102] = "SaveJob", [103] = "Broadcast", [104] = "BatchPresence", [105] = "BatchMessage", [107] = "StatCounter",
        [108] = "FetchTopicMessage", [109] = "DeleteAliasCache", [110] = "UpdateRegistration",
        [112] = "BatchMessageNew", [113] = "PublicWelfareMessage", [114] = "RevokeMessage", [200] = "SimulatorJob"
    };
    private static readonly Dictionary<short, string> TargetNames = Names("1:channelId 2:userId 3:server 4:resource 5:isPreview 7:token");
    private static readonly Dictionary<short, string> MetaNames = Names("1:id 2:messageTs 3:topic 4:title 5:description 6:notifyType 7:url 8:passThrough 9:notifyId 10:extra 11:internal 12:ignoreRegInfo 13:apsProperFields");
    private static readonly Dictionary<short, string> MessageNames = Names("1:to 2:id 3:appId 4:payload 5:createAt 6:ttl 7:collapseKey 8:packageName 9:regId 10:category 11:topic 12:metaInfo 13:aliasName 14:isOnline 15:userAccount 16:miid 20:imeiMd5 21:deviceId");
    private static readonly Dictionary<short, string> SendNames = Names("1:debug 2:target 3:id 4:appId 5:packageName 6:topic 7:aliasName 8:message 9:needAck 10:params 11:category 12:userAccount");
    private static readonly Dictionary<short, string> SubscriptionNames = Names("1:debug 2:target 3:id 4:appId 6:errorCode 7:reason 8:topic 9:packageName 10:category");
    private static readonly Dictionary<short, string> CommandNames = Names("2:target 3:id 4:appId 5:cmdName 7:errorCode 8:reason 9:packageName 10:cmdArgs 12:category 13:response2Client");
    private static readonly Dictionary<short, string> NotificationAckNames = Names("1:debug 2:target 3:id 4:appId 5:type 7:errorCode 8:reason 9:extra 10:packageName 11:category");
    private static readonly Dictionary<int, Dictionary<short, string>> BodyNames = new()
    {
        [1] = Names("1:debug 2:target 3:id 4:appId 6:errorCode 7:reason 8:regId 9:regSecret 10:packageName 11:registeredAt 12:aliasName 13:clientId 14:costTime 15:appVersion 16:pushSdkVersionCode 17:hybridPushEndpoint 18:appVersionCode 19:region 20:isHybridFrame 21:autoMarkPkgs"),
        [2] = Names("1:debug 2:target 3:id 4:appId 6:errorCode 7:reason 8:packageName 9:unRegisteredAt 10:costTime"),
        [3] = SubscriptionNames, [4] = SubscriptionNames, [5] = SendNames,
        [6] = Names("1:debug 2:target 3:id 4:appId 5:messageTs 6:topic 7:aliasName 8:request 9:packageName 10:category 11:isOnline 12:regId 13:callbackUrl 14:userAccount 15:deviceStatus 16:geoMsgStatus 20:imeiMd5 21:deviceId 22:passThrough 23:extra"),
        [7] = CommandNames, [8] = Names("1:debug 2:target 3:id 4:appId 6:errorCode 7:reason 8:category"),
        [9] = Names("1:debug 2:target 3:id 4:appId 5:type 6:requireAck 7:payload 8:extra 9:packageName 10:category 12:regId 13:aliasName 14:binaryExtra 15:createdTs 20:alreadyLogClickInXmq"),
        [10] = CommandNames
    };
    private static Dictionary<short, string> Names(string fields) => fields.Split(' ').Select(field => field.Split(':'))
        .ToDictionary(field => short.Parse(field[0], System.Globalization.CultureInfo.InvariantCulture), field => field[1]);
    public static byte[] Bind(ChannelAccount account, string challenge, string? packetId = null)
    {
        packetId ??= "win-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var signature = BindSignature(account, challenge, packetId, "0");
        var header = SlimProtocol.Protobuf((1, 5), (2, ulong.Parse(account.User)), (3, account.Server), (4, account.Resource),
            (5, "BIND"), (7, packetId), (9, 0));
        var payload = SlimProtocol.Protobuf((1, account.Token), (2, "0"), (3, "XMPUSH-PASS"),
            (4, account.ClientAttributes), (5, account.CloudAttributes), (6, signature));
        return SlimProtocol.Blob(2, header, payload);
    }
    public static string BindSignature(ChannelAccount account, string challenge, string packetId, string kick)
    {
        var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["challenge"] = challenge, ["chid"] = "5", ["client_attrs"] = account.ClientAttributes,
            ["cloud_attrs"] = account.CloudAttributes, ["from"] = account.Uuid, ["id"] = packetId,
            ["kick"] = kick, ["to"] = "xiaomi.com", ["token"] = account.Token
        };
        var signed = "XMPUSH-PASS&" + string.Join('&', attributes.Select(pair => pair.Key + "=" + pair.Value)) + "&" + account.Security;
        return Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(signed)));
    }
    public static byte[] SessionKey(string challenge, string deviceUuid)
    {
        if (challenge.Length is 0 or > 1024 || !challenge.All(char.IsAscii)) throw new PushProtocolException("Invalid CONN challenge.");
        return SlimProtocol.Rc4(Encoding.UTF8.GetBytes(challenge), Encoding.UTF8.GetBytes(challenge[(challenge.Length / 2)..] + deviceUuid[(deviceUuid.Length / 2)..]));
    }
    private static byte[] ChannelKey(string security, string packetId) => [.. Convert.FromBase64String(security), .. Encoding.UTF8.GetBytes("_" + packetId)];
    public static byte[] ChannelPayload(SlimBlob blob, string security)
    {
        if (blob.Header.Number(9) == 0) return blob.Payload;
        if (blob.Header.Number(9) != 1 || blob.PacketId is "" or "ID_NOT_AVAILABLE") throw new PushProtocolException("Invalid SECMSG cipher or packet ID.");
        var key = ChannelKey(security, blob.PacketId);
        try { return SlimProtocol.Unpack(SlimProtocol.Rc4(key, blob.Payload)); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public static byte[] CryptBody(byte[] body, string regSecret, bool encrypt = false)
    {
        var key = Convert.FromBase64String(regSecret);
        try
        {
            using var aes = Aes.Create(); aes.Key = key;
            return encrypt ? aes.EncryptCbc(body, AppIv, PaddingMode.PKCS7) : aes.DecryptCbc(body, AppIv, PaddingMode.PKCS7);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public static DecodedPush Decode(byte[] payload, IReadOnlyDictionary<string, AppCredential>? credentials = null)
    {
        var envelope = ThriftProtocol.Decode(payload);
        foreach (var (number, type) in new (short, ThriftType)[] { (1, ThriftType.I32), (2, ThriftType.Bool), (3, ThriftType.Bool), (4, ThriftType.Binary), (7, ThriftType.Struct) })
            if (envelope.Get(number, type) is null) throw new PushProtocolException($"Missing push field {number}.");
        var record = new PushRecord
        {
            Action = envelope.Int32(1)!.Value, EncryptedAction = envelope.Bool(2)!.Value, IsRequest = envelope.Bool(3)!.Value,
            AppId = envelope.Text(5), Package = envelope.Text(6), ReceivedAt = DateTimeOffset.UtcNow,
            BodyStatus = envelope.Bool(2) == true ? "encrypted" : "unparsed"
        };
        record.ActionName = Actions.GetValueOrDefault(record.Action, "Unknown");
        var meta = envelope.Struct(8);
        var body = new ThriftFields();
        var bodyBytes = envelope.Binary(4);
        var canDecode = !record.EncryptedAction;
        var needAck = true;
        AppCredential? registration = null;
        if (record.EncryptedAction && credentials?.GetValueOrDefault(record.Package) is { } credential
            && (credential.AppId.Length == 0 || credential.AppId == record.AppId))
        {
            try { bodyBytes = CryptBody(bodyBytes, credential.RegSecret); canDecode = true; }
            catch (Exception error) when (error is CryptographicException or FormatException) { record.BodyStatus = "decrypt_failed"; }
        }
        if (canDecode)
        {
            try
            {
                body = ThriftProtocol.Decode(bodyBytes);
                var names = record.Action == 9 && !record.IsRequest ? NotificationAckNames : BodyNames.GetValueOrDefault(record.Action);
                record.Body = Project(body, names);
                record.BodyStatus = names is null ? "unsupported" : "decoded";
                if (record.Action == 5)
                {
                    needAck = body.Bool(9) ?? true;
                    var inner = body.Struct(8);
                    var innerMeta = inner.Struct(12);
                    foreach (var (number, field) in meta) innerMeta[number] = field;
                    meta = innerMeta;
                    record.PayloadText = inner.Text(4);
                    record.CollapseKey = inner.Text(7);
                    if (record.Package.Length == 0) record.Package = body.Text(5);
                    if (record.AppId.Length == 0) record.AppId = body.Text(4);
                    if (!meta.ContainsKey(1)) meta[1] = ThriftValue.Text(body.Text(3));
                    if (!meta.ContainsKey(2) && inner.Int64(5) is { } created) meta[2] = ThriftValue.Int64(created);
                    if (!meta.ContainsKey(3) && body.Text(6).Length > 0) meta[3] = ThriftValue.Text(body.Text(6));
                    if (inner.Int64(5) is { } start && inner.Int64(6) is > 0 and var ttl
                        && start >= 0 && ttl <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() - start)
                        record.ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(start + ttl);
                }
                else if (record.Action == 9)
                {
                    record.ControlType = body.Text(5);
                    if (record.Package.Length == 0) record.Package = body.Text(record.IsRequest ? (short)9 : (short)10);
                    record.ControlExtra = body.Strings(record.IsRequest ? (short)8 : (short)9);
                    if (record.IsRequest)
                    {
                        record.PayloadText = body.Text(7);
                        if (record.ControlType == "clear_push_message" && HasClearSelector(record.ControlExtra)) record.Operation = "clear";
                    }
                    if (record.ControlType == AppSecretRecovery.ResponseType)
                    {
                        record.ControlExtra.Clear(); record.PayloadText = null;
                        record.Body = new() { ["id"] = body.Text(3), ["type"] = record.ControlType, ["extra"] = "[protected]" };
                    }
                }
                else if (record.Action == 1 && body.Int64(6) == 0 && body.Text(9).Length > 0)
                {
                    if (record.Package.Length == 0) record.Package = body.Text(10);
                    registration = new() { AppId = body.Text(4), RegId = body.Text(8), RegSecret = body.Text(9) };
                    AppStore.ValidateAppCredentials(new() { [record.Package] = registration });
                }
            }
            catch (Exception error) when (error is PushProtocolException or InvalidDataException)
            {
                record.BodyStatus = "malformed"; needAck = false; registration = null;
                record.Body = null; record.Operation = "";
            }
        }
        ApplyMetadata(record, meta);
        record.IsMessage = record.Action is 5 or 22;
        record.IsNotification = record.IsMessage && record.PassThrough != 1 && (record.Title.Length > 0 || record.Description.Length > 0);
        var identity = string.Join('\0', record.Package, record.AppId, record.Action.ToString(System.Globalization.CultureInfo.InvariantCulture), record.MessageId);
        record.Key = Convert.ToHexString(SHA256.HashData(record.MessageId.Length > 0 ? Encoding.UTF8.GetBytes(identity) : payload)).ToLowerInvariant();
        if (record.Extra.TryGetValue("timeout", out var timeoutText) && int.TryParse(timeoutText, out var seconds) && seconds > 0)
        {
            var expiration = record.ReceivedAt.AddSeconds(seconds);
            if (record.ExpiresAt is null || expiration < record.ExpiresAt) record.ExpiresAt = expiration;
        }
        var acknowledge = record.Action == 5 && needAck && record.MessageId.Length > 0 && record.AppId.Length > 0
            && record.Package.Length > 0 && record.MessageTimestamp is not null;
        return new(envelope, body, meta, record, acknowledge, registration);
    }
    private static bool HasClearSelector(Dictionary<string, string> extra) =>
        extra.TryGetValue("notifyId", out var value) && int.TryParse(value, out var number) && number >= -1
        || extra.GetValueOrDefault("msgId", "").Length > 0
        || extra.GetValueOrDefault("title", "").Length > 0 || extra.GetValueOrDefault("description", "").Length > 0;
    private static void ApplyMetadata(PushRecord record, ThriftFields meta)
    {
        record.MessageId = meta.Text(1); record.MessageTimestamp = meta.Int64(2); record.Topic = meta.Text(3);
        record.Title = meta.Text(4); record.Description = meta.Text(5); record.NotifyType = meta.Int32(6);
        record.Url = meta.Text(7); record.PassThrough = meta.Int32(8) ?? 0; record.NotifyId = meta.Int32(9);
        record.Extra = meta.Strings(10); record.Metadata = Project(meta, MetaNames);
    }
    public static Dictionary<string, object?> Project(ThriftFields fields, Dictionary<short, string>? names = null)
    {
        var result = new Dictionary<string, object?>();
        foreach (var (number, field) in fields)
        {
            var name = names?.GetValueOrDefault(number) ?? "field_" + number;
            if (name is "regSecret" or "token") { result[name] = "[protected]"; continue; }
            var nested = name switch { "target" or "to" => TargetNames, "message" => MessageNames, "metaInfo" => MetaNames, "request" => SendNames, _ => null };
            var projected = ProjectValue(field.Type, field.Value, nested);
            result[name] = names?.ContainsKey(number) == true ? projected : new { type = (byte)field.Type, value = projected };
        }
        return result;
    }
    private static object? ProjectValue(ThriftType type, object value, Dictionary<short, string>? names = null)
    {
        if (type == ThriftType.Binary)
        {
            var bytes = (byte[])value;
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { return new { base64 = Convert.ToBase64String(bytes) }; }
        }
        if (type == ThriftType.Struct) return Project((ThriftFields)value, names);
        if (type == ThriftType.Map)
        {
            var map = (ThriftMap)value;
            return new { keyType = (byte)map.KeyType, valueType = (byte)map.ValueType,
                entries = map.Entries.Select(entry => new[] { ProjectValue(map.KeyType, entry.Key), ProjectValue(map.ValueType, entry.Value) }).ToArray() };
        }
        if (type is ThriftType.Set or ThriftType.List)
        {
            var list = (ThriftList)value;
            return new { itemType = (byte)list.ItemType, items = list.Items.Select(item => ProjectValue(list.ItemType, item)).ToArray() };
        }
        if (value is double number && !double.IsFinite(number)) return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return value;
    }
    public static byte[] Ack(DecodedPush message, ChannelAccount account, long receivedMilliseconds)
    {
        if (!message.Acknowledge) throw new PushProtocolException("Message does not require acknowledgement.");
        var meta = ThriftProtocol.Decode(ThriftProtocol.Encode(message.Meta));
        if (meta.ContainsKey(11)) { var internalMap = meta.Strings(11); internalMap.Remove("score_info"); meta[11] = ThriftValue.Strings(internalMap); }
        var extra = meta.Strings(10);
        extra["mrt"] = extra["mat"] = receivedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        meta[10] = ThriftValue.Strings(extra);
        var record = message.Record;
        var ack = new ThriftFields { [3] = ThriftValue.Text(record.MessageId), [4] = ThriftValue.Text(record.AppId), [5] = ThriftValue.Int64(record.MessageTimestamp!.Value) };
        if (record.Topic.Length > 0) ack[6] = ThriftValue.Text(record.Topic);
        return Send(account, record.Package, record.AppId, 6, true, ack, meta);
    }
    public static byte[] ClearAck(DecodedPush message, ChannelAccount account)
    {
        var body = new ThriftFields
        {
            [3] = ThriftValue.Text(message.Body.Text(3)), [4] = ThriftValue.Text(message.Record.AppId),
            [5] = ThriftValue.Text("clear_push_message_ack"), [7] = ThriftValue.Int64(0),
            [8] = ThriftValue.Text("success clear push message."), [10] = ThriftValue.Text(message.Record.Package)
        };
        if (message.Body.ContainsKey(2)) body[2] = message.Body[2];
        return Send(account, message.Record.Package, message.Record.AppId, 9, false, body);
    }
    public static byte[] Send(ChannelAccount account, string package, string appId, int action, bool isRequest,
        ThriftFields body, ThriftFields? meta = null)
    {
        var target = new ThriftFields { [1] = ThriftValue.Int64(5), [2] = ThriftValue.Text(account.User), [3] = ThriftValue.Text(account.Server), [4] = ThriftValue.Text(account.Resource) };
        var outgoing = new ThriftFields
        {
            [1] = ThriftValue.Int32(action), [2] = ThriftValue.Bool(false), [3] = ThriftValue.Bool(isRequest),
            [4] = ThriftValue.Binary(ThriftProtocol.Encode(body)), [5] = ThriftValue.Text(appId),
            [6] = ThriftValue.Text(package), [7] = ThriftValue.Struct(target)
        };
        if (meta is not null) outgoing[8] = ThriftValue.Struct(meta);
        var packetId = "win-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var header = SlimProtocol.Protobuf((1, 5), (2, ulong.Parse(account.User)), (3, account.Server), (4, account.Resource),
            (5, "SECMSG"), (6, "message"), (7, packetId), (9, 1));
        var key = ChannelKey(account.Security, packetId);
        try { return SlimProtocol.Blob(1, header, SlimProtocol.Rc4(key, ThriftProtocol.Encode(outgoing))); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
