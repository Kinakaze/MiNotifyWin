using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MiPushDesk.Core;
using MiPushDesk.Core.Protocol;

internal static class AccountImportChecks
{
    internal const string Secret = "c3ludGhldGljLXNlY3JldA==";
    public static async Task<IReadOnlyList<string>> RunAsync(string directory)
    {
        var checks = new List<string>();
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "session.json"));
        var document = ExchangeDocument.ParseImport(json);
        var candidate = AccountCandidate.From(document).Single();
        Assert(candidate.HasSignature && candidate.MatchesSignature(Secret), "Python signature vector did not match");
        Assert(!candidate.MatchesSignature(Convert.ToBase64String("wrong-secret"u8)), "Wrong security matched");
        Assert(candidate.SecurityFromLog("invalid-sig token = another-token sec = " + Secret) is null, "A different token was accepted");
        Assert(candidate.SecurityFromLog("invalid-sig token = example-token sec = invalid!") is null, "Malformed security was accepted");
        Assert(candidate.SecurityFromLog("invalid-sig token = example-token sec = " + Secret) == Secret, "Matching log was lost");
        var verified = await candidate.VerifyAsync(Secret, CancellationToken.None);
        Assert(verified.GetProperty("bind_signature_verified").GetBoolean(), "No signature verification marker");
        Assert(document.Account is null, "Verification modified an unsaved document");
        checks.Add("Security matches the Python BIND vector and rejects wrong tokens and keys");

        var duplicate = JsonNode.Parse(json)!.AsObject();
        var sessions = duplicate["analysis"]!["sessions"]!.AsArray();
        sessions.Add(sessions[0]!.DeepClone());
        Assert(AccountCandidate.From(ExchangeDocument.ParseImport(duplicate.ToJsonString())).Count == 1, "Repeated BIND created multiple accounts");
        var other = sessions[0]!.DeepClone();
        other["bind"]!["uuid"] = "987654321@xiaomi.com/other";
        other["bind"]!["token"] = "other-token";
        sessions.Add(other);
        var multiple = AccountCandidate.From(ExchangeDocument.ParseImport(duplicate.ToJsonString()));
        Assert(multiple.Count == 2 && multiple[0].MatchesSignature(Secret) && !multiple[1].MatchesSignature(Secret), "Mixed independent sessions");
        await ExpectAsync<InvalidDataException>(() => multiple[1].VerifyAsync(Secret, CancellationToken.None));
        other["source"] = "self_test";
        sessions[2] = other.DeepClone();
        Assert(AccountCandidate.From(ExchangeDocument.ParseImport(duplicate.ToJsonString())).Count == 1, "Test account appeared in import");
        checks.Add("Repeated sessions deduplicate while distinct accounts and test traffic stay separate");
        var stale = JsonNode.Parse(verified.GetRawText())!.AsObject();
        stale["token"] = "previous-token";
        var sharedAgain = ExchangeDocument.ParseImport(json);
        sharedAgain.Account = JsonSerializer.SerializeToElement(stale);
        var accountChoices = AccountCandidate.From(sharedAgain);
        Assert(accountChoices.Count == 2 && accountChoices.Count(item => item.HasSignature) == 1
            && accountChoices[0].Label != accountChoices[1].Label, "A previously imported account hid a fresh capture");
        checks.Add("New phone captures remain selectable when an earlier account is included in the same JSON");

        var paths = new AppPaths(Path.Combine(directory, "account-completion"));
        var store = new AppStore(paths);
        store.SaveAccount(verified.GetRawText());
        store.SaveAppCredentials(new() { ["com.example.test"] = new() { AppId = "id", RegSecret = Convert.ToBase64String("0123456789abcdef"u8) } });
        var before = File.ReadAllBytes(paths.Account);
        await ExpectAsync<InvalidDataException>(() => candidate.VerifyAsync(Convert.ToBase64String("wrong-secret"u8), CancellationToken.None));
        Assert(before.SequenceEqual(File.ReadAllBytes(paths.Account)), "Failed verification changed the saved account");
        document.Account = verified;
        document.Settings = new() { Appearance = new() { Layout = "conversation" } };
        new ImportService(paths).Apply(document, new(), store);
        Assert(store.Read().Appearance.Layout == "conversation" && store.ReadAppCredentials().Count == 1, "Completion dropped existing data");
        Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(paths.Account)).Contains(Secret), "Account was not encrypted");
        ExchangeDocument.Parse(document.ToJson());
        checks.Add("Only verified account data is saved, preserving settings and encrypted application keys");

        var draft = JsonNode.Parse(verified.GetRawText())!.AsObject();
        draft.Remove("security");
        var draftDocument = new ExchangeDocument { Account = JsonSerializer.SerializeToElement(draft) };
        var parsedDraft = ExchangeDocument.ParseImport(draftDocument.ToJson());
        Assert(parsedDraft.Summary().Contains("尚缺 security"), "Draft was reported complete");
        await ExpectAsync<InvalidDataException>(() => Task.FromResult(ExchangeDocument.Parse(draftDocument.ToJson())));
        await ExpectAsync<InvalidDataException>(() => Task.Run(() => store.SaveAccount(draft.ToJsonString())));
        var online = AccountCandidate.From(parsedDraft).Single();
        Assert(!online.HasSignature, "Untrusted verification marker bypassed verification");
        await VerifyOnlineAsync(online, Secret, true);
        await VerifyOnlineAsync(online, Convert.ToBase64String("wrong-secret"u8), false);
        checks.Add("Incomplete account JSON requires real login verification when no captured signature exists");

        var devices = AdbClient.ParseDevices("List of devices attached\nUSB123 device usb:1 model:Pixel_8\n192.168.1.3:45678 device model:Xiaomi\nUSB456 unauthorized\nUSB789 offline\n");
        Assert(devices.Count == 4 && devices[0].Usb && !devices[1].Usb && !devices[2].Ready && !devices[3].Ready, "ADB devices were conflated");
        AdbClient.ValidateAddress("192.168.1.3:45678");
        AdbClient.ValidateAddress("[fd00::1]:45678");
        foreach (var address in new[] { "192.168.1.3", "192.168.1.3:0", "host:5555", "127.0.0.1:5555;whoami", "127.0.0.1:5555\n" })
            await ExpectAsync<InvalidDataException>(() => Task.Run(() => AdbClient.ValidateAddress(address)));
        checks.Add("USB and Wi-Fi selection retain unauthorized and offline states and validate addresses");

        var fixtureDirectory = Path.Combine(directory, "adb-fixture");
        Directory.CreateDirectory(fixtureDirectory);
        var previous = Environment.GetEnvironmentVariable("MIPUSHDESK_FAKE_ADB");
        Environment.SetEnvironmentVariable("MIPUSHDESK_FAKE_ADB", fixtureDirectory);
        try
        {
            var adb = new AdbClient(Environment.ProcessPath!);
            var progress = new Progress<string>();
            var cached = await adb.ExtractSecurityAsync(new("cached", "device", "Fixture", true), candidate, progress, CancellationToken.None);
            Assert(cached == Secret && !File.Exists(Path.Combine(fixtureDirectory, "started")), "Cached log triggered an unnecessary diagnostic");
            var live = await adb.ExtractSecurityAsync(new("live", "device", "Fixture", true), candidate, progress, CancellationToken.None);
            Assert(live == Secret && File.Exists(Path.Combine(fixtureDirectory, "stopped")), "Live diagnostic did not finish cleanup");
            File.Delete(Path.Combine(fixtureDirectory, "stopped"));
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await ExpectAsync<OperationCanceledException>(() => adb.ExtractSecurityAsync(new("cancel", "device", "Fixture", true), candidate, progress, cancel.Token));
            Assert(File.Exists(Path.Combine(fixtureDirectory, "stopped")), "Cancellation left the phone diagnostic active");
            var processId = int.Parse(File.ReadAllText(Path.Combine(fixtureDirectory, "reader-pid")));
            Assert(!Process.GetProcesses().Any(process => process.Id == processId), "Cancellation left logcat running");
            await adb.PairAsync("192.168.1.3:45678", "123456", CancellationToken.None);
            await adb.ConnectAsync("192.168.1.3:45678", CancellationToken.None);
            Assert(File.ReadAllText(Path.Combine(fixtureDirectory, "pair-input")) == "stdin", "Pairing code was placed on the command line");
            try
            {
                await adb.ExtractSecurityAsync(new("disconnected", "device", "Fixture", true), candidate, progress, CancellationToken.None);
                throw new InvalidOperationException("Disconnected device did not fail");
            }
            catch (IOException error) { Assert(!error.Message.Contains(Secret), "ADB error exposed raw output"); }
        }
        finally { Environment.SetEnvironmentVariable("MIPUSHDESK_FAKE_ADB", previous); }
        checks.Add("ADB streams matching logs, sends pairing codes privately, and stops owned diagnostics on cancellation");
        return checks;
    }

    private static async Task VerifyOnlineAsync(AccountCandidate candidate, string security, bool accepted)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = client.GetStream();
            var hello = await SlimProtocol.ReadFrameAsync(stream, null, TimeSpan.FromSeconds(2), deadline.Token);
            Assert(hello.Command == "CONN", "Missing verification handshake");
            await stream.WriteAsync(SlimProtocol.Frame(SlimProtocol.Command("CONN", SlimProtocol.Protobuf((1, "verify-challenge")))), deadline.Token);
            var account = ChannelAccount.Parse(candidate.WithSecurity(Secret).GetRawText());
            var key = MiPushMessage.SessionKey("verify-challenge", account.DeviceUuid);
            var bind = await SlimProtocol.ReadFrameAsync(stream, key, TimeSpan.FromSeconds(2), deadline.Token);
            var fields = SlimProtocol.ReadProtobuf(bind.Payload);
            Assert(fields.Text(2) == "0", "Validation changed kick behavior");
            var valid = fields.Text(6) == MiPushMessage.BindSignature(account, "verify-challenge", bind.PacketId, "0");
            await stream.WriteAsync(SlimProtocol.Frame(SlimProtocol.Blob(2, SlimProtocol.Protobuf((1, 5), (5, "BIND")),
                SlimProtocol.Protobuf((1, valid ? 1 : 0))), key), deadline.Token);
            if (valid) Assert((await SlimProtocol.ReadFrameAsync(stream, key, TimeSpan.FromSeconds(2), deadline.Token)).Command == "CLOSE", "Validation left its connection open");
        }, deadline.Token);
        var verify = candidate.VerifyAsync(security, deadline.Token, new("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port));
        if (accepted) Assert((await verify).GetProperty("login_verified").GetBoolean(), "Login was not verified");
        else await ExpectAsync<InvalidDataException>(() => verify);
        await server;
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task ExpectAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name);
    }
}
