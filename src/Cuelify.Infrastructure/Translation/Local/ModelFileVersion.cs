namespace Cuelify.Infrastructure.Translation.Local;

// 文件版本只用于实例复用与缓存分隔，不读取或验证 GGUF 内容。
public sealed record ModelFileVersion(string Path, long Length, DateTime LastWriteTimeUtc)
{
    public static ModelFileVersion Read(string path)
    {
        var file = new FileInfo(System.IO.Path.GetFullPath(path));
        if (!file.Exists) throw new FileNotFoundException("找不到指定模型文件。", file.FullName);
        var fullPath = OperatingSystem.IsWindows() ? file.FullName.ToUpperInvariant() : file.FullName;
        return new(fullPath, file.Length, file.LastWriteTimeUtc);
    }
}
