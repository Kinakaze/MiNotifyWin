using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiPushDesk.Core;
using MiPushDesk.Core.Protocol;

internal static class ProtocolChecks
{
    private const string GoldenPush = "CAABAAAABQIAAgECAAMBCwAEAAAAQKu/qYTrJd8ydpMhtePpwaMPxE98Z4mzR/VJ1hpCIV/3x5GseSh+CdwmBMOVSUZu5OcA2YcE0851nWaS9isdSVcLAAUAAAAIdGVzdC1hcHALAAYAAAASZXhhbXBsZS5sb2NhbC50ZXN0DAAHCgABAAAAAAAAAAULAAIAAAAJMTIzNDU2Nzg5AAwACAsAAQAAAA90ZXN0LW1lc3NhZ2UtaWQKAAIAAAGRRgeaCAsAAwAAAAp0ZXN0LXRvcGljCwAEAAAAEOWNj+iurua1i+ivlSA8Jj4LAAUAAAAV5Y+q55So5LqO5pys5Zyw5rWL6K+VCAAIAAAAAA0ACgsLAAAAAQAAAAhleGlzdGluZwAAAAhwcmVzZXJ2ZQ0ACwsLAAAAAgAAAApzY29yZV9pbmZvAAAABnJlbW92ZQAAAAVvdGhlcgAAAARrZWVwAAA=";
    private static readonly string RegSecret = Convert.ToBase64String("0123456789abcdef"u8);
    private static readonly string AccountJson = JsonSerializer.Serialize(new
    {
        uuid = "123456789@xiaomi.com/test-resource", token = "local-test-token",
        security = Convert.ToBase64String("local-test-security"u8), device_uuid = "local-test-device"
    });
    public static async Task<IReadOnlyList<string>> RunAsync(string directory)
    {
        var checks = new List<string>();
        var account = ChannelAccount.Parse(AccountJson);
        Assert(Convert.ToHexString(SlimProtocol.Rc4("Key"u8, "Plaintext"u8)) == "BBF316E8D940AF0AD3", "RC4 published vector");
        var bind = SlimProtocol.ReadFrame(SlimProtocol.Frame(MiPushMessage.Bind(account, "abcdefghij", "win-test-packet")));
        var bindFields = SlimProtocol.ReadProtobuf(bind.Payload);
        Assert(bindFields.Text(6) == "Y8jo79l5EFEp1r3vRhFRsCykkT4=" && bindFields.Text(2) == "0", "BIND independent signature and kick policy");
        var thrift = Convert.FromHexString("0B00010000000269640A0002000000000000002A0B000400000002686900");
        var decodedThrift = ThriftProtocol.Decode(thrift);
        Assert(decodedThrift.Text(1) == "id" && decodedThrift.Int64(2) == 42 && ThriftProtocol.Encode(decodedThrift).SequenceEqual(thrift), "Thrift golden vector");
        checks.Add("C# RC4, BIND and Thrift match independent vectors");

        var encrypted = Convert.FromBase64String(GoldenPush);
        var missing = MiPushMessage.Decode(encrypted);
        Assert(missing.Record.IsNotification && missing.Record.BodyStatus == "encrypted" && missing.Acknowledge, "Outer metadata without regSecret");
        var credentials = new Dictionary<string, AppCredential> { ["example.local.test"] = new() { AppId = "test-app", RegSecret = RegSecret } };
        var message = MiPushMessage.Decode(encrypted, credentials);
        Assert(message.Record.BodyStatus == "decoded" && message.Record.PayloadText == "plaintext payload", "AES golden body");
        var wrong = new Dictionary<string, AppCredential> { ["example.local.test"] = new() { RegSecret = Convert.ToBase64String("fedcba9876543210"u8) } };
        Assert(MiPushMessage.Decode(encrypted, wrong).Record.BodyStatus == "decrypt_failed", "Wrong key must remain an explicit failure");
        var ackBlob = SlimProtocol.ReadFrame(SlimProtocol.Frame(MiPushMessage.Ack(message, account, 1800000000000)));
        var ack = ThriftProtocol.Decode(MiPushMessage.ChannelPayload(ackBlob, account.Security));
        var ackBody = ThriftProtocol.Decode(ack.Binary(4));
        Assert(ack.Int32(1) == 6 && ack.Bool(2) == false && ack.Bool(3) == true && ackBody.Text(3) == "test-message-id"
            && ackBody.Int64(5) == 1723456789000 && ackBody.Text(6) == "test-topic", "ACK fields");
        Assert(!ack.Struct(8).Strings(11).ContainsKey("score_info") && ack.Struct(8).Strings(10)["mrt"] == "1800000000000", "ACK metadata");
        Assert(ThriftProtocol.Encode(message.Envelope).SequenceEqual(encrypted), "ACK mutated received container");
        checks.Add("AES app bodies, missing keys and encrypted delivery receipts work");

        foreach (var bytes in new[] { Array.Empty<byte>(), Convert.FromHexString("0B0001FFFFFFFF"), Convert.FromHexString("0200010200"),
            Convert.FromHexString("0F0001080000100100"), Convert.FromHexString("0B000100000002"), Convert.FromHexString("0001") })
            Expect<PushProtocolException>(() => ThriftProtocol.Decode(bytes));
        var corrupt = SlimProtocol.Frame(SlimProtocol.Command("PING")); corrupt[^1] ^= 1;
        Expect<PushProtocolException>(() => SlimProtocol.ReadFrame(corrupt));
        checks.Add("Malformed Thrift and damaged frames are rejected");

        var controls = new ThriftFields { [3] = ThriftValue.Text("clear-id"), [4] = ThriftValue.Text("test-app"),
            [5] = ThriftValue.Text("clear_push_message"), [6] = ThriftValue.Bool(true),
            [8] = ThriftValue.Strings(new Dictionary<string, string> { ["notifyId"] = "-1" }) };
        var control = MiPushMessage.Decode(Container(9, controls));
        Assert(control.Record.Operation == "clear" && !control.Record.IsNotification && !control.Acknowledge, "Control was treated as a notification");
        var clearAckBlob = SlimProtocol.ReadFrame(SlimProtocol.Frame(MiPushMessage.ClearAck(control, account)));
        var clearAck = ThriftProtocol.Decode(MiPushMessage.ChannelPayload(clearAckBlob, account.Security));
        Assert(clearAck.Bool(3) == false && ThriftProtocol.Decode(clearAck.Binary(4)).Text(5) == "clear_push_message_ack", "Clear ACK direction");
        controls[8] = ThriftValue.Strings(new Dictionary<string, string>());
        Assert(MiPushMessage.Decode(Container(9, controls)).Record.Operation == "", "Empty selector must not clear everything");
        var result = new ThriftFields { [3] = ThriftValue.Text("register-id"), [4] = ThriftValue.Text("test-app"),
            [6] = ThriftValue.Int64(0), [8] = ThriftValue.Text("synthetic-reg-id"), [9] = ThriftValue.Text(RegSecret) };
        var registration = MiPushMessage.Decode(Container(1, result));
        Assert(registration.Registration?.RegSecret == RegSecret && !JsonSerializer.Serialize(registration.Record).Contains(RegSecret), "Registration leaked regSecret");
        checks.Add("Registration results and scoped clear controls preserve their semantics");

        foreach (var action in new[] { 2, 3, 4, 6, 7, 8, 9, 10, 22, 114, 999 })
        {
            var fields = new ThriftFields { [3] = ThriftValue.Text("id"), [4] = ThriftValue.Text("test-app") };
            var parsed = MiPushMessage.Decode(Container(action, fields, false));
            Assert(parsed.Record.Body is not null && !parsed.Acknowledge && !parsed.Record.IsNotification, "Unsupported action created side effects");
            if (action == 22) Assert(parsed.Record.BodyStatus == "unsupported", "SendMessageNew schema must not be guessed");
        }
        checks.Add("Protocol actions retain decoded fields without inventing unsupported behavior");

        var paths = new AppPaths(Path.Combine(directory, "receiver"));
        var store = new AppStore(paths); store.SaveAccount(AccountJson); store.SaveAppCredentials(credentials);
        Assert(store.ReadAppCredentials()["example.local.test"].RegSecret == RegSecret
            && !Encoding.UTF8.GetString(File.ReadAllBytes(paths.AppCredentials)).Contains(RegSecret), "DPAPI application credentials");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var receiver = new MiPushReceiver(paths, store, new()
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Heartbeat = TimeSpan.FromMilliseconds(50),
            Reconnect = TimeSpan.FromSeconds(5), FrameTimeout = TimeSpan.FromSeconds(2), PongTimeout = TimeSpan.FromSeconds(2)
        });
        var run = Task.Run(() => receiver.RunAsync(stop.Token));
        try
        {
            for (var connectionIndex = 0; connectionIndex < 2; connectionIndex++)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                await using var stream = client.GetStream();
                var hello = await ReadAsync(stream, null, stop.Token);
                Assert(hello.Command == "CONN" && SlimProtocol.ReadProtobuf(hello.Payload).Text(4) == account.DeviceUuid, "Native CONN identity");
                await stream.WriteAsync(SlimProtocol.Frame(SlimProtocol.Command("CONN", SlimProtocol.Protobuf((1, "abcdefghij")))), stop.Token);
                var key = MiPushMessage.SessionKey("abcdefghij", account.DeviceUuid);
                Assert((await ReadAsync(stream, key, stop.Token)).Command == "BIND", "Native BIND missing");
                await stream.WriteAsync(SlimProtocol.Frame(ServerBlob("BIND", SlimProtocol.Protobuf((1, 1))), key), stop.Token);
                var ping = await ReadAsync(stream, key, stop.Token); Assert(ping.Command == "PING", "Initial heartbeat missing");
                await stream.WriteAsync(SlimProtocol.Frame([]), stop.Token);
                if (connectionIndex == 0)
                {
                    for (var copy = 0; copy < 2; copy++)
                    {
                        var packetId = "test-delivery-" + copy;
                        var payloadKey = Convert.FromBase64String(account.Security).Concat(Encoding.UTF8.GetBytes("_" + packetId)).ToArray();
                        var push = ServerBlob("SECMSG", SlimProtocol.Rc4(payloadKey, encrypted), packetId, 1);
                        var frame = SlimProtocol.Frame(push, key);
                        await stream.WriteAsync(frame.AsMemory(0, 7), stop.Token);
                        await stream.WriteAsync(frame.AsMemory(7), stop.Token);
                        SlimBlob deliveryAck;
                        do
                        {
                            deliveryAck = await ReadAsync(stream, key, stop.Token);
                            if (deliveryAck.Command == "PING") await stream.WriteAsync(SlimProtocol.Frame([]), stop.Token);
                        } while (deliveryAck.Command == "PING");
                        Assert(deliveryAck.Command == "SECMSG", "Delivery ACK missing");
                        var saved = MiPushReceiver.ReadJournal(Path.Combine(paths.Listener, "notifications.jsonl"));
                        Assert(saved.Count == 1 && saved[0].PayloadText == "plaintext payload", "ACK occurred before persistence or duplicate displayed");
                    }
                    await stream.WriteAsync(SlimProtocol.Frame(ServerBlob("KICK", SlimProtocol.Protobuf((1, "wait"), (2, "synthetic-reason"))), key), stop.Token);
                    Assert((await ReadPastHeartbeatsAsync(stream, key, stop.Token)).Command == "CLOSE", "Native session did not close after KICK");
                    while (receiver.Snapshot().RetryAt is null) await Task.Delay(10, stop.Token);
                    Assert(receiver.Snapshot().RetryAt > DateTimeOffset.UtcNow.AddSeconds(3), "Configured reconnect interval was ignored");
                    receiver.UpdateTiming(TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(120));
                }
                else
                {
                    stop.Cancel();
                    Assert((await ReadPastHeartbeatsAsync(stream, key, CancellationToken.None)).Command == "CLOSE", "Graceful cancellation missing CLOSE");
                }
            }
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            var state = receiver.Snapshot();
            Assert(state.ConnectionAttempts == 2 && state.AcksSent == 2 && state.Duplicates == 1 && state.NotificationsReceived == 1, "Native listener counters");
            Assert(state.State == "stopped" && !state.Bound, "Native receiver did not stop");
            Assert(state.ReconnectSeconds == 0.12 && state.HeartbeatSeconds == 0.08, "Live timing update was not applied");
            var events = File.ReadAllText(Path.Combine(paths.Listener, "events.jsonl"));
            Assert(events.Contains("synthetic-reason") && !events.Contains(account.Token) && !events.Contains(account.Security) && !events.Contains(RegSecret), "Receiver diagnostics leaked credentials");
            checks.Add("Native loopback: handshake, heartbeat, fragmented delivery, durable ACK, dedup, KICK reconnect and stop");
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        await VerifyPersistenceFailureAsync(directory, encrypted, account);
        checks.Add("Persistence failure prevents ACK and survives receiver restart without losing notification");
        await VerifyReplayAsync(directory, encrypted, credentials);
        checks.Add("Stored ciphertext decrypts after adding regSecret without replaying cleared notifications");
        return checks;
    }
    private static async Task VerifyPersistenceFailureAsync(string directory, byte[] encrypted, ChannelAccount account)
    {
        var paths = new AppPaths(Path.Combine(directory, "persistence-failure"));
        var store = new AppStore(paths); store.SaveAccount(AccountJson);
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        var options = new ReceiverOptions { Host = "127.0.0.1", Port = ((IPEndPoint)server.LocalEndpoint).Port,
            Heartbeat = TimeSpan.FromSeconds(10), Reconnect = TimeSpan.FromSeconds(10), FrameTimeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var receiver = new MiPushReceiver(paths, store, options);
            var running = Task.Run(() => receiver.RunAsync(cancellation.Token));
            try
            {
                using var client = await server.AcceptTcpClientAsync(cancellation.Token);
                await using var stream = client.GetStream();
                var key = await BindServerAsync(stream, account, cancellation.Token);
                using var blockedFile = attempt == 0 ? new FileStream(Path.Combine(paths.Listener, "notifications.jsonl"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None) : null;
                const string packetId = "persistence-test";
                var payloadKey = Convert.FromBase64String(account.Security).Concat(Encoding.UTF8.GetBytes("_" + packetId)).ToArray();
                await stream.WriteAsync(SlimProtocol.Frame(ServerBlob("SECMSG", SlimProtocol.Rc4(payloadKey, encrypted), packetId, 1), key), cancellation.Token);
                var response = await ReadAsync(stream, key, cancellation.Token);
                if (attempt == 0) Assert(response.Command == "CLOSE", "Failed persistence was acknowledged");
                else
                {
                    Assert(response.Command == "SECMSG", "Redelivery after restart was not acknowledged");
                    var journal = MiPushReceiver.ReadJournal(Path.Combine(paths.Listener, "notifications.jsonl"));
                    Assert(journal.Count == 1 && !journal[0].Duplicate, "Uncommitted message was incorrectly deduplicated after restart");
                }
            }
            finally { cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(4)); }
        }
    }
    private static async Task VerifyReplayAsync(string directory, byte[] encrypted, Dictionary<string, AppCredential> credentials)
    {
        foreach (var cleared in new[] { false, true })
        {
            var paths = new AppPaths(Path.Combine(directory, "replay-" + cleared));
            var store = new AppStore(paths); store.SaveAccount(AccountJson); store.SaveAppCredentials(credentials);
            var record = MiPushMessage.Decode(encrypted).Record;
            record.ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-2); record.RawPath = "raw/replay.bin";
            Directory.CreateDirectory(Path.Combine(paths.Listener, "raw"));
            File.WriteAllBytes(Path.Combine(paths.Listener, record.RawPath), encrypted);
            var history = Path.Combine(paths.Listener, "notifications.jsonl");
            File.WriteAllText(history, JsonSerializer.Serialize(record) + "\n");
            if (cleared) File.AppendAllText(history, JsonSerializer.Serialize(new PushRecord { Key = "clear-replay", Package = record.Package,
                ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-1), Operation = "clear", ControlExtra = new() { ["notifyId"] = "-1" } }) + "\n");
            using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var receiver = new MiPushReceiver(paths, store, new() { Host = "127.0.0.1", Port = ((IPEndPoint)server.LocalEndpoint).Port });
            var running = Task.Run(() => receiver.RunAsync(cancellation.Token));
            try
            {
                using var client = await server.AcceptTcpClientAsync(cancellation.Token);
                var replayed = MiPushReceiver.ReadJournal(history).Where(item => item.Replayed).ToArray();
                Assert(cleared ? replayed.Length == 0 : replayed.Length == 1 && replayed[0].PayloadText == "plaintext payload"
                    && replayed[0].ReceivedAt == record.ReceivedAt, "History replay lost body or resurrected cleared record");
            }
            finally { cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(4)); }
        }
    }
    private static async Task<byte[]> BindServerAsync(Stream stream, ChannelAccount account, CancellationToken cancellationToken)
    {
        Assert((await ReadAsync(stream, null, cancellationToken)).Command == "CONN", "Expected CONN");
        await stream.WriteAsync(SlimProtocol.Frame(SlimProtocol.Command("CONN", SlimProtocol.Protobuf((1, "abcdefghij")))), cancellationToken);
        var key = MiPushMessage.SessionKey("abcdefghij", account.DeviceUuid);
        Assert((await ReadAsync(stream, key, cancellationToken)).Command == "BIND", "Expected BIND");
        await stream.WriteAsync(SlimProtocol.Frame(ServerBlob("BIND", SlimProtocol.Protobuf((1, 1))), key), cancellationToken);
        Assert((await ReadAsync(stream, key, cancellationToken)).Command == "PING", "Expected heartbeat");
        await stream.WriteAsync(SlimProtocol.Frame([]), cancellationToken);
        return key;
    }
    private static byte[] Container(int action, ThriftFields body, bool request = true) => ThriftProtocol.Encode(new()
    {
        [1] = ThriftValue.Int32(action), [2] = ThriftValue.Bool(false), [3] = ThriftValue.Bool(request),
        [4] = ThriftValue.Binary(ThriftProtocol.Encode(body)), [5] = ThriftValue.Text("test-app"),
        [6] = ThriftValue.Text("example.local.test"), [7] = ThriftValue.Struct(new() { [1] = ThriftValue.Int64(5), [2] = ThriftValue.Text("123456789") })
    });
    private static byte[] ServerBlob(string command, byte[] payload, string packetId = "test-packet", int cipher = 0) =>
        SlimProtocol.Blob(command == "SECMSG" ? (ushort)1 : (ushort)2,
            SlimProtocol.Protobuf((1, 5), (5, command), (7, packetId), (9, cipher)), payload);
    private static async Task<SlimBlob> ReadAsync(Stream stream, byte[]? key, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(3));
        return await SlimProtocol.ReadFrameAsync(stream, key, TimeSpan.FromSeconds(2), deadline.Token);
    }
    private static async Task<SlimBlob> ReadPastHeartbeatsAsync(Stream stream, byte[] key, CancellationToken cancellationToken)
    {
        SlimBlob response;
        do { response = await ReadAsync(stream, key, cancellationToken); } while (response.Command == "PING");
        return response;
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Expect<TException>(Action action) where TException : Exception
    {
        try { action(); } catch (TException) { return; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name);
    }
}
