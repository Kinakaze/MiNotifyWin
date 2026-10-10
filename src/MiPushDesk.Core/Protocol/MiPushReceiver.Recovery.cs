using System.Security.Cryptography;
using System.Text.Json;

namespace MiPushDesk.Core.Protocol;

public sealed partial class MiPushReceiver
{
    private int _recoveryRequested;
    private DateTimeOffset _recoveryReadyAt;
    private DateTimeOffset _recoveryDeadline;
    private string? _recoveryRequestId;
    private readonly Dictionary<string, AppCredential> _recoveredCredentials = new(StringComparer.Ordinal);
    private readonly HashSet<string> _recoveryOffsets = new(StringComparer.Ordinal);

    private void RequestMissingAppSecret(PushRecord record)
    {
        if (!_options.RecoverAppSecrets || !record.EncryptedAction || string.IsNullOrWhiteSpace(record.AppId)
            || string.IsNullOrWhiteSpace(record.Package) || record.BodyStatus == "decoded") return;
        if (_credentials.TryGetValue(record.Package, out var credential) && credential.AppId == record.AppId) return;
        if (!RequestAppSecretRecovery()) return;
        Event("app_secret_recovery_needed", new { package = record.Package, app_id = record.AppId });
    }

    public bool RequestAppSecretRecovery()
    {
        lock (_stateGate)
        {
            if (!_state.Bound || _state.AppSecretRecoveryState is "waiting" or "running") return false;
            _state.AppSecretRecoveryState = "waiting";
            _state.AppSecretRecoveryError = null;
            Interlocked.Exchange(ref _recoveryRequested, 1);
            return true;
        }
    }

    private async Task AdvanceRecoveryAsync(Stream stream, byte[] sessionKey, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_recoveryRequestId is not null && now >= _recoveryDeadline) FailRecovery("timeout");
        if (now < _recoveryReadyAt || Interlocked.Exchange(ref _recoveryRequested, 0) == 0) return;
        _recoveredCredentials.Clear(); _recoveryOffsets.Clear();
        _recoveryDeadline = now + _options.RecoveryTimeout;
        lock (_stateGate)
        {
            _state.AppSecretRecoveryState = "running"; _state.AppSecretRecoveryError = null;
            _state.AppSecretRecoveryPages = 0; _state.AppSecretsRecovered = 0; _state.BodiesReprocessed = 0;
        }
        Event("app_secret_recovery_started");
        await SendRecoveryRequestAsync(stream, sessionKey, "", cancellationToken);
    }

    private async Task SendRecoveryRequestAsync(Stream stream, byte[] sessionKey, string offset, CancellationToken cancellationToken)
    {
        _recoveryOffsets.Add(offset);
        _recoveryRequestId = "recover-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        await SendAsync(stream, AppSecretRecovery.Request(_account, _recoveryRequestId, offset), sessionKey, cancellationToken);
    }

    private async Task ReceiveRecoveryAsync(DecodedPush message, Stream stream, byte[] sessionKey, CancellationToken cancellationToken)
    {
        if (_recoveryRequestId is null || message.Body.Text(3) != _recoveryRequestId)
        {
            Event("app_secret_recovery_ignored");
            return;
        }
        AppSecretRecoveryPage page;
        try
        {
            page = AppSecretRecovery.Read(message);
            if (page.ErrorCode is not null and not 0) { FailRecovery("server_rejected"); return; }
            foreach (var (package, credential) in page.Credentials)
            {
                if (_recoveredCredentials.TryGetValue(package, out var previous)
                    && (previous.AppId != credential.AppId || previous.RegSecret != credential.RegSecret))
                    throw new PushProtocolException("Conflicting application recovery pages.");
                _recoveredCredentials[package] = credential;
            }
            if (_recoveredCredentials.Count > AppSecretRecovery.MaxApplications)
                throw new PushProtocolException("Application recovery limit exceeded.");
            lock (_stateGate)
            {
                _state.AppSecretRecoveryPages++;
                _state.AppSecretsRecovered = _recoveredCredentials.Count;
            }
            if (page.Offset != "END" && (_recoveryOffsets.Contains(page.Offset) || _recoveryOffsets.Count >= AppSecretRecovery.MaxPages))
                throw new PushProtocolException("Invalid application recovery pagination.");
        }
        catch (Exception error) when (error is IOException or JsonException or FormatException or InvalidOperationException)
        { FailRecovery("invalid_response"); return; }
        if (page.Offset != "END")
        {
            Event("app_secret_recovery_page", new { page = _state.AppSecretRecoveryPages, applications = _recoveredCredentials.Count });
            await SendRecoveryRequestAsync(stream, sessionKey, page.Offset, cancellationToken);
            return;
        }
        try { _credentials = _store.MergeAppCredentials(_recoveredCredentials); }
        catch (Exception error) when (error is IOException or CryptographicException or UnauthorizedAccessException)
        { FailRecovery("save_failed"); return; }
        _recoveryRequestId = null; _recoveredCredentials.Clear(); _recoveryOffsets.Clear();
        lock (_stateGate)
        {
            _state.AppCredentialsCount = _credentials.Count;
            _state.AppSecretRecoveryState = "complete";
            _state.AppSecretsUpdatedAt = DateTimeOffset.UtcNow;
        }
        try
        {
            var reprocessed = ReprocessHistory();
            lock (_stateGate) _state.BodiesReprocessed = reprocessed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            lock (_stateGate) _state.AppSecretRecoveryError = "history_failed";
        }
        Event("app_secret_recovery_completed", new
        {
            pages = _state.AppSecretRecoveryPages, applications = _state.AppSecretsRecovered,
            reprocessed = _state.BodiesReprocessed, history_error = _state.AppSecretRecoveryError
        });
    }

    private void FailRecovery(string reason)
    {
        _recoveryRequestId = null; _recoveredCredentials.Clear(); _recoveryOffsets.Clear();
        lock (_stateGate) { _state.AppSecretRecoveryState = "failed"; _state.AppSecretRecoveryError = reason; }
        Event("app_secret_recovery_failed", new { reason });
    }

    private void EndRecoverySession(bool stopping)
    {
        _recoveryRequestId = null; _recoveredCredentials.Clear(); _recoveryOffsets.Clear();
        lock (_stateGate)
        {
            if (_state.AppSecretRecoveryState is not ("waiting" or "running")) return;
            _state.AppSecretRecoveryState = stopping ? "idle" : "waiting";
            Interlocked.Exchange(ref _recoveryRequested, stopping ? 0 : 1);
        }
    }
}
