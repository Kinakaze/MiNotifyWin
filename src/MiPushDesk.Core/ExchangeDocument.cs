using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiPushDesk.Core;

public sealed class ExchangeDocument
{
    [JsonRequired] public string Schema { get; set; } = "mipush-desk";
    [JsonRequired] public int Version { get; set; } = 1;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public JsonElement? Account { get; set; }
    public Dictionary<string, AppCredential>? AppCredentials { get; set; }
    public DeskSettings? Settings { get; set; }
    public Dictionary<string, string>? Icons { get; set; }
    public List<PushRecord>? Notifications { get; set; }
    public JsonElement? Analysis { get; set; }

    private static readonly JsonSerializerOptions Options = new(JsonData.Options)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ExchangeDocument Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 64 * 1024 * 1024) throw new InvalidDataException("JSON 超过 64 MiB。");
        var document = JsonSerializer.Deserialize<ExchangeDocument>(json, Options) ?? throw new InvalidDataException("JSON 为空。");
        if (document.Schema != "mipush-desk" || document.Version != 1) throw new InvalidDataException("需要 mipush-desk / 1 格式。");
        if (document.Account is null && document.AppCredentials is null && document.Settings is null && document.Icons is null && document.Notifications is null && document.Analysis is null)
            throw new InvalidDataException("JSON 没有可用数据。");
        if (document.Account is { } account) AppStore.ValidateAccount(account.GetRawText());
        if (document.AppCredentials is { } credentials) AppStore.ValidateAppCredentials(credentials);
        if (document.Settings is { } settings) AppStore.Validate(settings);
        if (document.Analysis is { ValueKind: not JsonValueKind.Object }) throw new InvalidDataException("analysis 必须是对象。");
        if (document.Notifications is { } records)
        {
            if (records.Count > 10000) throw new InvalidDataException("最多导入 10000 条通知。");
            foreach (var record in records)
                if (record is null || string.IsNullOrEmpty(record.Key) || record.Package is null || !AppStore.ValidPackage(record.Package)
                    || record.Title is null || record.Description is null || record.Extra is null || !record.IsNotification
                    || record.Duplicate || record.ReceivedAt == default || record.Extra.Values.Any(value => value is null))
                    throw new InvalidDataException("通知字段不完整。");
        }
        if (document.Icons is { } icons)
        {
            if (icons.Count > 10000) throw new InvalidDataException("最多导入 10000 个图标。");
            foreach (var (name, data) in icons)
            {
                var bytes = Convert.FromBase64String(data);
                if (bytes.Length > 4 * 1024 * 1024 || ImportService.DetectImage(bytes) is not { } extension
                    || name != ImportService.IconName(bytes, extension)) throw new InvalidDataException("图标名称或内容无效。");
            }
        }
        return document;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public string Summary()
    {
        var parts = new List<string>();
        if (Account is not null) parts.Add("账号完整");
        if (AppCredentials is not null) parts.Add($"{AppCredentials.Count} 个应用密钥");
        if (Settings is not null) parts.Add("设置");
        if (Icons is not null) parts.Add($"{Icons.Count} 个图标");
        if (Notifications is not null) parts.Add($"{Notifications.Count} 条通知");
        if (Analysis is { } analysis)
        {
            parts.Add("抓包分析");
            if (Account is null && analysis.TryGetProperty("credentials", out var credentials)
                && credentials.ValueKind == JsonValueKind.Object
                && (!credentials.TryGetProperty("security", out var security) || security.ValueKind == JsonValueKind.Null))
                parts.Add("尚缺 security");
        }
        return string.Join(" · ", parts);
    }
}
