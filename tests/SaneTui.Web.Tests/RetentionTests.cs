using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SaneTui.Sane;

namespace SaneTui.Web.Tests;

public class RetentionTests
{
	private static readonly TimeSpan Thirty = TimeSpan.FromDays(30);

	private static SaneImage Picture() => new(8, 8, SanePixelFormat.Gray, 8, 8, new byte[64]);

	private static DocumentStore Store(string folder) => new(Options.Create(new WebOptions { DataFolder = folder }));

	// a folder as a document makes it, with its files last written the given time ago
	private static string Document(string root, string id, TimeSpan age)
	{
		var folder = Path.Combine(root, "documents", id);

		Directory.CreateDirectory(folder);
		File.WriteAllText(Path.Combine(folder, "document.json"), "{}");
		File.WriteAllBytes(Path.Combine(folder, "p0001.jpg"), [1, 2, 3]);

		foreach (var file in Directory.GetFiles(folder)) {
			File.SetLastWriteTimeUtc(file, DateTime.UtcNow - age);
		}

		Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow - age);

		return folder;
	}

	private static void Age(string folder, TimeSpan age)
	{
		foreach (var file in Directory.GetFiles(folder)) {
			File.SetLastWriteTimeUtc(file, DateTime.UtcNow - age);
		}

		Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow - age);
	}

	[Test]
	public async Task ADocumentNotChangedForLongerThanTheLimit_IsRemoved_AndTheCurrentOneGoesWithIt()
	{
		using var dir = new TempFolder();
		var store = Store(dir.Path);

		store.AddPage(Picture(), 150, default);

		var id = store.Current!.Id;

		Age(Path.Combine(dir.Path, "documents", id), TimeSpan.FromDays(31));

		var result = store.RemoveOlderThan(Thirty, DateTime.UtcNow, CancellationToken.None);

		await Assert.That(result.Removed).IsEqualTo(1);
		await Assert.That(store.Current).IsNull();
		await Assert.That(Directory.Exists(Path.Combine(dir.Path, "documents", id))).IsFalse();
		await Assert.That(Store(dir.Path).Current).IsNull();
	}

	[Test]
	public async Task ADocumentChangedRecently_IsKept()
	{
		using var dir = new TempFolder();
		var store = Store(dir.Path);

		store.AddPage(Picture(), 150, default);
		Age(Path.Combine(dir.Path, "documents", store.Current!.Id), TimeSpan.FromDays(29));

		var result = store.RemoveOlderThan(Thirty, DateTime.UtcNow, CancellationToken.None);

		await Assert.That(result.Removed).IsEqualTo(0);
		await Assert.That(store.Current).IsNotNull();
	}

	[Test]
	public async Task TheAgeIsThatOfTheNewestFile_SoAPageAddedLateStartsItAfresh()
	{
		using var dir = new TempFolder();
		var folder = Document(dir.Path, "20240101-120000-abcd", TimeSpan.FromDays(100));

		File.WriteAllBytes(Path.Combine(folder, "p0002.jpg"), [4]); // written just now

		var result = Store(dir.Path).RemoveOlderThan(Thirty, DateTime.UtcNow, CancellationToken.None);

		await Assert.That(result.Removed).IsEqualTo(0);
		await Assert.That(Directory.Exists(folder)).IsTrue();
	}

	[Test]
	public async Task AFolderLeftBehindThatNothingLoads_IsRemovedWhenOld_AndKeptWhenNot()
	{
		using var dir = new TempFolder();
		var old = Document(dir.Path, "20240101-120000-abcd", TimeSpan.FromDays(60));
		var recent = Document(dir.Path, "20260101-120000-1234", TimeSpan.FromDays(2));
		var store = Store(dir.Path);

		var result = store.RemoveOlderThan(Thirty, DateTime.UtcNow, CancellationToken.None);

		await Assert.That(result.Removed).IsEqualTo(1);
		await Assert.That(Directory.Exists(old)).IsFalse();
		await Assert.That(Directory.Exists(recent)).IsTrue();
	}

	[Test]
	public async Task OnlyFoldersNamedLikeDocumentsAreEverTouched()
	{
		using var dir = new TempFolder();
		var other = Path.Combine(dir.Path, "documents", "my-own-notes");

		Directory.CreateDirectory(other);
		File.WriteAllText(Path.Combine(other, "keep.txt"), "not ours");
		File.SetLastWriteTimeUtc(Path.Combine(other, "keep.txt"), DateTime.UtcNow - TimeSpan.FromDays(900));
		Directory.SetLastWriteTimeUtc(other, DateTime.UtcNow - TimeSpan.FromDays(900));

		var result = Store(dir.Path).RemoveOlderThan(Thirty, DateTime.UtcNow, CancellationToken.None);

		await Assert.That(result.Removed).IsEqualTo(0);
		await Assert.That(File.Exists(Path.Combine(other, "keep.txt"))).IsTrue();
	}

	[Test]
	public async Task ASweepIsCancelledWithoutRemovingAnythingFurther()
	{
		using var dir = new TempFolder();
		var folder = Document(dir.Path, "20240101-120000-abcd", TimeSpan.FromDays(60));
		using var cancelled = new CancellationTokenSource();

		await cancelled.CancelAsync();

		await Assert.That(() => Store(dir.Path).RemoveOlderThan(Thirty, DateTime.UtcNow, cancelled.Token)).Throws<OperationCanceledException>();
		await Assert.That(Directory.Exists(folder)).IsTrue();
	}

	private static RetentionService ServiceFor(DocumentStore store, int days, FakeTimeProvider time)
	{
		var scanner = new ScannerService(store);

		return new RetentionService(store, scanner, Options.Create(new WebOptions { RetentionDays = days }), time, NullLogger<RetentionService>.Instance) { Interval = TimeSpan.FromHours(24) };
	}

	private static async Task<bool> Eventually(Func<bool> condition)
	{
		for (var i = 0; i < 200 && !condition(); i++) {
			await Task.Delay(25);
		}

		return condition();
	}

	[Test]
	public async Task TheServiceSweepsWhenItStarts_ThenOnceADay_UntilItIsStopped()
	{
		using var dir = new TempFolder();
		var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
		var store = Store(dir.Path);
		var first = Document(dir.Path, "20240101-120000-abcd", TimeSpan.FromDays(60));
		var service = ServiceFor(store, 30, time);

		await service.StartAsync(CancellationToken.None);

		await Assert.That(await Eventually(() => !Directory.Exists(first))).IsTrue(); // at the start

		// a document that's recent now becomes old as the days pass, and the next day's sweep takes it
		var second = Document(dir.Path, "20260101-120000-1234", TimeSpan.FromDays(1));

		time.Advance(TimeSpan.FromDays(10));
		await Task.Delay(100);
		await Assert.That(Directory.Exists(second)).IsTrue(); // 11 days old: kept

		time.Advance(TimeSpan.FromDays(25)); // each advance of a day triggers a tick; the sweeps see the clock move on
		await Assert.That(await Eventually(() => !Directory.Exists(second))).IsTrue();

		await service.StopAsync(CancellationToken.None);
	}

	[Test]
	public async Task WithRetentionOff_NothingIsEverRemoved()
	{
		using var dir = new TempFolder();
		var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
		var folder = Document(dir.Path, "20240101-120000-abcd", TimeSpan.FromDays(900));
		var service = ServiceFor(Store(dir.Path), 0, time);

		await service.StartAsync(CancellationToken.None);
		time.Advance(TimeSpan.FromDays(5));
		await Task.Delay(200);
		await service.StopAsync(CancellationToken.None);

		await Assert.That(Directory.Exists(folder)).IsTrue();
	}

	[Test]
	public async Task StoppingTheHost_StopsTheServiceAtOnce_WhateverTheIntervalIs()
	{
		using var dir = new TempFolder();
		var service = ServiceFor(Store(dir.Path), 30, new FakeTimeProvider(DateTimeOffset.UtcNow));

		await service.StartAsync(CancellationToken.None);

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

		await service.StopAsync(timeout.Token); // would throw if the wait for the next day ignored the stop

		await Assert.That(timeout.IsCancellationRequested).IsFalse();
	}
}
