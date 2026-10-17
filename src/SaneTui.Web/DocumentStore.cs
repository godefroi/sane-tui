using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SaneTui.Sane;

namespace SaneTui.Web;

/// <summary>
/// The document being scanned: a folder of JPEG pages and a manifest giving their order. It's all on disk, so a long feeder run
/// doesn't sit in memory and a restart picks up where it left off. Several scans add to the same document until it's discarded.
/// </summary>
internal sealed partial class DocumentStore
{
	private const string ManifestName = "document.json";

	private readonly object _lock = new();
	private readonly string _folder;
	private Manifest? _current;

	public DocumentStore(IOptions<WebOptions> options)
	{
		_folder = Path.Combine(Path.GetFullPath(options.Value.DataFolder), "documents");

		Directory.CreateDirectory(_folder);

		_current = Directory.EnumerateDirectories(_folder)
			.Select(d => Path.Combine(d, ManifestName))
			.Where(File.Exists)
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.Select(Read)
			.FirstOrDefault();
	}

	public DocumentInfo? Current
	{
		get {
			lock (_lock) {
				return _current is null ? null : new DocumentInfo(_current.Id, [.. _current.Pages]);
			}
		}
	}

	/// <summary>Writes a scanned page into the current document, starting one if there isn't one.</summary>
	public PageInfo AddPage(SaneImage image, double? dpi, bool rotate180)
	{
		string id;
		string path;

		// The number is taken under the lock but the file is written outside it: encoding takes a while, and readers shouldn't wait for it.
		lock (_lock) {
			_current ??= Start();
			id = $"p{_current.NextPage++:0000}";
			path = FileFor(_current.Id, id);
		}

		ImageWriter.WriteJpeg(image, dpi, path, overwrite: false, rotate180);

		var page = new PageInfo(id, image.Width, image.Height);

		lock (_lock) {
			_current!.Pages.Add(page);
			Save(_current);
		}

		return page;
	}

	/// <summary>The file for a page of the current document, or null if there's no such page. The id is checked against the manifest, never used to build a path unchecked.</summary>
	public string? PagePath(string id)
	{
		lock (_lock) {
			return _current is not null && PageId().IsMatch(id) && _current.Pages.Any(p => p.Id == id) ? FileFor(_current.Id, id) : null;
		}
	}

	/// <summary>The page files in order.</summary>
	public IReadOnlyList<string> PagePaths()
	{
		lock (_lock) {
			return _current is null ? [] : [.. _current.Pages.Select(p => FileFor(_current.Id, p.Id))];
		}
	}

	public bool DeletePage(string id)
	{
		lock (_lock) {
			if (_current is null || _current.Pages.RemoveAll(p => p.Id == id) == 0) {
				return false;
			}

			File.Delete(FileFor(_current.Id, id));
			Save(_current);

			return true;
		}
	}

	/// <summary>Throws the current document away, pages and all.</summary>
	public void Discard()
	{
		lock (_lock) {
			if (_current is not null) {
				Directory.Delete(Path.Combine(_folder, _current.Id), recursive: true);
				_current = null;
			}
		}
	}

	private string FileFor(string documentId, string pageId) => Path.Combine(_folder, documentId, pageId + ".jpg");

	private Manifest Start()
	{
		var manifest = new Manifest { Id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x1000, 0xFFFF):x}" };

		Directory.CreateDirectory(Path.Combine(_folder, manifest.Id));
		Save(manifest);

		return manifest;
	}

	// written under a temporary name and renamed, so a crash can't leave a half-written manifest that loses the document
	private void Save(Manifest manifest)
	{
		var path = Path.Combine(_folder, manifest.Id, ManifestName);
		var temp = path + ".tmp";

		File.WriteAllText(temp, JsonSerializer.Serialize(manifest));
		File.Move(temp, path, overwrite: true);
	}

	private static Manifest? Read(string path)
	{
		try {
			return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path));
		} catch (Exception ex) when (ex is JsonException or IOException) {
			return null; // an unreadable manifest is skipped; its pages are still on disk
		}
	}

	[GeneratedRegex(@"^p\d{4,}$")]
	private static partial Regex PageId();

	private sealed class Manifest
	{
		public string Id { get; set; } = "";

		public int NextPage { get; set; } = 1;

		public List<PageInfo> Pages { get; set; } = [];
	}
}
