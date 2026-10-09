using System.Text;
using LLama.Native;

namespace Cuelify.Infrastructure.Translation.Local;

public sealed class NativeLog : IDisposable
{
    private readonly object _sync = new();
    private readonly StringBuilder _text = new();
    private readonly StreamWriter _writer;

    public NativeLog(string path)
    {
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void Write(LLamaLogLevel level, string message)
    {
        lock (_sync)
        {
            _text.Append(message);
            _writer.Write(message);
        }
    }

    public string Snapshot()
    {
        lock (_sync)
            return _text.ToString();
    }

    public void Dispose() => _writer.Dispose();
}
