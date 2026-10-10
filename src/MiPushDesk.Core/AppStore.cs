using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MiPushDesk.Core;

public sealed class AppPaths
{
    public string Data { get; }
    public string Settings => Path.Combine(Data, "settings.json");
    public string Account => Path.Combine(Data, "account.dpapi");
    public string AppCredentials => Path.Combine(Data, "app-credentials.dpapi");
    public string Listener => Path.Combine(Data, "listener");
    public string Icons => Path.Combine(Data, "icons");
    public string Cache => Path.Combine(Data, "images");
    public AppPaths(string? directory = null)
    {
        Data = Path.GetFullPath(directory ?? Environment.GetEnvironmentVariable("MIPUSHDESK_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiPushDesk"));
        foreach (var path in new[] { Data, Listener, Icons, Cache }) Directory.CreateDirectory(path);
    }
}

public static class AtomicFile
{
    public static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void Write(string path, string text) => Write(path, Encoding.UTF8.GetBytes(text));
}

public sealed class AppStore(AppPaths paths)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MiPushDesk/account/v1");
    private static readonly byte[] CredentialEntropy = Encoding.UTF8.GetBytes("MiPushDesk/app-credentials/v1");
    private readonly object _credentialGate = new();
    public bool HasAccount => File.Exists(paths.Account);
    public DeskSettings Read()
    {
        if (!File.Exists(paths.Settings)) return new();
        var json = File.ReadAllText(paths.Settings);
        var settings = JsonSerializer.Deserialize<DeskSettings>(json, JsonData.Options)
            ?? throw new InvalidDataException("设置文件为空。");
        Validate(settings);
        return settings;
    }
    public void Save(DeskSettings settings)
    {
        Validate(settings);
        AtomicFile.Write(paths.Settings, JsonSerializer.Serialize(settings, JsonData.Options));
    }
    public void ImportAccount(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("账号文件过大。");
        SaveAccount(File.ReadAllText(path));
    }
    public void SaveAccount(string json)
    {
        var normalized = ValidateAccount(json);
        var plaintext = Encoding.UTF8.GetBytes(normalized);
        try { AtomicFile.Write(paths.Account, ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public string ReadAccount()
    {
        var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(paths.Account), Entropy, DataProtectionScope.CurrentUser);
        try { return ValidateAccount(Encoding.UTF8.GetString(plaintext)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public Dictionary<string, AppCredential> ReadAppCredentials()
    {
        lock (_credentialGate)
        {
            if (!File.Exists(paths.AppCredentials)) return new(StringComparer.Ordinal);
            var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(paths.AppCredentials), CredentialEntropy, DataProtectionScope.CurrentUser);
            try
            {
                var credentials = JsonSerializer.Deserialize<Dictionary<string, AppCredential>>(plaintext, JsonData.Options)!;
                ValidateAppCredentials(credentials);
                return credentials;
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
    }
    public void SaveAppCredentials(Dictionary<string, AppCredential> credentials)
    {
        ValidateAppCredentials(credentials);
        lock (_credentialGate)
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials, JsonData.Options);
            try { AtomicFile.Write(paths.AppCredentials, ProtectedData.Protect(plaintext, CredentialEntropy, DataProtectionScope.CurrentUser)); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
    }
    public void UpdateAppCredential(string package, AppCredential? credential)
    {
        lock (_credentialGate)
        {
            var credentials = ReadAppCredentials();
            if (credential is null) credentials.Remove(package); else credentials[package] = credential;
            SaveAppCredentials(credentials);
        }
    }
    public Dictionary<string, AppCredential> MergeAppCredentials(IReadOnlyDictionary<string, AppCredential> incoming)
    {
        lock (_credentialGate)
        {
            var credentials = ReadAppCredentials();
            foreach (var (package, credential) in incoming)
            {
                credentials.TryGetValue(package, out var previous);
                credentials[package] = new()
                {
                    AppId = credential.AppId, RegSecret = credential.RegSecret,
                    RegId = credential.RegId.Length > 0 ? credential.RegId
                        : previous?.AppId == credential.AppId && previous.RegSecret == credential.RegSecret ? previous.RegId : ""
                };
            }
            SaveAppCredentials(credentials);
            return credentials;
        }
    }
    public static void ValidateAppCredentials(Dictionary<string, AppCredential> credentials)
    {
        if (credentials.Count > 2048) throw new InvalidDataException("应用密钥超过 2048 个。");
        foreach (var (package, credential) in credentials)
        {
            if (!ValidPackage(package) || credential is null || credential.AppId is null || credential.RegId is null
                || credential.RegSecret is null || credential.AppId.Length > 256 || credential.RegId.Length > 4096)
                throw new InvalidDataException("应用密钥格式无效。");
            try
            {
                var key = Convert.FromBase64String(credential.RegSecret);
                var valid = key.Length is 16 or 24 or 32;
                CryptographicOperations.ZeroMemory(key);
                if (!valid) throw new FormatException();
            }
            catch (FormatException) { throw new InvalidDataException("regSecret 需要是 Base64 编码的 AES 密钥。"); }
        }
    }
    public static string ValidateAccount(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 8 });
            var root = document.RootElement;
            foreach (var key in new[] { "uuid", "token", "security", "device_uuid" })
                if (!root.TryGetProperty(key, out var property) || property.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(property.GetString()) || property.GetString()!.Length > 16384)
                    throw new InvalidDataException();
            var identity = root.GetProperty("uuid").GetString()!;
            if (!Regex.IsMatch(identity, @"^[0-9]+@xiaomi\.com/[^\s/]{1,256}$", RegexOptions.CultureInvariant)
                || !long.TryParse(identity.Split('@')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var user) || user <= 0)
                throw new InvalidDataException();
            var secret = Convert.FromBase64String(root.GetProperty("security").GetString()!);
            if (secret.Length is < 8 or > 512) throw new InvalidDataException();
            CryptographicOperations.ZeroMemory(secret);
            foreach (var key in new[] { "client_attrs", "cloud_attrs" })
                if (root.TryGetProperty(key, out var property) && property.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException();
            return JsonSerializer.Serialize(root);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or FormatException or InvalidOperationException)
        { throw new InvalidDataException("账号文件不完整或格式不正确，需要 uuid、token、security 和 device_uuid。"); }
    }
    public static void Validate(DeskSettings settings)
    {
        if (settings.Version != 2 || settings.Appearance is null || settings.Apps is null || settings.Apps.Count > 2048
            || settings.HiddenApps is null || settings.HiddenApps.Count > 2048 || settings.HiddenApps.Any(package => !ValidPackage(package)))
            throw new InvalidDataException("不支持的设置格式。");
        var profile = settings.Appearance;
        if (profile.Theme is null || !Palettes.Presets.ContainsKey(profile.Theme)
                || !new[] { "cards", "compact", "conversation" }.Contains(profile.Layout)
                || !new[] { "native", "tray", "off" }.Contains(profile.ToastMode)
                || !new[] { "rich", "standard", "compact" }.Contains(profile.ToastStyle)
                || !double.IsFinite(profile.FontSize) || profile.FontSize is < 12 or > 20
                || !double.IsFinite(profile.CornerRadius) || profile.CornerRadius is < 0 or > 24)
                throw new InvalidDataException("样式或字号无效。");
        if (profile.Colors is not null) Palettes.Validate(profile.Colors);
        if (profile.DefaultImage is not null && !ValidIconName(profile.DefaultImage))
            throw new InvalidDataException("默认配图路径无效。");
        if (settings.QuietStartHour is < 0 or > 23
            || settings.QuietEndHour is < 0 or > 23 || settings.HeartbeatSeconds is < 5 or > 300
            || settings.ReconnectSeconds is < 5 or > 86400)
            throw new InvalidDataException("时间设置无效。");
        foreach (var (package, rule) in settings.Apps)
        {
            if (!ValidPackage(package) || rule is null || rule.Name is null || rule.Name.Length > 128
                || (rule.Icon is not null && !ValidIconName(rule.Icon)))
                throw new InvalidDataException("应用规则或图标路径无效。");
        }
        if (settings.LibraryIcons is null || settings.LibraryIcons.Count > 10000 || settings.IconLibraryName is null || settings.IconLibraryName.Length > 128
            || settings.LibraryIcons.Any(pair => !ValidPackage(pair.Key) || !ValidIconName(pair.Value)))
            throw new InvalidDataException("图标库格式无效。");
    }
    public static bool ValidPackage(string package) => Regex.IsMatch(package, @"^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+$", RegexOptions.CultureInvariant) && package.Length <= 256;
    public static bool ValidIconName(string name) => !string.IsNullOrEmpty(name) && name.Length <= 150
        && name == Path.GetFileName(name) && !name.Contains(':') && !name.Contains('/') && !name.Contains('\\')
        && new[] { ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(name).ToLowerInvariant());
}
