using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MiPushDesk.Core.Protocol;

namespace MiPushDesk.Core;

public sealed partial class AccountCandidate
{
    private readonly JsonElement _account;
    private readonly List<JsonElement> _binds = [];
    private string _source = "文件账号";
    public string Uuid => Text(_account, "uuid");
    public string Token => Text(_account, "token");
    public string Security => Text(_account, "security");
    public bool HasSignature => _binds.Count > 0;
    public string Label => _source + " · " + (Uuid.Length > 12 ? Uuid[..4] + "…" + Uuid[^8..] : Uuid);

    private AccountCandidate(JsonElement account)
    {
        AppStore.ValidateAccountDraft(account.GetRawText());
        _account = account;
    }

    public static IReadOnlyList<AccountCandidate> From(ExchangeDocument document)
    {
        var candidates = new List<AccountCandidate>();
        if (document.Account is { } account) candidates.Add(new(account));
        if (document.Analysis is not { } analysis) return candidates;
        if (analysis.TryGetProperty("sessions", out var sessions) && sessions.ValueKind == JsonValueKind.Array)
        {
            var sessionNumber = 0;
            foreach (var session in sessions.EnumerateArray())
            {
                sessionNumber++;
                if (Text(session, "source") != "phone_xmsf" || !session.TryGetProperty("bind", out var bind)) continue;
                if (!bind.TryGetProperty("channel", out var channel) || !channel.TryGetInt32(out var number) || number != 5
                    || Text(bind, "method") != "XMPUSH-PASS") continue;
                foreach (var field in new[] { "challenge", "packet_id", "kick", "signature", "client_attrs", "cloud_attrs" })
                    if (!bind.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException("抓包缺少 BIND 核验字段，请重新抓包。");
                var fields = new JsonObject();
                foreach (var field in new[] { "uuid", "token", "device_uuid", "client_attrs", "cloud_attrs" })
                    fields[field] = Text(bind, field);
                fields["endpoint"] = Text(session, "endpoint");
                fields["channel"] = 5;
                fields["method"] = "XMPUSH-PASS";
                var captured = new AccountCandidate(JsonSerializer.SerializeToElement(fields)) { _source = "抓包会话 " + sessionNumber };
                var candidate = candidates.FirstOrDefault(item => item.SameAccount(captured._account));
                if (candidate is null)
                {
                    candidates.Add(candidate = captured);
                }
                candidate._binds.Add(bind.Clone());
            }
        }
        if (candidates.Count == 0 && analysis.TryGetProperty("credentials", out var credentials)
            && credentials.ValueKind == JsonValueKind.Object && Text(credentials, "uuid").Length > 0
            && Text(credentials, "token").Length > 0 && Text(credentials, "device_uuid").Length > 0)
            candidates.Add(new(credentials.Clone()));
        return candidates;
    }

    private bool SameAccount(JsonElement other) => new[] { "uuid", "token", "device_uuid", "client_attrs", "cloud_attrs" }
        .All(field => Text(_account, field) == Text(other, field));

    public JsonElement WithSecurity(string security)
    {
        var account = JsonNode.Parse(_account.GetRawText())!.AsObject();
        account["security"] = security.Trim();
        account.Remove("ready_for_windows_login");
        account.Remove("bind_signature_verified");
        account.Remove("login_verified");
        var result = JsonSerializer.SerializeToElement(account);
        AppStore.ValidateAccount(result.GetRawText());
        return result;
    }

    public bool MatchesSignature(string security)
    {
        var account = ChannelAccount.Parse(WithSecurity(security).GetRawText());
        return _binds.Any(bind => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Text(bind, "signature")),
            Encoding.UTF8.GetBytes(MiPushMessage.BindSignature(account, Text(bind, "challenge"), Text(bind, "packet_id"), Text(bind, "kick")))));
    }

    public string? SecurityFromLog(string line)
    {
        var match = SecurityLog().Match(line);
        if (!match.Success || match.Groups[1].Value != Token) return null;
        var security = match.Groups[2].Value;
        try
        {
            WithSecurity(security);
            return !HasSignature || MatchesSignature(security) ? security : null;
        }
        catch (InvalidDataException) { return null; }
    }

    public async Task<JsonElement> VerifyAsync(string security, CancellationToken cancellationToken,
        ChannelAccountVerifier? verifier = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = WithSecurity(security);
        if (HasSignature)
        {
            if (!MatchesSignature(security)) throw new InvalidDataException("security 与抓包签名不匹配，请检查密钥或重新提取。");
        }
        else await (verifier ?? new()).VerifyAsync(ChannelAccount.Parse(account.GetRawText()), cancellationToken);
        var verified = JsonNode.Parse(account.GetRawText())!.AsObject();
        verified[HasSignature ? "bind_signature_verified" : "login_verified"] = true;
        return JsonSerializer.SerializeToElement(verified);
    }

    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! : "";

    [GeneratedRegex(@"invalid-sig token = (\S+) sec = (\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex SecurityLog();
}
