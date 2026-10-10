using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MiPushDesk.Core.Protocol;

public sealed record ReceiverOptions
{
    public string Host { get; init; } = "cn.app.chat.xiaomi.net";
    public int Port { get; init; } = 5222;
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Reconnect { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan PongTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public bool RecoverAppSecrets { get; init; } = true;
    public TimeSpan RecoveryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RecoveryTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed class ChannelRejectedException(string command, string errorType, string reason) : IOException(command)
{
    public string ErrorType { get; } = errorType;
    public string Reason { get; } = reason;
}

public sealed partial class MiPushReceiver
{
    private static readonly JsonSerializerOptions JournalJson = new(JsonData.Options) { WriteIndented = false };
    private readonly AppPaths _paths;
    private readonly AppStore _store;
    private readonly ChannelAccount _account;
    private readonly ReceiverOptions _options;
    private readonly object _stateGate = new();
    private readonly ListenerState _state = new() { Pid = Environment.ProcessId, State = "starting" };
    private readonly SemaphoreSlim _timingChanged = new(0, 1);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private Dictionary<string, AppCredential> _credentials;
    private long _heartbeatTicks;
    private long _reconnectTicks;
    public MiPushReceiver(AppPaths paths, AppStore store, ReceiverOptions? options = null)
    {
        _paths = paths; _store = store; _account = ChannelAccount.Parse(store.ReadAccount());
        _credentials = store.ReadAppCredentials(); _options = options ?? new();
        _heartbeatTicks = _options.Heartbeat.Ticks; _reconnectTicks = _options.Reconnect.Ticks;
        _state.HeartbeatSeconds = _options.Heartbeat.TotalSeconds; _state.ReconnectSeconds = _options.Reconnect.TotalSeconds;
        _state.AppCredentialsCount = _credentials.Count;
    }
    public ListenerState Snapshot()
    {
        lock (_stateGate) return JsonSerializer.Deserialize<ListenerState>(JsonSerializer.Serialize(_state, JournalJson), JournalJson)!;
    }
    public void UpdateTiming(TimeSpan heartbeat, TimeSpan reconnect)
    {
        Interlocked.Exchange(ref _heartbeatTicks, heartbeat.Ticks);
        Interlocked.Exchange(ref _reconnectTicks, reconnect.Ticks);
        lock (_stateGate) { _state.HeartbeatSeconds = heartbeat.TotalSeconds; _state.ReconnectSeconds = reconnect.TotalSeconds; }
        if (_timingChanged.CurrentCount == 0) _timingChanged.Release();
    }
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var instance = new FileStream(Path.Combine(_paths.Listener, "listener.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        instance.Lock(0, 1);
        Directory.CreateDirectory(Path.Combine(_paths.Listener, "raw"));
        try
        {
            foreach (var record in ReadJournal(Path.Combine(_paths.Listener, "messages.jsonl"))) Remember(DedupKey(record));
            ReprocessHistory();
            Event("started", new { engine = "dotnet", host = _options.Host, port = _options.Port });
            while (!cancellationToken.IsCancellationRequested)
            {
                try { await SessionAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception error) when (error is IOException or SocketException or TimeoutException or CryptographicException)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    var disconnectedAt = DateTimeOffset.UtcNow;
                    lock (_stateGate)
                    {
                        _state.State = "reconnecting"; _state.Bound = false; _state.HeartbeatConfirmed = false;
                        _state.LastError = error is ChannelRejectedException rejection ? rejection.Message : error.GetType().Name;
                        _state.ErrorType = error is ChannelRejectedException rejected ? _account.Redact(rejected.ErrorType) : null;
                        _state.ErrorReason = error is ChannelRejectedException ended ? _account.Redact(ended.Reason) : null;
                    }
                    Event("disconnected", new { error = _state.LastError, error_type = _state.ErrorType, reason = _state.ErrorReason });
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var retryAt = disconnectedAt.AddTicks(Interlocked.Read(ref _reconnectTicks));
                        lock (_stateGate) _state.RetryAt = retryAt;
                        WriteStatus();
                        var delay = retryAt - DateTimeOffset.UtcNow;
                        if (delay <= TimeSpan.Zero || !await _timingChanged.WaitAsync(delay, cancellationToken)) break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            lock (_stateGate) { _state.State = "stopped"; _state.Bound = false; _state.HeartbeatConfirmed = false; _state.RetryAt = null; }
            Event("stopped");
            instance.Unlock(0, 1);
        }
    }
    private async Task SessionAsync(CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            _state.State = "connecting"; _state.Bound = false; _state.HeartbeatConfirmed = false;
            _state.RetryAt = null; _state.ConnectionAttempts++;
        }
        Event("connecting");
        using var client = new TcpClient { NoDelay = true };
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        using var session = new CancellationTokenSource();
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(session.Token, cancellationToken);
        handshake.CancelAfter(_options.FrameTimeout);
        try { await client.ConnectAsync(_options.Host, _options.Port, handshake.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("TCP connection timed out."); }
        await using var stream = client.GetStream();
        byte[]? sessionKey = null;
        Task<SlimBlob>? incoming = null;
        try
        {
            var hello = SlimProtocol.Protobuf((1, 106), (2, "MiPushDesk"), (3, "Windows"), (4, _account.DeviceUuid),
                (5, 48), (6, "wifi"), (7, _options.Host), (8, "zh_CN"), (10, 0));
            await SendAsync(stream, SlimProtocol.Command("CONN", hello), null, handshake.Token);
            SlimBlob response;
            try { response = await SlimProtocol.ReadFrameAsync(stream, null, _options.FrameTimeout, handshake.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("CONN timed out."); }
            if (response.Command != "CONN") throw new PushProtocolException("Expected CONN response.");
            var challenge = SlimProtocol.ReadProtobuf(response.Payload).Text(1);
            sessionKey = MiPushMessage.SessionKey(challenge, _account.DeviceUuid);
            lock (_stateGate) _state.State = "binding";
            await SendAsync(stream, MiPushMessage.Bind(_account, challenge), sessionKey, cancellationToken);
            Event("handshake_completed");
            var bindDeadline = DateTimeOffset.UtcNow + _options.FrameTimeout;
            DateTimeOffset? pendingPing = null;
            var previousPing = DateTimeOffset.MinValue;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = DateTimeOffset.UtcNow;
                var bound = _state.Bound;
                if (!bound && now >= bindDeadline) throw new TimeoutException("BIND timed out.");
                if (pendingPing is { } pending && now - pending >= _options.PongTimeout) throw new TimeoutException("MiPush heartbeat timed out.");
                var heartbeat = TimeSpan.FromTicks(Interlocked.Read(ref _heartbeatTicks));
                if (bound && pendingPing is null && now - previousPing >= heartbeat)
                {
                    await SendAsync(stream, SlimProtocol.Command("PING", packetId: "0"), sessionKey, cancellationToken);
                    pendingPing = previousPing = now;
                    lock (_stateGate) _state.PingsSent++;
                    Event("ping_sent");
                }
                if (bound) await AdvanceRecoveryAsync(stream, sessionKey, cancellationToken);
                incoming ??= SlimProtocol.ReadFrameAsync(stream, sessionKey, _options.FrameTimeout, session.Token);
                var wait = bound && pendingPing is null ? previousPing + heartbeat - now : TimeSpan.FromMilliseconds(200);
                wait = TimeSpan.FromMilliseconds(Math.Clamp(wait.TotalMilliseconds, 1, 200));
                if (await Task.WhenAny(incoming, Task.Delay(wait, cancellationToken)) != incoming) continue;
                response = await incoming; incoming = null;
                switch (response.Command)
                {
                    case "BIND" when response.Channel == 5:
                        var binding = SlimProtocol.ReadProtobuf(response.Payload);
                        if (binding.Number(1) != 1) throw new ChannelRejectedException("BIND rejected", binding.Text(2), binding.Text(3));
                        lock (_stateGate)
                        {
                            _state.State = "listening"; _state.Bound = true; _state.LastError = null;
                            _state.ErrorType = _state.ErrorReason = null; _state.ConnectionsBound++;
                        }
                        _recoveryReadyAt = DateTimeOffset.UtcNow + _options.RecoveryDelay;
                        Event("bound"); break;
                    case "PING" when response.Channel == 0:
                        if (pendingPing is not null && response.PacketId != "1")
                        {
                            pendingPing = null;
                            lock (_stateGate) { _state.HeartbeatConfirmed = true; _state.PongsReceived++; _state.LastPong = DateTimeOffset.UtcNow; }
                            Event("pong_received");
                        }
                        else Event("server_ping");
                        if (response.Payload.Length > 0) SaveControl("ping_config", response);
                        break;
                    case "SECMSG" when response.Channel == 5 && _state.Bound:
                        await ReceiveMessageAsync(response, stream, sessionKey, cancellationToken); break;
                    case "KICK" when response.Channel is 0 or 5:
                        var kick = SlimProtocol.ReadProtobuf(response.Payload);
                        throw new ChannelRejectedException("Kicked by server", kick.Text(1), kick.Text(2));
                    case "CLOSE": throw new ChannelRejectedException("Server requested CLOSE", "", "");
                    case "SYNC" when response.Channel == 0:
                        if (response.Subcommand == "P")
                        {
                            var ping = SlimProtocol.ReadProtobuf(response.Payload);
                            var pong = ping.ContainsKey(1) ? SlimProtocol.Protobuf((1, ping.Bytes(1))) : [];
                            await SendAsync(stream, SlimProtocol.Command("SYNC", pong, response.PacketId, "PCA"), sessionKey, cancellationToken);
                        }
                        else if (response.Subcommand == "U")
                            await SendAsync(stream, SlimProtocol.Command("SYNC", packetId: response.PacketId, subcommand: "UCA"), sessionKey, cancellationToken);
                        SaveControl("sync", response); break;
                    case "NOTIFY" when response.Channel == 0:
                        var notify = SlimProtocol.ReadProtobuf(response.Payload);
                        Event("server_notice", new { code = notify.Number(1) }); break;
                    default: SaveControl("frame", response); break;
                }
            }
        }
        finally
        {
            EndRecoverySession(cancellationToken.IsCancellationRequested);
            if (sessionKey is not null && client.Connected)
            {
                using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await SendAsync(stream, SlimProtocol.Command("CLOSE"), sessionKey, closeDeadline.Token); }
                catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
            }
            session.Cancel();
            if (incoming is not null)
            {
                try { await incoming; }
                catch (Exception error) when (error is IOException or OperationCanceledException or SocketException or TimeoutException) { }
            }
            if (sessionKey is not null) CryptographicOperations.ZeroMemory(sessionKey);
            lock (_stateGate) _state.Bound = false;
        }
    }
    private async Task SendAsync(Stream stream, byte[] blob, byte[]? key, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.FrameTimeout);
        try { await stream.WriteAsync(SlimProtocol.Frame(blob, key), deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("MiPush write timed out."); }
    }
    private async Task ReceiveMessageAsync(SlimBlob blob, Stream stream, byte[] sessionKey, CancellationToken cancellationToken)
    {
        lock (_stateGate) _state.SecmsgReceived++;
        if (blob.Header.Number(10) != 0) { Event("secmsg_error", new { code = blob.Header.Number(10) }); return; }
        var raw = blob.Payload;
        DecodedPush? message = null;
        var rawKind = "wire_payload";
        var record = new PushRecord { ReceivedAt = DateTimeOffset.UtcNow };
        try
        {
            raw = MiPushMessage.ChannelPayload(blob, _account.Security); rawKind = "decrypted_container";
            message = MiPushMessage.Decode(raw, _credentials); record = message.Record;
        }
        catch (Exception error) when (error is IOException or FormatException or OverflowException)
        {
            lock (_stateGate) _state.DecodeErrors++;
            record.BodyStatus = "malformed";
        }
        var digest = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
        var protectedRaw = message?.Record.Action is 1 or 9 || message is null && rawKind == "decrypted_container";
        record.RawPath = "raw/" + digest + (protectedRaw ? ".bin.dpapi" : ".bin");
        var rawPath = Path.Combine(_paths.Listener, record.RawPath);
        if (!File.Exists(rawPath)) AtomicFile.Write(rawPath, protectedRaw
            ? ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser) : raw);
        record.AdditionalData = new()
        {
            ["packet_id"] = JsonSerializer.SerializeToElement(blob.PacketId),
            ["raw_kind"] = JsonSerializer.SerializeToElement(protectedRaw ? "dpapi_container" : rawKind),
            ["raw_bytes"] = JsonSerializer.SerializeToElement(raw.Length)
        };
        var dedup = DedupKey(record);
        record.Duplicate = dedup.Length > 0 && _seen.Contains(dedup);
        if ((record.IsNotification || record.Operation == "clear") && !record.Duplicate) Append("notifications.jsonl", record);
        if (message?.Registration is { } credential)
        {
            _store.UpdateAppCredential(record.Package, credential);
            _credentials[record.Package] = credential;
            lock (_stateGate) _state.AppCredentialsCount = _credentials.Count;
            Event("registration_saved", new { package = record.Package });
            ReprocessHistory();
        }
        Append("messages.jsonl", record);
        if (message is null) { Event("decode_error", new { raw_path = record.RawPath }); return; }
        if (AppSecretRecovery.IsResponse(record)) await ReceiveRecoveryAsync(message, stream, sessionKey, cancellationToken);
        else RequestMissingAppSecret(record);
        Remember(dedup);
        lock (_stateGate)
        {
            _state.LastMessage = record.ReceivedAt;
            if (record.Duplicate) _state.Duplicates++;
            else { _state.MessagesReceived += record.IsMessage ? 1 : 0; _state.NotificationsReceived += record.IsNotification ? 1 : 0; }
            if (record.BodyStatus is "malformed" or "decrypt_failed") _state.DecodeErrors++;
        }
        Event("message_saved", new { package = record.Package, action = record.Action, notification = record.IsNotification,
            duplicate = record.Duplicate, body_status = record.BodyStatus, ack_required = message.Acknowledge });
        if (message.Acknowledge || record.Operation == "clear" && message.Body.Text(3).Length > 0)
        {
            var ack = message.Acknowledge ? MiPushMessage.Ack(message, _account, record.ReceivedAt.ToUnixTimeMilliseconds()) : MiPushMessage.ClearAck(message, _account);
            await SendAsync(stream, ack, sessionKey, cancellationToken);
            lock (_stateGate) _state.AcksSent++;
            Event("ack_sent", new { message_key = record.Key });
        }
    }
    private int ReprocessHistory()
    {
        var reprocessed = 0;
        var current = new List<PushRecord>();
        foreach (var record in new NotificationFeed(Path.Combine(_paths.Listener, "notifications.jsonl")).Read(true))
            NotificationTimeline.Apply(current, record, DateTimeOffset.UtcNow);
        var history = current.Where(record => record.EncryptedAction && record.BodyStatus != "decoded"
            && _credentials.ContainsKey(record.Package)).ToArray();
        foreach (var previous in history)
        {
            var raw = Path.GetFullPath(Path.Combine(_paths.Listener, previous.RawPath));
            var root = Path.Combine(_paths.Listener, "raw") + Path.DirectorySeparatorChar;
            if (!raw.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(raw)) continue;
            try
            {
                var decoded = MiPushMessage.Decode(File.ReadAllBytes(raw), _credentials);
                if (decoded.Record.BodyStatus != "decoded" || decoded.Record.Key != previous.Key) continue;
                decoded.Record.ReceivedAt = previous.ReceivedAt; decoded.Record.Replayed = true; decoded.Record.RawPath = previous.RawPath;
                decoded.Record.ExpiresAt = previous.ExpiresAt;
                Append("notifications.jsonl", decoded.Record);
                reprocessed++;
            }
            catch (PushProtocolException) { Event("reprocess_error", new { message_key = previous.Key }); }
        }
        return reprocessed;
    }
    private static string DedupKey(PushRecord record) => record.ExtraValue("jobkey") is { Length: > 0 } job
        ? "job:" + record.Package + ":" + record.Action + ":" + job : record.Key;
    private void Remember(string key)
    {
        if (key.Length == 0 || !_seen.Add(key)) return;
        _seenOrder.Enqueue(key);
        if (_seenOrder.Count > 4096) _seen.Remove(_seenOrder.Dequeue());
    }
    public static IReadOnlyList<PushRecord> ReadJournal(string file)
    {
        var result = new List<PushRecord>();
        if (!File.Exists(file)) return result;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - 8 * 1024 * 1024);
        stream.Position = start;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (start > 0) reader.ReadLine();
        while (reader.ReadLine() is { } line)
        {
            try { if (JsonSerializer.Deserialize<PushRecord>(line, JournalJson) is { } record) result.Add(record); }
            catch (JsonException) { }
        }
        return result;
    }
    private void SaveControl(string kind, SlimBlob blob) => Event(kind, new
    {
        command = blob.Command, channel = blob.Channel, subcommand = blob.Subcommand,
        packet_id = blob.PacketId, payload = Convert.ToBase64String(blob.Payload)
    });
    private void Append(string name, object value)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JournalJson) + "\n");
        using var output = new FileStream(Path.Combine(_paths.Listener, name), FileMode.Append, FileAccess.Write, FileShare.Read);
        output.Write(bytes); output.Flush(true);
    }
    private void Event(string kind, object? details = null)
    {
        Append("events.jsonl", new { time = DateTimeOffset.UtcNow, @event = kind, details });
        WriteStatus();
    }
    private void WriteStatus()
    {
        lock (_stateGate)
        {
            _state.UpdatedAt = DateTimeOffset.UtcNow;
            AtomicFile.Write(Path.Combine(_paths.Listener, "status.json"), JsonSerializer.Serialize(_state, JsonData.Options));
        }
    }
}
