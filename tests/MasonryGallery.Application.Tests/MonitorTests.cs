using System.Collections.Concurrent;
using System.Reactive.Subjects;
using FluentAssertions;
using MasonryGallery.Application.Ports;
using MasonryGallery.Application.UseCases;
using MasonryGallery.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace MasonryGallery.Application.Tests;

/// <summary>Records scans; each one waits on <see cref="Gate"/> so tests can queue requests behind it.</summary>
internal sealed class FakeScanner : ILibraryScanner
{
    public readonly ConcurrentQueue<string> Calls = new();
    public TaskCompletionSource Gate = Completed();
    public Func<IReadOnlyCollection<string>, IReadOnlyCollection<string>> Deferred = _ => [];
    public Func<LibraryId, Result> FullResult = _ => Result.Success();

    public static TaskCompletionSource Completed()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    public async Task<Result> ExecuteAsync(LibraryId libraryId, CancellationToken ct = default)
    {
        Calls.Enqueue($"full:{libraryId.Value}");
        await Gate.Task.WaitAsync(ct);
        return FullResult(libraryId);
    }

    public async Task<Result<IReadOnlyCollection<string>>> ExecutePathsAsync(
        LibraryId libraryId, IReadOnlyCollection<string> relPaths, CancellationToken ct = default)
    {
        Calls.Enqueue($"paths:{libraryId.Value}:{string.Join(",", relPaths.Order())}");
        await Gate.Task.WaitAsync(ct);
        return Result<IReadOnlyCollection<string>>.Success(Deferred(relPaths));
    }
}

internal sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.CompletedTask;
}

public class SchedulerTests
{
    private static readonly LibraryId A = new(1), B = new(2);
    private readonly FakeScanner _scanner = new();
    private readonly ScanScheduler _scheduler;

    public SchedulerTests() => _scheduler = new ScanScheduler(_scanner, new FakeClock(), NullLogger<ScanScheduler>.Instance);

    [Fact]
    public async Task Requests_during_a_scan_are_merged_and_run_after_it()
    {
        _scanner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scheduler.RequestFullScan(A);
        await WaitUntil(() => _scanner.Calls.Count == 1);

        _scheduler.RequestPaths(A, ["x.jpg"]);
        _scheduler.RequestPaths(A, ["y.jpg", "x.jpg"]);
        _scheduler.IsScanning(A).Should().BeTrue();
        _scanner.Gate.SetResult();
        await _scheduler.WhenIdleAsync();

        _scanner.Calls.Should().Equal("full:1", "paths:1:x.jpg,y.jpg");
        _scheduler.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task A_queued_full_scan_absorbs_path_requests()
    {
        _scanner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scheduler.RequestPaths(A, ["first.jpg"]);
        await WaitUntil(() => _scanner.Calls.Count == 1);

        _scheduler.RequestPaths(A, ["x.jpg"]);
        _scheduler.RequestFullScan(A);
        _scheduler.RequestPaths(A, ["y.jpg"]);
        _scanner.Gate.SetResult();
        await _scheduler.WhenIdleAsync();

        _scanner.Calls.Should().Equal("paths:1:first.jpg", "full:1");
    }

    [Fact]
    public async Task Libraries_scan_in_parallel_and_completion_is_reported()
    {
        var done = new ConcurrentBag<ScanCompleted>();
        _scheduler.Completed += done.Add;
        _scanner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _scheduler.RequestFullScan(A);
        _scheduler.RequestFullScan(B);
        await WaitUntil(() => _scanner.Calls.Count == 2); // both started before either finished
        _scanner.Gate.SetResult();
        await _scheduler.WhenIdleAsync();

        done.Select(d => (d.LibraryId, d.Kind, d.Result.IsSuccess))
            .Should().BeEquivalentTo([(A, ScanKind.Full, true), (B, ScanKind.Full, true)]);
    }

    // Photino runs UI work synchronously: a handler that marshals to the UI thread waits for it, and the UI
    // thread reads IsBusy while rendering. Raising BusyChanged under the scheduler lock deadlocked the app.
    [Fact]
    public async Task BusyChanged_handlers_can_wait_on_a_thread_that_reads_IsBusy()
    {
        var roundTrips = new ConcurrentBag<bool>();
        _scheduler.BusyChanged += () => roundTrips.Add(Task.Run(() => _scheduler.IsBusy).Wait(TimeSpan.FromSeconds(2)));

        _scheduler.RequestFullScan(A);
        _scheduler.RequestPaths(B, ["x.jpg"]);
        await _scheduler.WhenIdleAsync();

        roundTrips.Should().NotBeEmpty().And.OnlyContain(completed => completed);
    }

    [Fact]
    public async Task Files_still_being_written_are_retried()
    {
        var first = true;
        _scanner.Deferred = paths => first && !(first = false) ? paths : [];

        _scheduler.RequestPaths(A, ["copying.mp4"]);
        await WaitUntil(() => _scanner.Calls.Count == 2);
        await _scheduler.WhenIdleAsync();

        _scanner.Calls.Should().Equal("paths:1:copying.mp4", "paths:1:copying.mp4");
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }
}

public class LibraryMonitorTests
{
    private static readonly LibraryId A = new(1);
    private static readonly MonitorOptions Options =
        new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));

    private readonly FakeScanner _scanner = new();
    private readonly FakeClock _clock = new();
    private readonly ILibraryRepository _libraries = Substitute.For<ILibraryRepository>();
    private readonly IFileEnumerator _files = Substitute.For<IFileEnumerator>();
    private readonly FakeWatchers _watchers = new();
    private readonly ScanScheduler _scheduler;
    private readonly LibraryMonitor _monitor;
    private Library _library = new("Photos", "/photos") { Id = A };

    private sealed class FakeWatcher : IFileWatcher
    {
        public readonly Subject<WatchSignal> Subject = new();
        public bool Disposed;
        public IObservable<WatchSignal> Signals => Subject;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeWatchers : IFileWatcherFactory
    {
        public readonly List<FakeWatcher> Started = [];
        public string? Refuse;
        public Result<IFileWatcher> TryWatch(string rootPath)
        {
            if (Refuse is not null) return Result<IFileWatcher>.Failure(Refuse);
            var watcher = new FakeWatcher();
            Started.Add(watcher);
            return watcher;
        }
    }

    public LibraryMonitorTests()
    {
        _libraries.ListAsync(Arg.Any<CancellationToken>()).Returns(_ => new([_library]));
        _libraries.GetByIdAsync(A, Arg.Any<CancellationToken>()).Returns(_ => Result<Library>.Success(_library));
        _files.RootExists("/photos").Returns(true);
        _scheduler = new ScanScheduler(_scanner, _clock, NullLogger<ScanScheduler>.Instance);
        _monitor = new LibraryMonitor(_libraries, _watchers, _files, _scheduler, _clock,
            NullLogger<LibraryMonitor>.Instance, Options);
    }

    private async Task StartAsync()
    {
        await _monitor.StartAsync();
        await _scheduler.WhenIdleAsync();
        _scanner.Calls.Clear();
    }

    [Fact]
    public async Task Startup_scans_enabled_libraries_and_watches_them()
    {
        var hidden = new Library("Hidden", "/hidden") { Id = new LibraryId(2), Enabled = false };
        _libraries.ListAsync(Arg.Any<CancellationToken>()).Returns(new System.Collections.ObjectModel.ReadOnlyCollection<Library>([_library, hidden]));

        await _monitor.StartAsync();
        await _scheduler.WhenIdleAsync();

        _scanner.Calls.Should().Equal("full:1");
        _watchers.Started.Should().ContainSingle();
        _monitor.Statuses.Should().ContainKey(A).WhoseValue.Mode.Should().Be(WatchMode.Live);
    }

    [Fact]
    public async Task Watcher_signals_become_targeted_or_full_scans()
    {
        await StartAsync();
        var watcher = _watchers.Started.Single();

        watcher.Subject.OnNext(new WatchSignal.Changed(["new.jpg", "old"]));
        await _scheduler.WhenIdleAsync();
        watcher.Subject.OnNext(new WatchSignal.Overflow());
        await _scheduler.WhenIdleAsync();

        _scanner.Calls.Should().Equal("paths:1:new.jpg,old", "full:1");
    }

    [Fact]
    public async Task Failed_watcher_falls_back_to_polling_and_rescans()
    {
        await StartAsync();
        var watcher = _watchers.Started.Single();

        watcher.Subject.OnNext(new WatchSignal.Failed("inotify limit"));
        await _scheduler.WhenIdleAsync();

        watcher.Disposed.Should().BeTrue();
        _scanner.Calls.Should().Equal("full:1");
        // That full rescan retried live watching, which works again.
        _watchers.Started.Should().HaveCount(2);
        _monitor.Statuses[A].Mode.Should().Be(WatchMode.Live);
    }

    [Fact]
    public async Task Unwatchable_library_is_polled_on_the_shorter_interval()
    {
        _watchers.Refuse = "Network folder";
        await StartAsync();
        _monitor.Statuses[A].Should().Be(new WatchStatus(WatchMode.Polling, "Network folder"));

        _clock.UtcNow += TimeSpan.FromMinutes(4);
        _monitor.Tick();
        await _scheduler.WhenIdleAsync();
        _scanner.Calls.Should().BeEmpty();

        _clock.UtcNow += TimeSpan.FromMinutes(2);
        _monitor.Tick();
        await _scheduler.WhenIdleAsync();
        _scanner.Calls.Should().Equal("full:1");
    }

    [Fact]
    public async Task Watched_library_is_reconciled_every_15_minutes()
    {
        await StartAsync();

        _clock.UtcNow += TimeSpan.FromMinutes(14);
        _monitor.Tick();
        _clock.UtcNow += TimeSpan.FromMinutes(2);
        _monitor.Tick();
        _monitor.Tick(); // already queued: not requested twice
        await _scheduler.WhenIdleAsync();

        _scanner.Calls.Should().Equal("full:1");
    }

    [Fact]
    public async Task Offline_library_stops_watching_and_comes_back_when_the_folder_returns()
    {
        _scanner.FullResult = _ =>
        {
            _library = new Library("Photos", "/photos") { Id = A, Status = _files.RootExists("/photos") ? LibraryStatus.Online : LibraryStatus.Offline };
            return _library.IsOnline ? Result.Success() : Result.Failure("offline");
        };
        await StartAsync();
        var watcher = _watchers.Started.Single();

        _files.RootExists("/photos").Returns(false); // unplugged
        watcher.Subject.OnNext(new WatchSignal.Overflow());
        await _scheduler.WhenIdleAsync();
        watcher.Disposed.Should().BeTrue();
        _monitor.Statuses[A].Mode.Should().Be(WatchMode.Off);
        _scanner.Calls.Clear();

        _clock.UtcNow += TimeSpan.FromMinutes(2);
        _monitor.Tick(); // still gone: only a stat, no scan
        await _scheduler.WhenIdleAsync();
        _scanner.Calls.Should().BeEmpty();

        _files.RootExists("/photos").Returns(true); // plugged back in
        _clock.UtcNow += TimeSpan.FromMinutes(2);
        _monitor.Tick();
        await _scheduler.WhenIdleAsync();
        _scanner.Calls.Should().Equal("full:1");
        _monitor.Statuses[A].Mode.Should().Be(WatchMode.Live);
    }

    // Same deadlock one level up: the monitor used to request scans while holding its own lock,
    // so a BusyChanged handler waiting on the UI thread (which reads Statuses) never returned.
    [Fact]
    public async Task Scheduler_events_raised_by_the_monitor_can_wait_on_a_thread_that_reads_Statuses()
    {
        var roundTrips = new ConcurrentBag<bool>();
        _scheduler.BusyChanged += () => roundTrips.Add(Task.Run(() => _monitor.Statuses).Wait(TimeSpan.FromSeconds(2)));

        await _monitor.StartAsync();
        await _scheduler.WhenIdleAsync();
        _watchers.Started.Single().Subject.OnNext(new WatchSignal.Overflow());
        await _scheduler.WhenIdleAsync();

        roundTrips.Should().NotBeEmpty().And.OnlyContain(completed => completed);
    }

    [Fact]
    public async Task Disabled_library_releases_its_watcher()
    {
        await StartAsync();
        _library = new Library("Photos", "/photos") { Id = A, Enabled = false };

        await _monitor.SyncAsync();

        _watchers.Started.Single().Disposed.Should().BeTrue();
        _monitor.Statuses.Should().BeEmpty();
    }
}
