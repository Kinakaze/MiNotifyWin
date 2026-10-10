using System.Text.Json;

namespace MiPushDesk.Core.Protocol;

public sealed record AppSecretRecoveryPage(string Offset, Dictionary<string, AppCredential> Credentials, long? ErrorCode);

public static class AppSecretRecovery
{
    public const string Package = "com.xiaomi.xmsf";
    public const string AppId = "1000271";
    public const string ResponseType = "push_data_recover_ack";
    public const int MaxPages = 100;
    public const int MaxApplications = 2048;

    public static byte[] Request(ChannelAccount account, string requestId, string offset) => MiPushMessage.Send(
        account, Package, AppId, 9, true, new()
        {
            [3] = ThriftValue.Text(requestId), [5] = ThriftValue.Text("push_data_recover"),
            [6] = ThriftValue.Bool(true), [8] = ThriftValue.Strings(new Dictionary<string, string> { ["offset"] = offset })
        });

    public static bool IsResponse(PushRecord record) => record.Action == 9 && record.ControlType == ResponseType;

    public static AppSecretRecoveryPage Read(DecodedPush message)
    {
        var record = message.Record;
        if (!IsResponse(record) || record.IsRequest || record.EncryptedAction || record.BodyStatus != "decoded"
            || record.Package != Package || record.AppId != AppId)
            throw new PushProtocolException("Unexpected application recovery response.");
        var errorCode = message.Body.Int64(7);
        if (errorCode is not null and not 0) return new("", new(StringComparer.Ordinal), errorCode);
        var extra = message.Body.Strings(9);
        if (!extra.TryGetValue("offset", out var offset) || offset.Length is 0 or > 4096
            || !extra.TryGetValue("data", out var data))
            throw new PushProtocolException("Application recovery response has no data or offset.");
        using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > MaxApplications)
            throw new PushProtocolException("Invalid application recovery list.");
        var credentials = new Dictionary<string, AppCredential>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("package_name", out var package) || package.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("app_id", out var appId)
                || !item.TryGetProperty("secret", out var secret) || secret.ValueKind != JsonValueKind.String)
                throw new PushProtocolException("Invalid application recovery entry.");
            var appIdText = appId.ValueKind == JsonValueKind.String ? appId.GetString()!
                : appId.ValueKind == JsonValueKind.Number && appId.TryGetInt64(out var number) && number > 0
                    ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            if (appIdText.Length == 0) throw new PushProtocolException("Application recovery entry has no app ID.");
            var credential = new AppCredential { AppId = appIdText, RegSecret = secret.GetString()! };
            var packageName = package.GetString()!;
            if (credentials.TryGetValue(packageName, out var previous)
                && (previous.AppId != credential.AppId || previous.RegSecret != credential.RegSecret))
                throw new PushProtocolException("Conflicting application recovery entries.");
            credentials[packageName] = credential;
        }
        AppStore.ValidateAppCredentials(credentials);
        return new(offset, credentials, errorCode);
    }
}
