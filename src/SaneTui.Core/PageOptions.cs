namespace SaneTui;

/// <summary>What to do to each page before it's saved. All of it happens on the scanner's own pixels, ahead of the one JPEG encode.</summary>
/// <param name="Rotate180">Turn the picture half way round, for a page that was laid on the scanner upside-down. Loses nothing.</param>
/// <param name="Deskew">Straighten a page that was laid on crooked. This resamples the picture, which softens it slightly.</param>
/// <param name="Trim">Cut off the background around the page, found by the color of the picture's corner. Can cut into a page that has no margin.</param>
internal readonly record struct PageOptions(bool Rotate180 = false, bool Deskew = false, bool Trim = false);
