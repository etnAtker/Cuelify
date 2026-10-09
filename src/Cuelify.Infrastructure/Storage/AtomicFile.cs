using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cuelify.Infrastructure.Storage;

public static class AtomicFile
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        if (!File.Exists(path)) return null;
        await using var input = File.OpenRead(path);
        try { return await JsonSerializer.DeserializeAsync<T>(input, JsonOptions, cancellationToken) ?? throw new InvalidDataException("缓存为空。"); }
        catch (JsonException) { throw new InvalidDataException($"缓存 JSON 损坏：{Path.GetFileName(path)}。请检查该作业缓存，避免意外重复计费。"); }
    }

    public static Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        WriteAsync(path, stream => JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken), true, cancellationToken);

    public static Task WriteTextAsync(string path, string text, bool overwrite, CancellationToken cancellationToken) =>
        WriteAsync(path, async stream =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteAsync(text.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }, overwrite, cancellationToken);

    private static async Task WriteAsync(string path, Func<Stream, Task> write, bool overwrite, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await write(output);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
    }
}
