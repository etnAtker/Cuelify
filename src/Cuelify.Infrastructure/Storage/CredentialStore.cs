using System.Security.Cryptography;
using System.Text.Json;

namespace Cuelify.Infrastructure.Storage;

public sealed record CredentialValues(string ElevenLabs = "", string Compatible = "", string DeepSeek = "", string CompatibleAddress = "");

// 只依赖托管密码学和文件 API；凭证文件不绑定操作系统或账户。
public sealed class CredentialStore(string root) : IDisposable
{
    private const int Iterations = 600_000;
    private const int MaximumFileBytes = 256 * 1024;
    private readonly string _path = Path.Combine(root, "credentials.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _key;
    private Header? _header;
    private string? _revision;
    public bool HasMasterPassword => File.Exists(_path);
    public bool IsUnlocked => _key is not null;
    public CredentialValues Values { get; private set; } = new();

    public async Task CreateAsync(string password, CancellationToken token = default)
    {
        RequirePassword(password);
        await MutateAsync(async () =>
        {
            if (HasMasterPassword) throw new InvalidOperationException("主密码已设置，请先解锁或重置凭证。");
            await ReplaceAsync(password, new(), token).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    public async Task UnlockAsync(string password, CancellationToken token = default)
    {
        await MutateAsync(async () =>
        {
            var envelope = await ReadAsync(token).ConfigureAwait(false);
            var key = await DeriveAsync(password, envelope.Metadata, token).ConfigureAwait(false);
            try
            {
                var values = Decrypt(envelope, key);
                token.ThrowIfCancellationRequested();
                SetSession(envelope, key, values); key = [];
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }, token).ConfigureAwait(false);
    }

    public async Task SaveAsync(CredentialValues values, CancellationToken token = default)
    {
        ValidateValues(values);
        await MutateAsync(async () =>
        {
            await CheckRevisionAsync(token).ConfigureAwait(false);
            var envelope = Encrypt(values, _key!, _header!);
            await AtomicFile.WriteJsonAsync(_path, envelope, token).ConfigureAwait(false);
            Values = values; _revision = AtomicFile.Hash(envelope);
        }, token).ConfigureAwait(false);
    }

    public async Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken token = default)
    {
        RequirePassword(newPassword);
        await MutateAsync(async () =>
        {
            await CheckRevisionAsync(token).ConfigureAwait(false);
            var envelope = await ReadAsync(token).ConfigureAwait(false);
            var key = await DeriveAsync(currentPassword, envelope.Metadata, token).ConfigureAwait(false);
            try
            {
                var values = Decrypt(envelope, key);
                await ReplaceAsync(newPassword, values, token).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }, token).ConfigureAwait(false);
    }

    public async Task ResetAsync(CancellationToken token = default)
    {
        await MutateAsync(() => { token.ThrowIfCancellationRequested(); File.Delete(_path); Lock(); return Task.CompletedTask; }, token).ConfigureAwait(false);
    }

    public void Lock()
    {
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        _key = null; _header = null; _revision = null; Values = new();
    }

    private async Task MutateAsync(Func<Task> action, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(root);
            using var lease = new FileStream(Path.Combine(root, "credentials.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await action().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task CheckRevisionAsync(CancellationToken token)
    {
        if (!IsUnlocked) throw new InvalidOperationException("请先解锁已保存的密钥。");
        if (!HasMasterPassword || AtomicFile.Hash(await ReadAsync(token).ConfigureAwait(false)) != _revision)
            throw new InvalidOperationException("凭证文件已被其他窗口修改，请锁定后重新解锁。");
    }

    private async Task ReplaceAsync(string password, CredentialValues values, CancellationToken token)
    {
        var header = new Header(1, "PBKDF2-SHA256", Iterations, RandomNumberGenerator.GetBytes(16), "AES-256-GCM");
        var key = await DeriveAsync(password, header, token).ConfigureAwait(false);
        try
        {
            var envelope = Encrypt(values, key, header);
            await AtomicFile.WriteJsonAsync(_path, envelope, token).ConfigureAwait(false);
            SetSession(envelope, key, values); key = [];
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private void SetSession(Envelope envelope, byte[] key, CredentialValues values)
    {
        Lock(); _key = key; _header = envelope.Metadata; _revision = AtomicFile.Hash(envelope); Values = values;
    }

    private static Task<byte[]> DeriveAsync(string password, Header header, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        return Rfc2898DeriveBytes.Pbkdf2(password, header.Salt, header.Iterations, HashAlgorithmName.SHA256, 32);
    }, token);

    private static Envelope Encrypt(CredentialValues values, byte[] key, Header header)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(values);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var ciphertext = new byte[plaintext.Length];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, JsonSerializer.SerializeToUtf8Bytes(header));
            return new(header, nonce, tag, ciphertext);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static CredentialValues Decrypt(Envelope envelope, byte[] key)
    {
        var plaintext = new byte[envelope.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, JsonSerializer.SerializeToUtf8Bytes(envelope.Metadata));
            var values = JsonSerializer.Deserialize<CredentialValues>(plaintext) ?? throw new JsonException();
            ValidateValues(values); return values;
        }
        catch (CryptographicException) { throw new InvalidDataException("主密码不正确或凭证文件已损坏。请重试；重置凭证将清除已保存的密钥。"); }
        catch (JsonException) { throw new InvalidDataException("凭证内容已损坏，请检查凭证文件。重置凭证将清除已保存的密钥。"); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private async Task<Envelope> ReadAsync(CancellationToken token)
    {
        if (!HasMasterPassword) throw new InvalidOperationException("尚未设置主密码。");
        await using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException("凭证文件大小无效，请检查文件。");
        var bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        Envelope envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidDataException("凭证文件格式已损坏，请检查文件。重置凭证将清除已保存的密钥。"); }
        var header = envelope.Metadata;
        if (header is null) throw new InvalidDataException("凭证文件缺少加密参数。");
        if (header.Version != 1 || header.Kdf != "PBKDF2-SHA256" || header.Cipher != "AES-256-GCM")
            throw new InvalidDataException("不支持此凭证文件版本或加密格式，请使用兼容版本的 Cuelify。");
        if (header.Iterations is < Iterations or > 2_000_000 || header.Salt?.Length != 16 || envelope.Nonce?.Length != 12 || envelope.Tag?.Length != 16 || envelope.Ciphertext is not { Length: > 0 and <= 128 * 1024 })
            throw new InvalidDataException("凭证文件的加密参数无效，请检查文件。");
        return envelope;
    }

    private static void RequirePassword(string password)
    { if (string.IsNullOrEmpty(password)) throw new ArgumentException("请输入主密码。"); }
    private static void ValidateValues(CredentialValues values)
    {
        if (new[] { values.ElevenLabs, values.Compatible, values.DeepSeek, values.CompatibleAddress }.Any(value => value is null || value.Length > 4096))
            throw new InvalidDataException("密钥或服务地址长度无效。");
    }
    public void Dispose() => Lock();

    private sealed record Header(int Version, string Kdf, int Iterations, byte[] Salt, string Cipher);
    private sealed record Envelope(Header Metadata, byte[] Nonce, byte[] Tag, byte[] Ciphertext);
}
