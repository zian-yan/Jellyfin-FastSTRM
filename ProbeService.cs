using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FastSTRM;

/// <summary>
/// Background service that provides library-wide .strm pre-probing
/// and automatic probing of newly added items.
/// </summary>
public sealed class ProbeService : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly IDirectoryService _directoryService;
    private readonly ILogger<ProbeService> _logger;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _shutdownCts;
    private Task? _probeTask;
    private readonly object _lock = new();

    /// <summary>
    /// Items whose background playback probe is currently running, so one item is never probed twice at once.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _playbackProbeInFlight = new();

    /// <summary>
    /// Last time a background playback probe was attempted per item. Prevents a failing remote
    /// source from being re-probed on every playback progress report.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, DateTime> _lastPlaybackProbeAttemptUtc = new();

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

    /// <summary>
    /// Delay before the first background probe of an item that is currently being played.
    /// The client still gets its instant direct-play start immediately; the probe waits until the
    /// player has opened the stream, so the two requests do not hit the stream endpoint at the same
    /// moment (which is a common cause of 5xx answers to the ffprobe request).
    /// </summary>
    private static readonly TimeSpan PlaybackProbeInitialDelay = TimeSpan.FromSeconds(10);

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
        ILogger<ProbeService> logger)
    {
        _libraryManager = libraryManager;
        _directoryService = directoryService;
        _logger = logger;
    }

    // ── IHostedService lifecycle ──

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _shutdownCts = new CancellationTokenSource();
        _libraryManager.ItemAdded += OnItemAdded;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;

        // Cancel pending retry delays so shutdown is not held up by a backoff timer.
        _shutdownCts?.Cancel();

        StopProbe();
        return Task.CompletedTask;
    }

    // ── Auto-probe on item added ──

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
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

        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("FastSTRM: Auto-probing new item {Name}", item.Name);
                await ProbeItemAsync(item, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FastSTRM: Auto-probe failed for {Name}", item.Name);
            }
        });
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
    /// Starts a probe task. Returns false if one is already running.
    /// </summary>
    /// <param name="mode">"Incremental" or "Full".</param>
    public bool StartProbe(string mode)
    {
        lock (_lock)
        {
            if (State == "Running")
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

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _probeTask = Task.Run(() => RunProbeAsync(items, ct));
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
            if (_cts is { IsCancellationRequested: false })
            {
                State = "Stopping";
                _cts.Cancel();
            }
        }
    }

    /// <summary>
    /// Schedules a background probe for an unprobed .strm item that is being played.
    /// Playback itself is never blocked: the client keeps its instant direct-play start, and the
    /// probe is scheduled <see cref="PlaybackProbeInitialDelay"/> later so it does not compete with
    /// the player opening the stream. A probe that yields no runtime (typically a transient 5xx
    /// from the stream endpoint) is retried a few times at short intervals before giving up until
    /// the next playback.
    /// </summary>
    /// <param name="item">The item that is being played.</param>
    public void SchedulePlaybackProbe(BaseItem item)
    {
        if (!IsStrmItem(item) || HasStreamInfo(item))
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
        if (_lastPlaybackProbeAttemptUtc.TryGetValue(item.Id, out var lastAttempt)
            && now - lastAttempt < PlaybackProbeRetryInterval)
        {
            return;
        }

        if (!_playbackProbeInFlight.TryAdd(item.Id, 0))
        {
            return;
        }

        _lastPlaybackProbeAttemptUtc[item.Id] = now;

        _ = Task.Run(async () =>
        {
            try
            {
                var shutdownToken = _shutdownCts?.Token ?? CancellationToken.None;

                _logger.LogInformation(
                    "FastSTRM: Playback started before {Name} was probed, filling in duration in the background so playback progress can be tracked",
                    item.Name);

                _logger.LogDebug(
                    "FastSTRM: Waiting {Delay}s before probing {Name} so the probe does not compete with the player opening the same stream",
                    (int)PlaybackProbeInitialDelay.TotalSeconds,
                    item.Name);

                await Task.Delay(PlaybackProbeInitialDelay, shutdownToken).ConfigureAwait(false);

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

                    // A failed probe does not throw: Jellyfin logs the provider error and lets the
                    // refresh finish, so the runtime is the only reliable success signal.
                    await ProbeItemAsync(item, CancellationToken.None).ConfigureAwait(false);

                    var probedRuntimeTicks = GetProbedRuntimeTicks(item);
                    if (probedRuntimeTicks is > 0)
                    {
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

                    await Task.Delay(retryDelay, shutdownToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug(
                    "FastSTRM: Background probe of {Name} was cancelled because the server is shutting down",
                    item.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FastSTRM: Background playback probe failed for {Name}", item.Name);
            }
            finally
            {
                _playbackProbeInFlight.TryRemove(item.Id, out _);
            }
        });
    }

    // ── Internal ──

    private async Task RunProbeAsync(List<BaseItem> items, CancellationToken ct)
    {
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
        finally
        {
            lock (_lock)
            {
                State = "Idle";
                CurrentItemName = string.Empty;
                _cts?.Dispose();
                _cts = null;
            }
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
        _cts?.Dispose();
        _shutdownCts?.Dispose();
    }
}
