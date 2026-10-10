using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiPushDesk.Core;
using MiPushDesk.Core.Protocol;

internal static class AppSecretRecoveryChecks
{
    private const long NumericAppId = 2882303761512345678;
    private const string TestPackage = "example.local.recovery";
    private static readonly string Secret = Convert.ToBase64String("0123456789abcdef"u8);
    private static readonly string AccountJson = JsonSerializer.Serialize(new
    {
        uuid = "123456789@xiaomi.com/test-resource", token = "local-test-token",
        security = Convert.ToBase64String("local-test-security"u8), device_uuid = "local-test-device"
    });
    private static readonly ChannelAccount Account = ChannelAccount.Parse(AccountJson);

    public static async Task<IReadOnlyList<string>> RunAsync(string directory)
    {
        var checks = new List<string>();
        var request = SlimProtocol.ReadFrame(SlimProtocol.Frame(AppSecretRecovery.Request(Account, "request", "")));
        var container = ThriftProtocol.Decode(MiPushMessage.ChannelPayload(request, Account.Security));
        var expected = Convert.FromHexString("0B000300000007726571756573740B000500000011707573685F646174615F7265636F766572020006010D00080B0B00000001000000066F66667365740000000000");
        Assert(container.Binary(4).SequenceEqual(expected) && container.Int32(1) == 9 && container.Bool(2) == false
            && container.Bool(3) == true && container.Text(5) == AppSecretRecovery.AppId && container.Text(6) == AppSecretRecovery.Package,
            "Recovery request does not match the Python protocol vector");
        checks.Add("Native recovery request matches the independently verified Python bytes");

        var items = Enumerable.Range(0, 150).Select(index => new { package_name = $"example.local.app{index}", app_id = NumericAppId, secret = Secret });
        var data = JsonSerializer.Serialize(items);
        Assert(data.Length > 8192, "Large recovery fixture is too small");
        var decoded = MiPushMessage.Decode(Response("large", "END", data));
        var page = AppSecretRecovery.Read(decoded);
        Assert(page.Credentials.Count == 150 && page.Credentials["example.local.app149"].AppId == NumericAppId.ToString(),
            "Recovery response truncated data or rounded a numeric app ID");
        Assert(!JsonSerializer.Serialize(decoded.Record).Contains(Secret) && decoded.Record.ControlExtra.Count == 0,
            "Recovery record contains plaintext secrets");
        checks.Add("Large recovery pages preserve 64-bit app IDs and redact journal records");

        var rejected = AppSecretRecovery.Read(MiPushMessage.Decode(Response("rejected", "END", data, 70000001)));
        Assert(rejected.ErrorCode == 70000001 && rejected.Credentials.Count == 0, "Server rejection was accepted as credentials");
        ExpectFailure(() => AppSecretRecovery.Read(MiPushMessage.Decode(Response("bad", "END", "[{\"package_name\":\"example.test\",\"app_id\":true,\"secret\":\"bad\"}]"))));
        var wrongTarget = ThriftProtocol.Decode(Response("wrong", "END", data));
        wrongTarget[6] = ThriftValue.Text("example.wrong.app");
        ExpectFailure(() => AppSecretRecovery.Read(MiPushMessage.Decode(ThriftProtocol.Encode(wrongTarget))));
        checks.Add("Recovery validates application identity, AES keys and server error codes");

        var mergeStore = new AppStore(new AppPaths(Path.Combine(directory, "recovery-merge")));
        mergeStore.SaveAppCredentials(new() { [TestPackage] = new() { AppId = "test", RegId = "retained-registration", RegSecret = Secret } });
        var merged = mergeStore.MergeAppCredentials(new Dictionary<string, AppCredential> { [TestPackage] = new() { AppId = "test", RegSecret = Secret } });
        Assert(merged[TestPackage].RegId == "retained-registration", "Recovery discarded the matching regId");
        var rotated = Convert.ToBase64String("fedcba9876543210"u8);
        merged = mergeStore.MergeAppCredentials(new Dictionary<string, AppCredential> { [TestPackage] = new() { AppId = "test", RegSecret = rotated } });
        Assert(merged[TestPackage].RegId == "", "Recovery retained a regId from a different registration");
        checks.Add("Credential merge preserves matching registration IDs and clears stale IDs after key rotation");

        await SessionAsync(directory, "success", TimeSpan.FromSeconds(3), async context =>
        {
            Assert(context.Receiver.RequestAppSecretRecovery(), "Manual recovery cannot start after binding");
            var first = await ReadRequestAsync(context);
            await SendResponseAsync(context, Response(first.Text(3), "next-page", ApplicationData(TestPackage)));
            await SendResponseAsync(context, Push("live"));
            var second = await ReadRequestAsync(context);
            Assert(second.Strings(8)["offset"] == "next-page", "Recovery did not request the next offset");
            Assert(context.Store.ReadAppCredentials().Count == 0, "A partial recovery overwrote stored credentials");
            var ack = await ReadOutgoingAsync(context);
            var ackContainer = ThriftProtocol.Decode(MiPushMessage.ChannelPayload(ack, Account.Security));
            Assert(ackContainer.Int32(1) == 6, "Normal notification did not receive a delivery receipt during recovery");
            await SendResponseAsync(context, Response(second.Text(3), "END", ApplicationData("example.local.second")));
            await WaitForAsync(() => context.Receiver.Snapshot().AppSecretRecoveryState == "complete", context.Token);
            var state = context.Receiver.Snapshot();
            Assert(state.AppCredentialsCount == 2 && state.AppSecretsRecovered == 2 && state.AppSecretRecoveryPages == 2,
                "Native recovery did not finish both pages");
            Assert(state.BodiesReprocessed == 2 && state.AcksSent == 1, "History replay or delivery receipts are incorrect");
            var records = MiPushReceiver.ReadJournal(Path.Combine(context.Paths.Listener, "notifications.jsonl"));
            var replayed = records.Where(record => record.Replayed).ToArray();
            Assert(replayed.Length == 2 && replayed.All(record => record.PayloadText == "synthetic plaintext")
                && replayed.All(record => record.MessageId != "deleted"), "Recovery replay lost plaintext or resurrected a deleted record");
            var secretsFile = File.ReadAllBytes(context.Paths.AppCredentials);
            Assert(!Encoding.UTF8.GetString(secretsFile).Contains(Secret), "App credentials are not protected at rest");
            foreach (var file in Directory.EnumerateFiles(context.Paths.Listener, "*.json*"))
                Assert(!File.ReadAllText(file).Contains(Secret), "Recovery leaked a secret into a journal or status file");
            var protectedResponses = Directory.GetFiles(Path.Combine(context.Paths.Listener, "raw"), "*.bin.dpapi");
            Assert(protectedResponses.Length == 2, "Recovery response containers were not protected");
            foreach (var file in protectedResponses)
            {
                var raw = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
                Assert(AppSecretRecovery.IsResponse(MiPushMessage.Decode(raw).Record), "Protected recovery container cannot be opened");
                CryptographicOperations.ZeroMemory(raw);
            }
            Assert(context.Receiver.RequestAppSecretRecovery() && !context.Receiver.RequestAppSecretRecovery(),
                "Manual recovery allows overlapping requests");
            var refresh = await ReadRequestAsync(context);
            await SendResponseAsync(context, Response(refresh.Text(3), "END", "[]"));
            await WaitForAsync(() => context.Receiver.Snapshot().AppSecretRecoveryState == "complete", context.Token);
            Assert(context.Store.ReadAppCredentials().Count == 2 && context.Receiver.Snapshot().BodiesReprocessed == 0,
                "An empty refresh erased credentials or repeated history replay");
        }, seedHistory: true);
        checks.Add("Native recovery interleaves notifications, commits complete pages, protects secrets and replays history without restoring deleted messages");
        checks.Add("Manual recovery prevents duplicates and an empty result preserves existing keys");

        await SessionAsync(directory, "invalid-page", TimeSpan.FromSeconds(3), async context =>
        {
            Assert(context.Receiver.RequestAppSecretRecovery(), "Manual recovery cannot start after binding");
            var requestBody = await ReadRequestAsync(context);
            await SendResponseAsync(context, Response(requestBody.Text(3), "same-offset", ApplicationData(TestPackage)));
            var next = await ReadRequestAsync(context);
            await SendResponseAsync(context, Response(next.Text(3), "same-offset", "[]"));
            await WaitForAsync(() => context.Receiver.Snapshot().AppSecretRecoveryState == "failed", context.Token);
            Assert(context.Store.ReadAppCredentials().Count == 0 && context.Receiver.Snapshot().Bound,
                "Invalid recovery pagination changed credentials or disconnected normal reception");
        });
        checks.Add("Repeated pagination offsets fail without partial credential writes or disconnecting notifications");

        await SessionAsync(directory, "timeout", TimeSpan.FromMilliseconds(150), async context =>
        {
            Assert(context.Receiver.RequestAppSecretRecovery(), "Manual recovery cannot start after binding");
            await ReadRequestAsync(context);
            await WaitForAsync(() => context.Receiver.Snapshot().AppSecretRecoveryState == "failed", context.Token);
            Assert(context.Receiver.Snapshot().AppSecretRecoveryError == "timeout" && context.Receiver.Snapshot().Bound,
                "Recovery timeout stopped the receiving connection");
            Assert(context.Receiver.RequestAppSecretRecovery(), "Timed-out recovery cannot be retried");
            var retry = await ReadRequestAsync(context);
            await SendResponseAsync(context, Response(retry.Text(3), "END", ApplicationData(TestPackage)));
            await WaitForAsync(() => context.Receiver.Snapshot().AppSecretRecoveryState == "complete", context.Token);
        });
        checks.Add("Recovery timeout preserves reception and manual retry succeeds");

        await SessionAsync(directory, "on-demand", TimeSpan.FromSeconds(3), async context =>
        {
            await AssertNoRecoveryAsync(context);
            await SendResponseAsync(context, Container(9, false, false, "example.control", "control-app", ThriftProtocol.Encode(new()
            {
                [3] = ThriftValue.Text("control"), [5] = ThriftValue.Text("configuration")
            })));
            await WaitForAsync(() => context.Receiver.Snapshot().SecmsgReceived == 1, context.Token);
            await AssertNoRecoveryAsync(context);
            await SendResponseAsync(context, Push("without-app-id", appId: ""));
            await WaitForAsync(() => context.Receiver.Snapshot().SecmsgReceived == 2, context.Token);
            await AssertNoRecoveryAsync(context);
            await SendResponseAsync(context, Push("first-missing"));
            await ReadAckAsync(context);
            var requestBody = await ReadRequestAsync(context);
            await SendResponseAsync(context, Push("same-missing"));
            await ReadAckAsync(context);
            await SendResponseAsync(context, Response(requestBody.Text(3), "END", ApplicationData(TestPackage)));
            await WaitForAsync(() => context.Receiver.Snapshot().AppSecretRecoveryState == "complete", context.Token);
            await SendResponseAsync(context, Push("known-app"));
            await ReadAckAsync(context);
            await AssertNoRecoveryAsync(context);
            var known = MiPushReceiver.ReadJournal(Path.Combine(context.Paths.Listener, "notifications.jsonl")).Last();
            Assert(known.BodyStatus == "decoded", "Recovered credentials did not decrypt the next notification");
        });
        checks.Add("Automatic recovery requires an encrypted message with a missing app ID, coalesces duplicates and skips known credentials");

        await SessionAsync(directory, "missing-retry", TimeSpan.FromSeconds(3), async context =>
        {
            await SendResponseAsync(context, Push("missing"));
            await ReadAckAsync(context);
            var requestBody = await ReadRequestAsync(context);
            await SendResponseAsync(context, Response(requestBody.Text(3), "END", "[]"));
            await WaitForAsync(() => context.Receiver.Snapshot().AppSecretRecoveryState == "complete", context.Token);
            await SendResponseAsync(context, Push("missing-again"));
            await ReadAckAsync(context);
            var retryBody = await ReadRequestAsync(context);
            await SendResponseAsync(context, Response(retryBody.Text(3), "END", "[]"));
            await WaitForAsync(() => context.Receiver.Snapshot().AppSecretRecoveryState == "complete", context.Token);
            await SendResponseAsync(context, Push("another-app-id", appId: "different-app"));
            await ReadAckAsync(context);
            var another = await ReadRequestAsync(context);
            await SendResponseAsync(context, Response(another.Text(3), "END", "[]"));
            await WaitForAsync(() => context.Receiver.Snapshot().AppSecretRecoveryState == "complete", context.Token);
        });
        checks.Add("A new message can retry an unresolved app ID without an arbitrary refresh cooldown");

        await SessionAsync(directory, "existing-credentials", TimeSpan.FromSeconds(3), async context =>
        {
            await AssertNoRecoveryAsync(context);
            await SendResponseAsync(context, Push("existing-app"));
            await ReadAckAsync(context);
            await AssertNoRecoveryAsync(context);
            await SendResponseAsync(context, Push("same-app-wrong-key", secret: Convert.ToBase64String("fedcba9876543210"u8)));
            await ReadAckAsync(context);
            await AssertNoRecoveryAsync(context);
            await SendResponseAsync(context, Push("changed-app-id", appId: "changed-registration"));
            await ReadAckAsync(context);
            var changed = await ReadRequestAsync(context);
            await SendResponseAsync(context, Response(changed.Text(3), "END", "[]"));
            await WaitForAsync(() => context.Receiver.Snapshot().AppSecretRecoveryState == "complete", context.Token);
        }, credentials: new() { [TestPackage] = new() { AppId = NumericAppId.ToString(), RegSecret = Secret } });
        checks.Add("Saved credentials suppress startup and decryption-failure refreshes; a changed app ID triggers recovery");
        return checks;
    }

    private sealed record SessionContext(AppPaths Paths, AppStore Store, MiPushReceiver Receiver, Stream Stream, byte[] Key, CancellationToken Token);

    private static async Task SessionAsync(string directory, string name, TimeSpan timeout, Func<SessionContext, Task> check,
        bool seedHistory = false, Dictionary<string, AppCredential>? credentials = null)
    {
        var paths = new AppPaths(Path.Combine(directory, "recovery-" + name));
        var store = new AppStore(paths); store.SaveAccount(AccountJson);
        if (credentials is not null) store.SaveAppCredentials(credentials);
        if (seedHistory)
        {
            Directory.CreateDirectory(Path.Combine(paths.Listener, "raw"));
            var records = new List<PushRecord>();
            foreach (var id in new[] { "history", "deleted" })
            {
                var raw = Push(id);
                var record = MiPushMessage.Decode(raw).Record;
                record.ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-1); record.RawPath = "raw/" + id + ".bin";
                AtomicFile.Write(Path.Combine(paths.Listener, record.RawPath), raw); records.Add(record);
            }
            var history = Path.Combine(paths.Listener, "notifications.jsonl");
            AtomicFile.Write(history, string.Join('\n', records.Select(record => JsonSerializer.Serialize(record))) + "\n");
            new NotificationFeed(history).Delete(records[1].Key);
        }
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var receiver = new MiPushReceiver(paths, store, new()
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)server.LocalEndpoint).Port,
            RecoveryDelay = TimeSpan.Zero, RecoveryTimeout = timeout, Heartbeat = TimeSpan.FromSeconds(2),
            FrameTimeout = TimeSpan.FromSeconds(2), PongTimeout = TimeSpan.FromSeconds(2)
        });
        var running = Task.Run(() => receiver.RunAsync(cancellation.Token));
        try
        {
            using var client = await server.AcceptTcpClientAsync(cancellation.Token);
            await using var stream = client.GetStream();
            Assert((await ReadAsync(stream, null, cancellation.Token)).Command == "CONN", "Recovery session did not handshake");
            await stream.WriteAsync(SlimProtocol.Frame(SlimProtocol.Command("CONN", SlimProtocol.Protobuf((1, "abcdefghij")))), cancellation.Token);
            var key = MiPushMessage.SessionKey("abcdefghij", Account.DeviceUuid);
            Assert((await ReadAsync(stream, key, cancellation.Token)).Command == "BIND", "Recovery session did not authenticate");
            await stream.WriteAsync(SlimProtocol.Frame(SlimProtocol.Blob(2, SlimProtocol.Protobuf((1, 5), (5, "BIND")), SlimProtocol.Protobuf((1, 1))), key), cancellation.Token);
            await WaitForAsync(() => receiver.Snapshot().Bound, cancellation.Token);
            await check(new(paths, store, receiver, stream, key, cancellation.Token));
        }
        finally { cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    private static async Task<SlimBlob> ReadOutgoingAsync(SessionContext context)
    {
        while (true)
        {
            var blob = await ReadAsync(context.Stream, context.Key, context.Token);
            if (blob.Command != "PING") return blob;
            await context.Stream.WriteAsync(SlimProtocol.Frame([]), context.Token);
        }
    }
    private static async Task<ThriftFields> ReadRequestAsync(SessionContext context)
    {
        var blob = await ReadOutgoingAsync(context);
        Assert(blob.Command == "SECMSG", "Expected a recovery request");
        var container = ThriftProtocol.Decode(MiPushMessage.ChannelPayload(blob, Account.Security));
        var body = ThriftProtocol.Decode(container.Binary(4));
        Assert(container.Int32(1) == 9 && body.Text(5) == "push_data_recover", "Unexpected application request during recovery");
        return body;
    }
    private static async Task ReadAckAsync(SessionContext context)
    {
        var blob = await ReadOutgoingAsync(context);
        var container = ThriftProtocol.Decode(MiPushMessage.ChannelPayload(blob, Account.Security));
        Assert(blob.Command == "SECMSG" && container.Int32(1) == 6, "Normal delivery did not receive an ACK before recovery");
    }
    private static async Task AssertNoRecoveryAsync(SessionContext context)
    {
        await Task.Delay(240, context.Token);
        while (((NetworkStream)context.Stream).DataAvailable)
        {
            var blob = await ReadAsync(context.Stream, context.Key, context.Token);
            Assert(blob.Command == "PING", "Recovery ran without a missing app ID");
            await context.Stream.WriteAsync(SlimProtocol.Frame([]), context.Token);
        }
        Assert(context.Receiver.Snapshot().AppSecretRecoveryState is not ("waiting" or "running"), "Unexpected recovery was queued");
    }
    private static async Task SendResponseAsync(SessionContext context, byte[] payload)
    {
        var packetId = Guid.NewGuid().ToString("N");
        var key = Convert.FromBase64String(Account.Security).Concat(Encoding.UTF8.GetBytes("_" + packetId)).ToArray();
        var blob = SlimProtocol.Blob(1, SlimProtocol.Protobuf((1, 5), (5, "SECMSG"), (7, packetId), (9, 1)), SlimProtocol.Rc4(key, payload));
        await context.Stream.WriteAsync(SlimProtocol.Frame(blob, context.Key), context.Token);
    }
    private static byte[] Response(string requestId, string offset, string data, long errorCode = 0) => Container(9, false, false,
        AppSecretRecovery.Package, AppSecretRecovery.AppId, ThriftProtocol.Encode(new()
        {
            [3] = ThriftValue.Text(requestId), [5] = ThriftValue.Text(AppSecretRecovery.ResponseType), [7] = ThriftValue.Int64(errorCode),
            [9] = ThriftValue.Strings(new Dictionary<string, string> { ["offset"] = offset, ["data"] = data })
        }));
    private static string ApplicationData(string package) => JsonSerializer.Serialize(new[] { new { package_name = package, app_id = NumericAppId, secret = Secret } });
    private static byte[] Push(string id, string? appId = null, string? secret = null) => Container(5, true, true, TestPackage, appId ?? NumericAppId.ToString(),
        MiPushMessage.CryptBody(ThriftProtocol.Encode(new()
        {
            [3] = ThriftValue.Text(id), [4] = ThriftValue.Text(appId ?? NumericAppId.ToString()),
            [8] = ThriftValue.Struct(new() { [4] = ThriftValue.Text("synthetic plaintext") })
        }), secret ?? Secret, true), new()
        {
            [1] = ThriftValue.Text(id), [2] = ThriftValue.Int64(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            [4] = ThriftValue.Text("Recovery check"), [5] = ThriftValue.Text("Encrypted notification")
        });
    private static byte[] Container(int action, bool encrypted, bool request, string package, string appId, byte[] body, ThriftFields? meta = null)
    {
        var container = new ThriftFields
        {
            [1] = ThriftValue.Int32(action), [2] = ThriftValue.Bool(encrypted), [3] = ThriftValue.Bool(request),
            [4] = ThriftValue.Binary(body), [5] = ThriftValue.Text(appId), [6] = ThriftValue.Text(package),
            [7] = ThriftValue.Struct(new() { [1] = ThriftValue.Int64(5), [2] = ThriftValue.Text("123456789") })
        };
        if (meta is not null) container[8] = ThriftValue.Struct(meta);
        return ThriftProtocol.Encode(container);
    }
    private static Task<SlimBlob> ReadAsync(Stream stream, byte[]? key, CancellationToken token) => SlimProtocol.ReadFrameAsync(stream, key, TimeSpan.FromSeconds(2), token);
    private static async Task WaitForAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(10, token);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException) { return; }
        throw new InvalidOperationException("Invalid recovery response was accepted");
    }
}
