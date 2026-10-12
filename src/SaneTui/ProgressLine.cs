namespace SaneTui;

/// <summary>
/// Draws "Label 42%" on one line, redrawn in place as the scan progresses. Only for terminals: the carriage returns
/// make a mess of a redirected log. Reports arrive on the scanner's background thread.
/// </summary>
internal sealed class ProgressLine(TextWriter log) : IProgress<double>
{
	private string _label = "";
	private int _percent = -1; // -1 means nothing has been drawn for the current label

	/// <summary>Ends the line being drawn, if any, and sets the label for the next one.</summary>
	public void StartPage(string label)
	{
		Finish();

		_label = label;
	}

	public void Report(double fraction)
	{
		var percent = (int)Math.Round(fraction * 100);

		if (percent == _percent) {
			return;
		}

		_percent = percent;
		log.Write($"\r{_label} {percent}%");
	}

	/// <summary>Moves to a new line so the next message doesn't land on top of the percentage. Does nothing if nothing was drawn.</summary>
	public void Finish()
	{
		if (_percent >= 0) {
			log.WriteLine();

			_percent = -1;
		}
	}
}
