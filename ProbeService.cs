using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastSTRM.Configuration;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FastSTRM;

/// <summary>
/// Background service that provides library-wide .strm pre-probing
/// and automatic probing of newly added items.
/// </summary>
public sealed class ProbeService : IHostedService, IDisposable
{
    /// <summary>
    /// Upper bound for the per-item playback-probe attempt map. Entries older than
    /// <see cref="PlaybackProbeRetryInterval"/> are pruned first; the oldest entry is dropped
    /// when a very large library still exceeds the bound inside the retry window.
    /// </summary>
    private const int MaxTrackedPlaybackAttempts = 4096;

    /// <summary>
    /// How long shutdown waits for background probes to finish before giving up.
    /// </summary>
    private static readonly TimeSpan ShutdownTaskTimeout = TimeSpan.FromSeconds(10);

    private readonly ILibraryManager _libraryManager;
    private readonly IDirectoryService _directoryService;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<ProbeService> _logger;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _shutdownCts;
    private Task? _probeTask;
    private int _probeGeneration;
    private volatile bool _stopping;
    private readonly object _lock = new();

    /// <summary>
    /// Items whose background playback probe is currently running, so one item is never probed twice at once.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _playbackProbeInFlight = new();

    /// <summary>
    /// Cancellation source of the background playback probe per item, so stopping playback can
    /// abort a probe that is waiting for its delay or running an ffprobe.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _playbackProbeCancellation = new();

    /// <summary>
    /// Background playback probe task per item, so shutdown can wait for them instead of
    /// abandoning work that is still touching library services.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, Task> _playbackProbeTasks = new();

    /// <summary>
    /// Last time a background playback probe was attempted per item. Prevents a failing remote
    /// source from being re-probed on every playback progress report. The map is pruned so it
    /// cannot grow with the number of distinct items ever played.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, DateTime> _lastPlaybackProbeAttemptUtc = new();

    /// <summary>
    /// Automatic (item added) probe tasks per item, so shutdown can cancel and wait for them
    /// and duplicate ItemAdded events for the same item do not probe it concurrently.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, Task> _autoProbeTasks = new();

    /// <summary>
    /// Items whose automatic (item added) probe is currently running.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _autoProbeInFlight = new();

    private static readonly TimeSpan PlaybackProbeRetryInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Delays between consecutive background playback-probe attempts. A probe often fails for
    /// transient reasons — the upstream .strm endpoint returning 5xx while the client is opening
    /// the very same file, a cloud-drive rate limit, a slow redirect — so a short retry usually
    /// succeeds without waiting for the next playback.
    /// Tune these values to trade retry aggressiveness against load on the stream endpoint.
    /// </summary>
    private static readonly TimeSpan[] PlaybackProbeRetryDelays =
    {
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    };

    /// <summary>Gets the current task state: Idle, Running, or Stopping.</summary>
    public string State { get; private set; } = "Idle";

    /// <summary>Gets the total number of items in the current probe batch.</summary>
    public int TotalItems { get; private set; }

    /// <summary>Gets the number of items already processed.</summary>
    public int ProcessedItems { get; private set; }

    /// <summary>Gets the name of the item currently being probed.</summary>
    public string CurrentItemName { get; private set; } = string.Empty;

    public ProbeService(
        ILibraryManager libraryManager,
        IDirectoryService directoryService,
        ISessionManager sessionManager,
        ILogger<ProbeService> logger)
    {
        _libraryManager = libraryManager;
        _directoryService = directoryService;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    // ── IHostedService lifecycle ──

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = false;

        // Replace a source left over from a previous start so it does not stay undisposed.
        _shutdownCts?.Dispose();
        _shutdownCts = new CancellationTokenSource();

        // Unsubscribe first: a second StartAsync must not register the handlers twice.
        _libraryManager.ItemAdded -= OnItemAdded;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _libraryManager.ItemAdded += OnItemAdded;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;

        _libraryManager.ItemAdded -= OnItemAdded;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;

        try
        {
            _shutdownCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already shut down.
        }
        catch (AggregateException ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Error while cancelling pending background probes");
        }

        StopProbe();

        var pending = new List<Task>();
        Task? batchTask;
        lock (_lock)
        {
            batchTask = _probeTask;
        }

        if (batchTask is not null)
        {
            pending.Add(batchTask);
        }

        pending.AddRange(_playbackProbeTasks.Values);
        pending.AddRange(_autoProbeTasks.Values);

        var running = pending.Where(task => !task.IsCompleted).ToArray();
        if (running.Length == 0)
        {
            return;
        }

        // Do not let a stuck ffprobe hold server shutdown forever; the host has its own timeout too.
        var all = Task.WhenAll(running);
        var completed = await Task.WhenAny(all, Task.Delay(ShutdownTaskTimeout, cancellationToken))
            .ConfigureAwait(false);
        if (!ReferenceEquals(completed, all))
        {
            _logger.LogWarning(
                "FastSTRM: {Count} background probe task(s) did not finish within {Timeout}s of shutdown",
                running.Length,
                (int)ShutdownTaskTimeout.TotalSeconds);
            return;
        }

        try
        {
            await all.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Background probe task failed during shutdown");
        }
    }

    // ── Auto-probe on item added ──

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (_stopping)
        {
            return;
        }

        var config = Plugin.Instance?.Configuration;
        if (config is not { EnableAutoProbe: true })
        {
            return;
        }

        var item = e.Item;
        if (!IsStrmItem(item) || HasStreamInfo(item))
        {
            return;
        }

        if (!_autoProbeInFlight.TryAdd(item.Id, 0))
        {
            // The same item can raise ItemAdded more than once during a scan.
            return;
        }

        var token = _shutdownCts?.Token ?? CancellationToken.None;
        try
        {
            var task = Task.Run(() => RunAutoProbeAsync(item, token));
            _autoProbeTasks[item.Id] = task;
            if (task.IsCompleted)
            {
                _autoProbeTasks.TryRemove(item.Id, out _);
            }
        }
        catch (Exception ex)
        {
            // Never let a scheduling failure both escape the event handler and leave the item
            // permanently marked as "already being probed".
            _autoProbeInFlight.TryRemove(item.Id, out _);
            _logger.LogDebug(ex, "FastSTRM: Unable to schedule the auto-probe for {Name}", item.Name);
        }
    }

    private async Task RunAutoProbeAsync(BaseItem item, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("FastSTRM: Auto-probing new item {Name}", item.Name);
            await ProbeItemAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("FastSTRM: Auto-probe of {Name} was cancelled", item.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FastSTRM: Auto-probe failed for {Name}", item.Name);
        }
        finally
        {
            _autoProbeInFlight.TryRemove(item.Id, out _);
            _autoProbeTasks.TryRemove(item.Id, out _);
        }
    }

    // ── Playback stop handling ──

    /// <summary>
    /// Cancels the background probe of an item as soon as its playback stops, unless the
    /// administrator opted into letting the probe finish. A probe that is still waiting for its
    /// initial delay is cancelled immediately; a running ffprobe receives the cancellation token.
    /// </summary>
    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (Plugin.Instance?.Configuration.CancelBackgroundProbeOnPlaybackStop != true)
        {
            return;
        }

        var item = e.Item;
        if (item is null)
        {
            return;
        }

        // Another client may still be playing the same item. Stopping one session must not
        // abort the probe that the remaining session still needs.
        if (IsAnotherSessionPlaying(item, e.Session))
        {
            _logger.LogDebug(
                "FastSTRM: Playback of {Name} stopped in one session but another session is still playing it; keeping its background probe",
                item.Name);
            return;
        }

        if (CancelPlaybackProbe(item.Id))
        {
            _logger.LogInformation(
                "FastSTRM: Playback of {Name} stopped, cancelling its background probe",
                item.Name);
        }
    }

    private bool CancelPlaybackProbe(Guid itemId)
    {
        if (!_playbackProbeCancellation.TryGetValue(itemId, out var cts))
        {
            return false;
        }

        // Allow a probe on the next playback even if this one was cancelled.
        _lastPlaybackProbeAttemptUtc.TryRemove(itemId, out _);

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (AggregateException ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Error while cancelling the background probe of {ItemId}", itemId);
            return false;
        }
    }

    /// <summary>
    /// Returns true when a session other than <paramref name="stoppingSession"/> is still playing
    /// the item, so stopping one client does not cancel a probe another client still needs.
    /// </summary>
    private bool IsAnotherSessionPlaying(BaseItem item, SessionInfo? stoppingSession)
    {
        try
        {
            var stoppingSessionId = stoppingSession?.Id;
            return _sessionManager.Sessions.Any(session =>
                (stoppingSessionId is null
                    || !string.Equals(session.Id, stoppingSessionId, StringComparison.OrdinalIgnoreCase))
                && session.NowPlayingItem is not null
                && session.NowPlayingItem.Id == item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Unable to inspect playback sessions for {Name}", item.Name);
            return false;
        }
    }

    /// <summary>
    /// Returns true when no active session is playing the item anymore. Used right before each
    /// probe attempt so a probe that was scheduled or re-scheduled around the stop event cannot
    /// outlive playback when cancellation on stop is enabled.
    /// </summary>
    private bool ShouldSkipProbeForStoppedPlayback(BaseItem item)
    {
        if (Plugin.Instance?.Configuration.CancelBackgroundProbeOnPlaybackStop != true)
        {
            return false;
        }

        try
        {
            var sessions = _sessionManager.Sessions.ToList();
            if (sessions.Count == 0)
            {
                // Without session information there is nothing to prove that playback stopped.
                return false;
            }

            return !sessions.Any(session =>
                session.NowPlayingItem is not null
                && session.NowPlayingItem.Id == item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Unable to inspect playback sessions for {Name}", item.Name);
            return false;
        }
    }

    // ── Public API ──

    /// <summary>
    /// Returns a summary of .strm items in the library.
    /// </summary>
    public (int Total, int Probed, int Unprobed) GetSummary()
    {
        var items = GetStrmItems();
        int probed = items.Count(HasStreamInfo);
        return (items.Count, probed, items.Count - probed);
    }

    /// <summary>
    /// Starts a probe task. Returns false if one is already running or still stopping.
    /// </summary>
    /// <param name="mode">"Incremental" or "Full".</param>
    public bool StartProbe(string mode)
    {
        lock (_lock)
        {
            // "Stopping" must also block a new start: the previous task is still winding down and
            // its finally block is the current owner of State/_cts.
            if (State != "Idle")
            {
                return false;
            }

            var items = GetStrmItems();
            if (string.Equals(mode, "Incremental", StringComparison.OrdinalIgnoreCase))
            {
                items = items.Where(i => !HasStreamInfo(i)).ToList();
            }

            TotalItems = items.Count;
            ProcessedItems = 0;
            CurrentItemName = string.Empty;
            State = "Running";

            var cts = new CancellationTokenSource();
            _cts = cts;
            var generation = ++_probeGeneration;
            _probeTask = Task.Run(() => RunProbeAsync(items, cts, generation));
            return true;
        }
    }

    /// <summary>
    /// Requests cancellation of the running probe task.
    /// </summary>
    public void StopProbe()
    {
        lock (_lock)
        {
            if (State != "Running")
            {
                return;
            }

            State = "Stopping";
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The probe finished between the state check and the cancel.
            }
            catch (AggregateException ex)
            {
                _logger.LogDebug(ex, "FastSTRM: Error while cancelling the probe task");
            }
        }
    }

    /// <summary>
    /// Schedules a background probe for an unprobed .strm item that is being played.
    /// Playback itself is never blocked: the client keeps its instant direct-play start, and the
    /// probe waits <paramref name="delaySeconds"/> (the administrator-configured delay, default
    /// 5 seconds) so it does not compete with the player opening the stream. A probe that yields
    /// no runtime (typically a transient 5xx from the stream endpoint) is retried a few times at
    /// short intervals before giving up until the next playback.
    /// </summary>
    /// <param name="item">The item that is being played.</param>
    /// <param name="delaySeconds">Seconds to wait after playback started before probing.</param>
    public void SchedulePlaybackProbe(BaseItem item, int delaySeconds)
    {
        if (_stopping || !IsStrmItem(item) || HasStreamInfo(item))
        {
            return;
        }

        // A running batch probe already covers unprobed items; do not probe the same
        // item concurrently with it.
        if (State != "Idle")
        {
            return;
        }

        var now = DateTime.UtcNow;
        PrunePlaybackProbeAttempts(now);
        if (_lastPlaybackProbeAttemptUtc.TryGetValue(item.Id, out var lastAttempt)
            && now - lastAttempt < PlaybackProbeRetryInterval)
        {
            return;
        }

        if (!_playbackProbeInFlight.TryAdd(item.Id, 0))
        {
            return;
        }

        try
        {
            _lastPlaybackProbeAttemptUtc[item.Id] = now;

            var initialDelay = TimeSpan.FromSeconds(
                Math.Clamp(delaySeconds, 0, PluginConfiguration.MaxBackgroundProbeDelaySeconds));

            var probeCts = CancellationTokenSource.CreateLinkedTokenSource(
                _shutdownCts?.Token ?? CancellationToken.None);
            _playbackProbeCancellation[item.Id] = probeCts;

            var task = Task.Run(() => RunPlaybackProbeAsync(item, initialDelay, probeCts));
            _playbackProbeTasks[item.Id] = task;
            if (task.IsCompleted)
            {
                // The probe can finish before the assignment above; do not leave a completed
                // task in the map (shutdown would otherwise keep awaiting stale entries).
                _playbackProbeTasks.TryRemove(item.Id, out _);
            }
        }
        catch (Exception ex)
        {
            // Roll back every marker so a transient failure cannot permanently disable the
            // background probe of this item.
            _playbackProbeInFlight.TryRemove(item.Id, out _);
            _lastPlaybackProbeAttemptUtc.TryRemove(item.Id, out _);
            if (_playbackProbeCancellation.TryRemove(item.Id, out var cts))
            {
                cts.Dispose();
            }

            _logger.LogDebug(ex, "FastSTRM: Unable to schedule a background probe for {Name}", item.Name);
        }
    }

    private async Task RunPlaybackProbeAsync(
        BaseItem item,
        TimeSpan initialDelay,
        CancellationTokenSource probeCts)
    {
        try
        {
            var token = probeCts.Token;

            _logger.LogInformation(
                "FastSTRM: Playback started before {Name} was probed, filling in duration in the background so playback progress can be tracked",
                item.Name);

            _logger.LogDebug(
                "FastSTRM: Waiting {Delay}s before probing {Name} so the probe does not compete with the player opening the same stream",
                (int)initialDelay.TotalSeconds,
                item.Name);

            await Task.Delay(initialDelay, token).ConfigureAwait(false);

            for (var attempt = 0; ; attempt++)
            {
                if (State != "Idle")
                {
                    // A batch probe is running and already covers every unprobed item.
                    _logger.LogDebug(
                        "FastSTRM: Dropping background probe of {Name} because a batch probe is running",
                        item.Name);
                    return;
                }

                // Playback may have stopped while the probe was waiting, and a stop that races
                // with scheduling may not have reached the cancellation source in time.
                if (ShouldSkipProbeForStoppedPlayback(item))
                {
                    _lastPlaybackProbeAttemptUtc.TryRemove(item.Id, out _);
                    _logger.LogInformation(
                        "FastSTRM: Playback of {Name} has stopped, skipping the background probe",
                        item.Name);
                    return;
                }

                // A failed probe does not throw: Jellyfin logs the provider error and lets the
                // refresh finish, so the runtime is the only reliable success signal.
                await ProbeItemAsync(item, token).ConfigureAwait(false);

                var probedRuntimeTicks = GetProbedRuntimeTicks(item);
                if (probedRuntimeTicks is > 0)
                {
                    // The item now has stream info, so the "next playback may retry" marker is
                    // dead weight; drop it instead of keeping one entry per probed library item.
                    _lastPlaybackProbeAttemptUtc.TryRemove(item.Id, out _);

                    _logger.LogInformation(
                        "FastSTRM: Background probe of {Name} finished (RunTimeTicks={RunTimeTicks})",
                        item.Name,
                        probedRuntimeTicks);
                    return;
                }

                if (attempt >= PlaybackProbeRetryDelays.Length)
                {
                    _logger.LogWarning(
                        "FastSTRM: Background probe of {Name} produced no stream info after {Attempts} attempt(s) "
                        + "(RunTimeTicks=null). Playback and progress tracking are unaffected; the next playback "
                        + "retries it, at most once every {Interval}",
                        item.Name,
                        attempt + 1,
                        PlaybackProbeRetryInterval);
                    return;
                }

                var retryDelay = PlaybackProbeRetryDelays[attempt];
                _logger.LogWarning(
                    "FastSTRM: Background probe of {Name} produced no stream info (RunTimeTicks=null); "
                    + "retrying in {Delay}s (retry {Retry}/{MaxRetries})",
                    item.Name,
                    (int)retryDelay.TotalSeconds,
                    attempt + 1,
                    PlaybackProbeRetryDelays.Length);

                await Task.Delay(retryDelay, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "FastSTRM: Background probe of {Name} was cancelled because playback stopped or the server is shutting down",
                item.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FastSTRM: Background playback probe failed for {Name}", item.Name);
        }
        finally
        {
            _playbackProbeInFlight.TryRemove(item.Id, out _);
            _playbackProbeTasks.TryRemove(item.Id, out _);
            if (_playbackProbeCancellation.TryRemove(item.Id, out var cts))
            {
                cts.Dispose();
            }
        }
    }

    // ── Internal ──

    private async Task RunProbeAsync(List<BaseItem> items, CancellationTokenSource cts, int generation)
    {
        var ct = cts.Token;

        try
        {
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                CurrentItemName = item.Name ?? item.Path ?? string.Empty;
                _logger.LogInformation("FastSTRM: Probing [{Index}/{Total}] {Name}",
                    ProcessedItems + 1, TotalItems, CurrentItemName);

                try
                {
                    await ProbeItemAsync(item, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "FastSTRM: Probe failed for {Name}, skipping", CurrentItemName);
                }

                ProcessedItems++;
            }
        }
        catch (Exception ex)
        {
            // Keep the task from faulting with an exception nobody observes; the per-item
            // failures above already logged themselves.
            _logger.LogError(ex, "FastSTRM: Probe task failed unexpectedly");
        }
        finally
        {
            lock (_lock)
            {
                // A newer StartProbe may already own State/_cts; only the owning run may reset them.
                if (generation == _probeGeneration)
                {
                    State = "Idle";
                    CurrentItemName = string.Empty;
                    _cts = null;
                    _probeTask = null;
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Triggers the official Jellyfin metadata refresh with remote content probing
    /// for a single item. This writes real probe data into the database.
    /// </summary>
    private async Task ProbeItemAsync(BaseItem item, CancellationToken ct)
    {
        await item.RefreshMetadata(
            new MetadataRefreshOptions(_directoryService)
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh
            },
            ct).ConfigureAwait(false);
    }

    private List<BaseItem> GetStrmItems()
    {
        return _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                MediaTypes = new[] { MediaType.Video },
                Recursive = true
            })
            .Where(IsStrmItem)
            .ToList();
    }

    private static bool IsStrmItem(BaseItem item)
        => item.Path is not null
           && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

    private static bool HasStreamInfo(BaseItem item)
        => item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0;

    /// <summary>
    /// Keeps the playback-probe attempt map bounded. Entries are only needed for the retry
    /// throttle window, so anything older is dead. If a very large library still exceeds the
    /// bound inside the window, the oldest record is dropped to guarantee the bound.
    /// </summary>
    private void PrunePlaybackProbeAttempts(DateTime now)
    {
        if (_lastPlaybackProbeAttemptUtc.Count < MaxTrackedPlaybackAttempts)
        {
            return;
        }

        var cutoff = now - PlaybackProbeRetryInterval;
        foreach (var pair in _lastPlaybackProbeAttemptUtc)
        {
            if (pair.Value < cutoff)
            {
                _lastPlaybackProbeAttemptUtc.TryRemove(pair.Key, out _);
            }
        }

        if (_lastPlaybackProbeAttemptUtc.Count < MaxTrackedPlaybackAttempts)
        {
            return;
        }

        var oldestKey = Guid.Empty;
        var oldestValue = DateTime.MaxValue;
        foreach (var pair in _lastPlaybackProbeAttemptUtc)
        {
            if (pair.Value < oldestValue)
            {
                oldestValue = pair.Value;
                oldestKey = pair.Key;
            }
        }

        if (oldestKey != Guid.Empty)
        {
            _lastPlaybackProbeAttemptUtc.TryRemove(oldestKey, out _);
        }
    }

    /// <summary>
    /// Returns the runtime a probe actually stored, or null when the probe produced nothing.
    /// The instance passed to the interceptor is not necessarily the instance Jellyfin keeps in
    /// its cache, so re-read the item from the library before declaring the probe failed.
    /// </summary>
    private long? GetProbedRuntimeTicks(BaseItem item)
    {
        if (HasStreamInfo(item))
        {
            return item.RunTimeTicks;
        }

        try
        {
            var current = _libraryManager.GetItemById(item.Id);
            return current is not null && HasStreamInfo(current) ? current.RunTimeTicks : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Unable to re-read {Name} after probing", item.Name);
            return null;
        }
    }

    public void Dispose()
    {
        _stopping = true;

        try
        {
            _shutdownCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException)
        {
        }

        StopProbe();

        // The batch probe task owns _cts and disposes it in RunProbeAsync's finally block.
        // Disposing it here could race with a running RefreshMetadata call.
        _shutdownCts?.Dispose();
    }
}
