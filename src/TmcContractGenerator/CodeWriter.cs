using System.Text;

namespace TmcContractGenerator;

internal sealed class CodeWriter
{
    private readonly StringBuilder _sb = new();
    private int _indent;

    public void Line(string text = "")
    {
        if (text.Length > 0)
            _sb.Append(' ', _indent * 4).Append(text);
        _sb.Append('\n');
    }

    public IDisposable Block(string header)
    {
        Line(header);
        Line("{");
        _indent++;
        return new Scope(this);
    }

    public override string ToString() => _sb.ToString();

    private sealed class Scope : IDisposable
    {
        private readonly CodeWriter _writer;
        private bool _disposed;

        public Scope(CodeWriter writer)
        {
            _writer = writer;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writer._indent--;
            _writer.Line("}");
        }
    }
}
