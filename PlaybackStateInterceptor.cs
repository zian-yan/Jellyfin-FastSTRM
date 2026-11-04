using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FastSTRM;

/// <summary>
/// Decorator around <see cref="IUserDataManager"/> that keeps playback progress for .strm
/// items whose duration is still unknown (the probe was skipped for instant playback).
/// </summary>
/// <remarks>
/// Jellyfin's <c>UserDataManager.UpdatePlayState</c> treats an item without a runtime as
/// "watched to completion": it sets <c>Played = true</c> and resets the position to 0.
/// That is why, with probing skipped, third-party clients lose their resume point on every
/// progress report and the item is marked watched as soon as playback starts/stops.
/// This decorator stores the reported position instead and lets a background probe fill in
/// the duration (when background probing is enabled), after which Jellyfin's normal completion
/// detection takes over again. When background probing is disabled, the position is preserved
/// but completion stays undetectable until a probe runs.
/// </remarks>
public sealed class PlaybackStateInterceptor : IUserDataManager, IDisposable
{
    /// <summary>
    /// Upper bound for the "position preserved" notice map. The map is only used to avoid
    /// repeating the same log line, so clearing it when full merely allows a duplicate notice
    /// later; it must never grow with the library.
    /// </summary>
    private const int MaxPreservedItems = 8192;

    private readonly IUserDataManager _inner;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PlaybackStateInterceptor> _logger;
    private readonly PlaybackPolicyResolver _policyResolver;

    /// <summary>
    /// Resolved on first use instead of being injected: Jellyfin's session manager itself depends
    /// on <see cref="IUserDataManager"/>, so injecting it here would make the container resolve the
    /// decorator while constructing its own constructor argument (a circular dependency).
    /// By the time playback progress is reported the container is fully built, so the lookup
    /// returns the already-constructed singleton.
    /// </summary>
    private ISessionManager? _sessionManager;

    /// <summary>
    /// Whether this decorator is the only owner of <see cref="_inner"/>. An instance that came
    /// from <c>ImplementationInstance</c> or an arbitrary factory may be shared, so disposing it
    /// from here could break the rest of the server.
    /// </summary>
    private readonly bool _disposeInner;

    /// <summary>
    /// Items for which the "duration unknown, keeping position" notice was already logged.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _preservedItems = new();

    public PlaybackStateInterceptor(
        IUserDataManager inner,
        IServiceProvider serviceProvider,
        ILogger<PlaybackStateInterceptor> logger,
        PlaybackPolicyResolver policyResolver,
        bool disposeInner)
    {
        _inner = inner;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _policyResolver = policyResolver;
        _disposeInner = disposeInner;
    }

    /// <inheritdoc />
    public event EventHandler<UserDataSaveEventArgs>? UserDataSaved
    {
        add => _inner.UserDataSaved += value;
        remove => _inner.UserDataSaved -= value;
    }

    /// <inheritdoc />
    public void SaveUserData(User user, BaseItem item, UserItemData userData, UserDataSaveReason reason, CancellationToken cancellationToken)
        => _inner.SaveUserData(user, item, userData, reason, cancellationToken);

    /// <inheritdoc />
    public void SaveUserData(User user, BaseItem item, UpdateUserItemDataDto userDataDto, UserDataSaveReason reason)
        => _inner.SaveUserData(user, item, userDataDto, reason);

    /// <inheritdoc />
    public UserItemData? GetUserData(User user, BaseItem item)
        => _inner.GetUserData(user, item);

    /// <inheritdoc />
    public UserItemDataDto? GetUserDataDto(BaseItem item, User user)
        => _inner.GetUserDataDto(item, user);

    /// <inheritdoc />
    public Dictionary<Guid, UserItemData> GetUserDataBatch(IReadOnlyList<BaseItem> items, User user)
        => _inner.GetUserDataBatch(items, user);

    /// <inheritdoc />
    public VersionResumeData? GetResumeUserData(User user, BaseItem item)
        => _inner.GetResumeUserData(user, item);

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, VersionResumeData> GetResumeUserDataBatch(IReadOnlyList<BaseItem> items, User user)
        => _inner.GetResumeUserDataBatch(items, user);

    /// <inheritdoc />
    public UserItemDataDto? GetUserDataDto(BaseItem item, BaseItemDto? itemDto, User user, DtoOptions options)
        => _inner.GetUserDataDto(item, itemDto, user, options);

    /// <inheritdoc />
    public bool UpdatePlayState(BaseItem item, UserItemData data, long? reportedPositionTicks)
    {
        if (IsStrmItem(item) && HasKnownRuntime(item))
        {
            // The background probe has stored a duration, so the "position preserved" marker for
            // this item is obsolete. Dropping it keeps the map bounded by the unprobed items.
            _preservedItems.TryRemove(item.Id, out _);
        }

        // Only .strm items whose duration is still unknown are affected by Jellyfin's
        // "unknown runtime means fully played" fallback. Once the background probe has
        // stored a runtime, the original implementation must handle completion again.
        if (reportedPositionTicks is >= 0
            && IsStrmItem(item)
            && item.SupportsPositionTicksResume
            && !HasKnownRuntime(item))
        {
            data.PlaybackPositionTicks = reportedPositionTicks.Value;

            // 补探测是否调度沿用播放者自己的生效策略（用户级优先，未配置继承全局）。
            // UpdatePlayState 不带用户参数，因此从正在播放该条目的会话反查用户；
            // 查不到会话（例如手动标记已看）时按全局策略处理。
            // The official-probe fallback (skip disabled) handles probing itself, so no background
            // probe is scheduled in that mode either.
            var policy = _policyResolver.Resolve(ResolvePlayingUserId(item));
            var backgroundProbeDelaySeconds = policy.ShouldProbeInBackground
                ? policy.BackgroundProbeDelaySeconds
                : (int?)null;

            if (reportedPositionTicks.Value > 0)
            {
                if (_preservedItems.Count >= MaxPreservedItems)
                {
                    // Only the log de-duplication marker is dropped; playback is unaffected.
                    _preservedItems.Clear();
                }

                if (_preservedItems.TryAdd(item.Id, 0))
                {
                    if (backgroundProbeDelaySeconds.HasValue)
                    {
                        _logger.LogInformation(
                            "FastSTRM: Preserving playback position {Position} for {Name} because its duration is not known yet; probing in background so completion can be tracked",
                            reportedPositionTicks.Value,
                            LogSafe.Name(item));
                    }
                    else
                    {
                        _logger.LogInformation(
                            "FastSTRM: Preserving playback position {Position} for {Name} because its duration is not known yet; background probing is disabled for this user's playback policy, so completion cannot be tracked until a probe runs",
                            reportedPositionTicks.Value,
                            LogSafe.Name(item));
                    }
                }
            }

            if (backgroundProbeDelaySeconds.HasValue)
            {
                ScheduleBackgroundProbe(item, backgroundProbeDelaySeconds.Value);
            }

            return false;
        }

        return _inner.UpdatePlayState(item, data, reportedPositionTicks);
    }

    /// <inheritdoc />
    public void ResetPlaybackStreamSelections(User user, BaseItem item)
        => _inner.ResetPlaybackStreamSelections(user, item);

    private static bool IsStrmItem(BaseItem item)
        => item.Path is not null
           && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

    private static bool HasKnownRuntime(BaseItem item)
        => item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0;

    /// <summary>
    /// Finds which user is playing <paramref name="item"/>, so the per-user playback policy can be
    /// applied on the progress-report path as well.
    /// </summary>
    /// <returns>
    /// The user id, or <c>null</c> when no session is playing the item or session information is
    /// unavailable. The caller then uses the global policy — a probe decision must never depend on
    /// a guess about which user is playing.
    /// </returns>
    /// <remarks>
    /// <see cref="IUserDataManager.UpdatePlayState"/> has no user parameter and
    /// <c>UserItemData</c> carries no user id, so the session is the only reliable source.
    /// Two users playing the same item at the same time resolve to the first matching session;
    /// the item is probed at most once anyway, so the difference is only whether a background
    /// probe is scheduled for this particular item.
    /// </remarks>
    private Guid? ResolvePlayingUserId(BaseItem item)
    {
        try
        {
            var sessionManager = _sessionManager
                ??= _serviceProvider.GetService<ISessionManager>();

            if (sessionManager is null)
            {
                return null;
            }

            foreach (var session in sessionManager.Sessions)
            {
                if (session.NowPlayingItem is not null
                    && session.NowPlayingItem.Id == item.Id
                    && session.UserId != Guid.Empty)
                {
                    return session.UserId;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Unable to resolve the playing user for {Name}", LogSafe.Name(item));
        }

        return null;
    }

    private void ScheduleBackgroundProbe(BaseItem item, int delaySeconds)
    {
        try
        {
            _serviceProvider.GetService<ProbeService>()?.SchedulePlaybackProbe(item, delaySeconds);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Unable to schedule a background probe for {Name}", LogSafe.Name(item));
        }
    }

    public void Dispose()
    {
        if (_disposeInner && _inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
