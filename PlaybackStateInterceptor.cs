using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
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
/// the duration, after which Jellyfin's normal completion detection takes over again.
/// </remarks>
public sealed class PlaybackStateInterceptor : IUserDataManager
{
    private readonly IUserDataManager _inner;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PlaybackStateInterceptor> _logger;

    /// <summary>
    /// Items for which the "duration unknown, keeping position" notice was already logged.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _preservedItems = new();

    public PlaybackStateInterceptor(
        IUserDataManager inner,
        IServiceProvider serviceProvider,
        ILogger<PlaybackStateInterceptor> logger)
    {
        _inner = inner;
        _serviceProvider = serviceProvider;
        _logger = logger;
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
        // Only .strm items whose duration is still unknown are affected by Jellyfin's
        // "unknown runtime means fully played" fallback. Once the background probe has
        // stored a runtime, the original implementation must handle completion again.
        if (reportedPositionTicks is >= 0
            && IsStrmItem(item)
            && item.SupportsPositionTicksResume
            && !HasKnownRuntime(item))
        {
            data.PlaybackPositionTicks = reportedPositionTicks.Value;

            if (reportedPositionTicks.Value > 0 && _preservedItems.TryAdd(item.Id, 0))
            {
                _logger.LogInformation(
                    "FastSTRM: Preserving playback position {Position} for {Name} because its duration is not known yet; probing in background so completion can be tracked",
                    reportedPositionTicks.Value,
                    item.Name);
            }

            ScheduleBackgroundProbe(item);
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

    private void ScheduleBackgroundProbe(BaseItem item)
    {
        try
        {
            _serviceProvider.GetService<ProbeService>()?.SchedulePlaybackProbe(item);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Unable to schedule a background probe for {Name}", item.Name);
        }
    }
}
