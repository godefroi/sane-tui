using System.Text;

namespace SaneTui;

/// <summary>
/// A <see cref="TextWriter"/> that hands each finished line to a callback, so code that writes messages to a log
/// (the scan command) can feed a UI instead. Carriage returns are dropped; they only matter to a terminal.
/// </summary>
internal sealed class StatusWriter(Action<string> onLine) : TextWriter
{
	private readonly StringBuilder _line = new();

	public override Encoding Encoding => Encoding.UTF8;

	public override void Write(char value)
	{
		switch (value) {
			case '\n':
				onLine(_line.ToString());
				_line.Clear();
				break;
			case '\r':
				break;
			default:
				_line.Append(value);
				break;
		}
	}
}
