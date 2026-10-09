using System.Text.Json.Nodes;
using Cuelify.Infrastructure.Storage;
using Xunit;

namespace Cuelify.Tests;

public sealed class CredentialStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CuelifyCredentials", Guid.NewGuid().ToString("N"));
    private const string Password = "测试主密码-123456789";
    private string PathToFile => Path.Combine(_root, "credentials.json");

    [Fact]
    public async Task PasswordHasNoLengthOrCompositionPolicy()
    {
        using var store = new CredentialStore(_root);
        await store.CreateAsync("!"); await store.SaveAsync(new("saved-secret"));
        var password = new string('中', 2048);
        await store.ChangePasswordAsync("!", password); store.Lock();
        await store.UnlockAsync(password); Assert.Equal("saved-secret", store.Values.ElevenLabs);
    }

    [Fact]
    public async Task FileContainsOnlyEncryptedCredentialsAndCanMoveBetweenDirectories()
    {
        using var store = new CredentialStore(_root);
        await store.CreateAsync(Password);
        var values = new CredentialValues("eleven-secret", "compatible-secret", "deepseek-secret", "https://example.com/v1");
        await store.SaveAsync(values);
        var text = await File.ReadAllTextAsync(PathToFile);
        foreach (var secret in new[] { Password, values.ElevenLabs, values.Compatible, values.DeepSeek, values.CompatibleAddress }) Assert.DoesNotContain(secret, text);
        store.Lock(); Assert.False(store.IsUnlocked); Assert.Equal(new CredentialValues(), store.Values);
        var moved = Path.Combine(_root, "moved"); Directory.CreateDirectory(moved); File.Copy(PathToFile, Path.Combine(moved, "credentials.json"));
        using var other = new CredentialStore(moved);
        Assert.True(other.HasMasterPassword); Assert.False(other.IsUnlocked);
        await other.UnlockAsync(Password); Assert.Equal(values, other.Values);
    }

    [Fact]
    public async Task WrongPasswordAndTamperedCiphertextNeverUnlockOrOverwriteFile()
    {
        using var store = new CredentialStore(_root); await store.CreateAsync(Password); store.Lock();
        var original = await File.ReadAllTextAsync(PathToFile);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UnlockAsync("incorrect-password"));
        Assert.False(store.IsUnlocked); Assert.Equal(original, await File.ReadAllTextAsync(PathToFile));
        var document = JsonNode.Parse(original)!;
        var ciphertext = Convert.FromBase64String(document["Ciphertext"]!.GetValue<string>()); ciphertext[0] ^= 1;
        document["Ciphertext"] = Convert.ToBase64String(ciphertext); var changed = document.ToJsonString(); await File.WriteAllTextAsync(PathToFile, changed);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UnlockAsync(Password));
        Assert.False(store.IsUnlocked); Assert.Equal(changed, await File.ReadAllTextAsync(PathToFile));
    }

    [Theory]
    [InlineData("Iterations", 1)]
    [InlineData("Iterations", 2_000_001)]
    [InlineData("Iterations", 600_001)]
    [InlineData("Version", 99)]
    public async Task InvalidOrTamperedMetadataIsRejected(string field, int value)
    {
        using var store = new CredentialStore(_root); await store.CreateAsync(Password); store.Lock();
        var document = JsonNode.Parse(await File.ReadAllTextAsync(PathToFile))!; document["Metadata"]![field] = value;
        await File.WriteAllTextAsync(PathToFile, document.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UnlockAsync(Password)); Assert.False(store.IsUnlocked);
    }

    [Fact]
    public async Task PasswordChangeRequiresCurrentPasswordAndPreservesSavedKeys()
    {
        using var store = new CredentialStore(_root); await store.CreateAsync(Password); await store.SaveAsync(new("existing-secret"));
        var original = await File.ReadAllTextAsync(PathToFile);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ChangePasswordAsync("wrong-password", "new-password-123"));
        Assert.Equal(original, await File.ReadAllTextAsync(PathToFile)); Assert.True(store.IsUnlocked);
        await store.ChangePasswordAsync(Password, "new-password-123"); store.Lock();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UnlockAsync(Password));
        await store.UnlockAsync("new-password-123"); Assert.Equal("existing-secret", store.Values.ElevenLabs);
    }

    [Fact]
    public async Task EachSaveUsesFreshNonceAndLockedStoreCannotSave()
    {
        using var store = new CredentialStore(_root); await store.CreateAsync(Password);
        await store.SaveAsync(new("secret")); var first = JsonNode.Parse(await File.ReadAllTextAsync(PathToFile))!;
        await store.SaveAsync(new("secret")); var second = JsonNode.Parse(await File.ReadAllTextAsync(PathToFile))!;
        Assert.NotEqual(first["Nonce"]!.GetValue<string>(), second["Nonce"]!.GetValue<string>());
        store.Lock(); await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(new("replacement")));
    }

    [Fact]
    public async Task CancelledOrBlockedWriteKeepsOldFileAndSessionValues()
    {
        using var store = new CredentialStore(_root); await store.CreateAsync(Password); await store.SaveAsync(new("old-secret"));
        var original = await File.ReadAllTextAsync(PathToFile);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new("new-secret"), cancelled.Token));
        using (var lease = new FileStream(Path.Combine(_root, "credentials.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(new("new-secret")));
        Assert.Equal(original, await File.ReadAllTextAsync(PathToFile)); Assert.Equal("old-secret", store.Values.ElevenLabs);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task StaleSessionCannotOverwriteNewCredentialsAndResetKeepsOtherFiles()
    {
        using var first = new CredentialStore(_root); await first.CreateAsync(Password);
        using var second = new CredentialStore(_root); await second.UnlockAsync(Password);
        await first.SaveAsync(new("new-secret"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.SaveAsync(new("stale-secret")));
        var settings = Path.Combine(_root, "settings.json"); await File.WriteAllTextAsync(settings, "keep-settings");
        var cache = Path.Combine(_root, "Jobs"); Directory.CreateDirectory(cache); await File.WriteAllTextAsync(Path.Combine(cache, "cached.txt"), "keep-cache");
        await first.ResetAsync(); Assert.False(first.HasMasterPassword); Assert.False(first.IsUnlocked);
        Assert.Equal("keep-settings", await File.ReadAllTextAsync(settings)); Assert.True(File.Exists(Path.Combine(cache, "cached.txt")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.SaveAsync(new("stale-secret")));
    }

    [Fact]
    public async Task CorruptFileIsNotReplacedByCreationOrUnlock()
    {
        Directory.CreateDirectory(_root); await File.WriteAllTextAsync(PathToFile, "broken-json");
        using var store = new CredentialStore(_root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(Password));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UnlockAsync(Password));
        Assert.Equal("broken-json", await File.ReadAllTextAsync(PathToFile));
        await store.ResetAsync(); await store.CreateAsync(Password); Assert.True(store.IsUnlocked);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
