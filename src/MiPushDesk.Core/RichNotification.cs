using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace MiPushDesk.Core;

public sealed record NotificationAction(string Label, string Url, string Kind = "web")
{
    public bool IsWeb => Kind == "web";
}

public static class RichNotification
{
    public static string? WebUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl)) return null;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            && string.IsNullOrEmpty(uri.UserInfo) && !string.IsNullOrWhiteSpace(uri.Host) ? uri.AbsoluteUri : null;
    }
    public static string Style(PushRecord record) => record.ExtraValue("notification_style_type") switch
    {
        "1" => "text", "2" => "picture", "3" => "banner", "4" => "colorful", "6" => "call", _ => "standard"
    };
    public static string? ImageUrl(PushRecord record) => Style(record) switch
    {
        "banner" => WebUrl(record.ExtraValue("notification_banner_image_uri")),
        "colorful" => WebUrl(record.ExtraValue("notification_colorful_bg_image_uri")),
        _ => WebUrl(record.ExtraValue("notification_bigPic_uri"))
    };
    public static string? AvatarUrl(PushRecord record) => Style(record) == "banner"
        ? WebUrl(record.ExtraValue("notification_banner_icon_uri")) : WebUrl(record.ExtraValue("notification_large_icon_uri"));
    public static string? Background(PushRecord record)
    {
        var color = record.ExtraValue("notification_colorful_bg_color");
        return Style(record) == "colorful" && Palettes.ValidColor(color) ? color : null;
    }
    public static string? ButtonColor(PushRecord record)
    {
        var color = record.ExtraValue("notification_colorful_button_bg_color");
        return Style(record) == "colorful" && Palettes.ValidColor(color) ? color : null;
    }
    public static string? Link(PushRecord record) => WebUrl(record.Url) ?? WebUrl(record.ExtraValue("web_uri"));
    public static string Tag(PushRecord record) => Identifier(NotificationTimeline.Slot(record));
    public static string Group(PushRecord record) => record.ExtraValue("notification_group") is { Length: > 0 } group
        ? Identifier(record.Package + "\0" + group) : record.ExtraValue("notification_group_disable_default") == "true" ? "" : Identifier(record.Package);
    public static IReadOnlyList<NotificationAction> Actions(PushRecord record)
    {
        var actions = new List<NotificationAction>();
        foreach (var position in new[] { "left", "mid", "right" })
        {
            var prefix = "notification_style_button_" + position;
            Add(record.ExtraValue(prefix + "_name"), record.ExtraValue(prefix + "_notify_effect"), record.ExtraValue(prefix + "_web_uri"),
                record.ExtraValue(prefix + "_intent_uri"), record.ExtraValue(prefix + "_intent_class"));
        }
        Add(record.ExtraValue("notification_colorful_button_text"), record.ExtraValue("notification_colorful_button_notify_effect"),
            record.ExtraValue("notification_colorful_button_web_uri"), record.ExtraValue("notification_colorful_button_intent_uri"), record.ExtraValue("notification_colorful_button_intent_class"));
        for (var index = 1; index <= 3; index++) Add(record.ExtraValue($"cust_btn_{index}_n"), record.ExtraValue($"cust_btn_{index}_ne"),
            record.ExtraValue($"cust_btn_{index}_wu"), record.ExtraValue($"cust_btn_{index}_iu"), record.ExtraValue($"cust_btn_{index}_ic"));
        if (Link(record) is { } link) Add("打开链接", "3", link, "", "");
        else if (record.ExtraValue("notify_effect") is "1" or "2" || record.ExtraValue("hyper_click_type") == "1")
            Add("应用内打开", record.ExtraValue("notify_effect") == "1" ? "1" : "2", "", record.ExtraValue("intent_uri"), record.ExtraValue("intent_class"));
        return actions;
        void Add(string label, string effect, string web, string intent, string component)
        {
            string? target = null;
            var kind = "web";
            if (effect == "1") { target = record.Package; kind = "android-app"; }
            else if (effect == "2") { target = intent.Length > 0 ? intent : component; kind = "android-intent"; }
            else if (effect is "" or "3") target = WebUrl(web);
            if (string.IsNullOrWhiteSpace(target) || target.Any(char.IsControl) || target.Length > 8192
                || actions.Any(action => action.Url == target && action.Kind == kind)) return;
            actions.Add(new(string.IsNullOrWhiteSpace(label) ? "查看" : CleanText(label, 40), target, kind));
        }
    }
    public static string Identifier(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];
    public static string CleanText(string value, int maximum)
    {
        var builder = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > maximum) break;
            if (System.Xml.XmlConvert.IsXmlChar((char)rune.Value) || rune.Value > 65535) builder.Append(rune.ToString());
        }
        return builder.ToString();
    }
    public static string Description(PushRecord record, DisplayProfile profile) => profile.ToastStyle == "compact"
        ? CleanText(record.Description.Split('\n', 2)[0].TrimEnd('\r'), 180) : CleanText(record.Description, 2048);
    public static string BuildToast(PushRecord record, DisplayProfile profile, string applicationName,
                                    string? iconPath = null, string? imagePath = null, string? foregroundAction = null, string? avatarPath = null)
    {
        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"));
        if (profile.UseAppIcons && iconPath is not null)
            binding.Add(new XElement("image", new XAttribute("placement", "appLogoOverride"),
                new XAttribute("hint-crop", "circle"), new XAttribute("src", new Uri(Path.GetFullPath(iconPath)).AbsoluteUri)));
        binding.Add(new XElement("text", CleanText(record.Title.Length > 0 ? record.Title : applicationName, 250)));
        binding.Add(new XElement("text", new XAttribute("hint-maxLines", profile.ToastStyle == "compact" ? 1 : 4),
            Description(record, profile)));
        binding.Add(new XElement("text", new XAttribute("placement", "attribution"), CleanText(applicationName, 128)));
        if (profile.ToastStyle == "rich" && profile.ShowImages && avatarPath is not null)
            binding.Add(new XElement("image", new XAttribute("hint-crop", "circle"),
                new XAttribute("src", new Uri(Path.GetFullPath(avatarPath)).AbsoluteUri)));
        if (profile.ToastStyle == "rich" && profile.ShowImages && imagePath is not null)
            binding.Add(new XElement("image", new XAttribute("placement", "hero"),
                new XAttribute("src", new Uri(Path.GetFullPath(imagePath)).AbsoluteUri)));
        var toast = new XElement("toast", new XAttribute("launch", "message=" + Identifier(record.Key)),
            new XElement("visual", binding));
        if (!profile.PlaySound || record.NotifyType is { } notifyType && (notifyType & 1) == 0)
            toast.Add(new XElement("audio", new XAttribute("silent", "true")));
        var actions = new XElement("actions");
        if (foregroundAction is not null) actions.Add(new XElement("action", new XAttribute("content", foregroundAction),
            new XAttribute("activationType", "foreground"), new XAttribute("arguments", "message=" + Identifier(record.Key))));
        if (profile.ToastStyle == "rich")
            actions.Add(Actions(record).Take(foregroundAction is null ? 5 : 4).Select(action => new XElement("action",
                new XAttribute("content", CleanText(action.Label, 40)), new XAttribute("activationType", action.IsWeb ? "protocol" : "foreground"),
                new XAttribute("arguments", action.IsWeb ? action.Url : "message=" + Identifier(record.Key)))));
        if (actions.HasElements) toast.Add(actions);
        return toast.ToString(SaveOptions.DisableFormatting);
    }
}
