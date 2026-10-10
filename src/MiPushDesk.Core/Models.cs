using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;

namespace MiPushDesk.Core;

public sealed class DisplayProfile
{
    public string Theme { get; set; } = "paper";
    public PaletteColors? Colors { get; set; }
    public string Layout { get; set; } = "cards";
    public string ToastMode { get; set; } = "native";
    public string ToastStyle { get; set; } = "rich";
    public bool UseAppIcons { get; set; } = true;
    public bool ShowImages { get; set; }
    public string? DefaultImage { get; set; }
    public bool UseMessageColors { get; set; }
    public bool PlaySound { get; set; }
    public double FontSize { get; set; } = 14;
    public double CornerRadius { get; set; } = 10;
}

public sealed class AppRule
{
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public bool Muted { get; set; }
}

public sealed class AppCredential
{
    public string AppId { get; set; } = "";
    public string RegId { get; set; } = "";
    public string RegSecret { get; set; } = "";
}

public sealed class DeskSettings
{
    [JsonRequired] public int Version { get; set; } = 2;
    public DisplayProfile Appearance { get; set; } = new();
    public Dictionary<string, AppRule> Apps { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> LibraryIcons { get; set; } = new(StringComparer.Ordinal);
    public string IconLibraryName { get; set; } = "";
    public bool UseBuiltInIcons { get; set; } = true;
    public HashSet<string> HiddenApps { get; set; } = new(StringComparer.Ordinal);
    public bool UseXiaomiMetadata { get; set; } = true;
    public bool AutoConnect { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    public bool QuietHoursEnabled { get; set; }
    public int QuietStartHour { get; set; } = 23;
    public int QuietEndHour { get; set; } = 8;
    public int HeartbeatSeconds { get; set; } = 30;
    public int ReconnectSeconds { get; set; } = 60;

    public bool ShouldNotify(string package, DateTimeOffset now)
    {
        if (!NotificationsEnabled || (Apps.TryGetValue(package, out var rule) && rule.Muted)) return false;
        if (!QuietHoursEnabled) return true;
        var hour = now.LocalDateTime.Hour;
        return QuietStartHour < QuietEndHour ? hour < QuietStartHour || hour >= QuietEndHour
            : QuietStartHour != QuietEndHour && hour < QuietStartHour && hour >= QuietEndHour;
    }
}

public sealed class PushRecord
{
    [JsonPropertyName("message_key")] public string Key { get; set; } = "";
    [JsonPropertyName("received_at")] public DateTimeOffset ReceivedAt { get; set; }
    [JsonPropertyName("message_ts")] public long? MessageTimestamp { get; set; }
    [JsonPropertyName("package")] public string Package { get; set; } = "";
    [JsonPropertyName("app_id")] public string AppId { get; set; } = "";
    [JsonPropertyName("message_id")] public string MessageId { get; set; } = "";
    [JsonPropertyName("action")] public int Action { get; set; }
    [JsonPropertyName("action_name")] public string ActionName { get; set; } = "";
    [JsonPropertyName("is_request")] public bool IsRequest { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("extra")] public Dictionary<string, string> Extra { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("encrypted_action")] public bool EncryptedAction { get; set; }
    [JsonPropertyName("body_status")] public string BodyStatus { get; set; } = "";
    [JsonPropertyName("body")] public Dictionary<string, object?>? Body { get; set; }
    [JsonPropertyName("metadata")] public Dictionary<string, object?>? Metadata { get; set; }
    [JsonPropertyName("notify_id")] public int? NotifyId { get; set; }
    [JsonPropertyName("notify_type")] public int? NotifyType { get; set; }
    [JsonPropertyName("pass_through")] public int PassThrough { get; set; }
    [JsonPropertyName("topic")] public string Topic { get; set; } = "";
    [JsonPropertyName("collapse_key")] public string CollapseKey { get; set; } = "";
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; set; }
    [JsonPropertyName("operation")] public string Operation { get; set; } = "";
    [JsonPropertyName("control_type")] public string ControlType { get; set; } = "";
    [JsonPropertyName("control_extra")] public Dictionary<string, string> ControlExtra { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("is_message")] public bool IsMessage { get; set; }
    [JsonPropertyName("replayed")] public bool Replayed { get; set; }
    [JsonPropertyName("raw_path")] public string RawPath { get; set; } = "";
    [JsonPropertyName("payload_text")] public string? PayloadText { get; set; }
    [JsonPropertyName("is_notification")] public bool IsNotification { get; set; }
    [JsonPropertyName("duplicate")] public bool Duplicate { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
    public string ExtraValue(string name) => Extra.GetValueOrDefault(name, "");
}

public sealed class ListenerState
{
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "stopped";
    [JsonPropertyName("bound")] public bool Bound { get; set; }
    [JsonPropertyName("heartbeat_confirmed")] public bool HeartbeatConfirmed { get; set; }
    [JsonPropertyName("pongs_received")] public int PongsReceived { get; set; }
    [JsonPropertyName("acks_sent")] public int AcksSent { get; set; }
    [JsonPropertyName("decode_errors")] public int DecodeErrors { get; set; }
    [JsonPropertyName("last_error")] public string? LastError { get; set; }
    [JsonPropertyName("error_type")] public string? ErrorType { get; set; }
    [JsonPropertyName("error_reason")] public string? ErrorReason { get; set; }
    [JsonPropertyName("heartbeat_seconds")] public double HeartbeatSeconds { get; set; }
    [JsonPropertyName("reconnect_seconds")] public double ReconnectSeconds { get; set; } = 60;
    [JsonPropertyName("retry_at")] public DateTimeOffset? RetryAt { get; set; }
    [JsonPropertyName("connection_attempts")] public int ConnectionAttempts { get; set; }
    [JsonPropertyName("connections_bound")] public int ConnectionsBound { get; set; }
    [JsonPropertyName("secmsg_received")] public int SecmsgReceived { get; set; }
    [JsonPropertyName("notifications_received")] public int NotificationsReceived { get; set; }
    [JsonPropertyName("messages_received")] public int MessagesReceived { get; set; }
    [JsonPropertyName("duplicates")] public int Duplicates { get; set; }
    [JsonPropertyName("pings_sent")] public int PingsSent { get; set; }
    [JsonPropertyName("last_message_at")] public DateTimeOffset? LastMessage { get; set; }
    [JsonPropertyName("last_pong_at")] public DateTimeOffset? LastPong { get; set; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
    [JsonPropertyName("app_credentials_count")] public int AppCredentialsCount { get; set; }
    [JsonPropertyName("app_secret_recovery_state")] public string AppSecretRecoveryState { get; set; } = "idle";
    [JsonPropertyName("app_secret_recovery_pages")] public int AppSecretRecoveryPages { get; set; }
    [JsonPropertyName("app_secrets_recovered")] public int AppSecretsRecovered { get; set; }
    [JsonPropertyName("app_secret_recovery_error")] public string? AppSecretRecoveryError { get; set; }
    [JsonPropertyName("app_secrets_updated_at")] public DateTimeOffset? AppSecretsUpdatedAt { get; set; }
    [JsonPropertyName("bodies_reprocessed")] public int BodiesReprocessed { get; set; }
}

public static class AppCatalog
{
    public static readonly Dictionary<string, (string Name, string Mark, string Color)> Known = new(StringComparer.Ordinal)
    {
        ["com.tencent.mobileqq"] = ("QQ", "QQ", "#5C9DFF"),
        ["com.tencent.mm"] = ("微信", "微", "#57BB80"),
        ["ctrip.android.view"] = ("携程旅行", "程", "#66AAEC"),
        ["com.dragon.read"] = ("番茄小说", "阅", "#EE8767"),
        ["com.xs.fm"] = ("番茄畅听", "听", "#EC9862"),
        ["com.ss.android.ugc.livelite"] = ("抖音极速版", "抖", "#D3A1D5"),
        ["com.ss.android.ugc.aweme"] = ("抖音", "抖", "#D3A1D5"),
        ["com.xiaomi.vipaccount"] = ("小米社区", "米", "#EFA660"),
        ["tv.danmaku.bili"] = ("哔哩哔哩", "哔", "#EE9BB6"),
        ["com.eg.android.AlipayGphone"] = ("支付宝", "支", "#408DDC"),
        ["com.taobao.taobao"] = ("淘宝", "淘", "#EE8B63"),
        ["com.sina.weibo"] = ("微博", "微", "#E78371"),
        ["com.netease.cloudmusic"] = ("网易云音乐", "音", "#DE747E"),
    };
    public static string Name(string package, DeskSettings settings, string? cachedName = null) => settings.Apps.TryGetValue(package, out var rule)
        && !string.IsNullOrWhiteSpace(rule.Name) ? rule.Name : !string.IsNullOrWhiteSpace(cachedName) ? cachedName : Known.GetValueOrDefault(package).Name ?? package;
    public static IEnumerable<string> Packages(DeskSettings settings) => Known.Keys.Concat(settings.Apps.Keys)
        .Distinct(StringComparer.Ordinal).Where(package => !settings.HiddenApps.Contains(package));
    public static (string Mark, string Color) Badge(string package)
    {
        if (Known.TryGetValue(package, out var info)) return (info.Mark, info.Color);
        var part = package.Split('.').LastOrDefault() ?? "M";
        return (part.Length > 0 ? part[..1].ToUpperInvariant() : "M", "#A79BC9");
    }
}

public static class JsonData
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true, MaxDepth = 128
    };
}
