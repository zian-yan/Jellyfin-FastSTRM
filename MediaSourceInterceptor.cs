#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace FastSTRM;

/// <summary>
/// Decorator that wraps the original <see cref="IMediaSourceManager"/>.
/// Only <see cref="GetPlaybackMediaSources"/> contains interception logic;
/// all other members delegate 100% to the inner instance.
/// </summary>
public sealed class MediaSourceInterceptor : IMediaSourceManager, IDisposable
{
    private readonly IMediaSourceManager _inner;
    private readonly ILogger<MediaSourceInterceptor> _logger;

    /// <summary>
    /// Items for which the "no stream info" warning has already been emitted,
    /// so repeated PlaybackInfo requests do not flood the log.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _warnedItems = new();

    public MediaSourceInterceptor(IMediaSourceManager inner, ILogger<MediaSourceInterceptor> logger)
    {
        _inner = inner;
        _logger = logger;
    }

    /// <summary>
    /// Core interception point — routing for .strm playback:
    /// 1. Non-.strm → passthrough (allowMediaProbe unchanged, zero side effects).
    /// 2. .strm with stream info (RunTimeTicks &gt; 0) → allowMediaProbe = false (skip ffprobe).
    /// 3. .strm without stream info → allowMediaProbe = false as well by default
    ///    (instant start, no remote probe); admins can opt back into the official
    ///    probing fallback via <see cref="Configuration.PluginConfiguration.SkipProbeForUnprobedStrm"/>.
    /// </summary>
    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
    {
        if (!IsStrmItem(item))
        {
            return await _inner.GetPlaybackMediaSources(
                item, user, allowMediaProbe, enablePathSubstitution, cancellationToken)
                .ConfigureAwait(false);
        }

        var hasStreamInfo = item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0;
        var skipProbe = hasStreamInfo
                        || (Plugin.Instance?.Configuration.SkipProbeForUnprobedStrm ?? true);

        if (!skipProbe)
        {
            _logger.LogDebug(
                "FastSTRM: Unprobed .strm, falling back to the official media probe for {Name}",
                item.Name);

            return await _inner.GetPlaybackMediaSources(
                item, user, allowMediaProbe, enablePathSubstitution, cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogDebug(
            "FastSTRM: Skipping ffprobe for {Name} (HasStreamInfo={HasStreamInfo})",
            item.Name,
            hasStreamInfo);

        var sources = await _inner.GetPlaybackMediaSources(
            item, user, allowMediaProbe: false, enablePathSubstitution, cancellationToken)
            .ConfigureAwait(false);

        WarnIfStreamInfoMissing(item, hasStreamInfo, sources);
        return sources;
    }

    private static bool IsStrmItem(BaseItem item)
        => item.Path is not null
           && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Emits a single warning per item when an unprobed .strm is played without
    /// probing, so administrators know why duration/track selection is missing.
    /// </summary>
    private void WarnIfStreamInfoMissing(
        BaseItem item,
        bool hasStreamInfo,
        IReadOnlyList<MediaSourceInfo> sources)
    {
        if (hasStreamInfo)
        {
            return;
        }

        var hasVideoStream = sources.Any(source =>
            source.MediaStreams is not null
            && source.MediaStreams.Any(stream => stream.Type == MediaStreamType.Video));

        if (hasVideoStream || !_warnedItems.TryAdd(item.Id, 0))
        {
            return;
        }

        _logger.LogWarning(
            "FastSTRM: {Name} has no stream info and probing was skipped for instant playback. "
            + "Duration and track information stay unavailable until the background probe completes; "
            + "run the Incremental probe to fill them in.",
            item.Name);
    }

    // ── All remaining members: 100% delegation to _inner ──

    public void AddParts(IEnumerable<IMediaSourceProvider> providers)
        => _inner.AddParts(providers);

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
        => _inner.GetMediaStreams(itemId);

    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
        => _inner.GetMediaStreams(query);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId)
        => _inner.GetMediaAttachments(itemId);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query)
        => _inner.GetMediaAttachments(query);

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User user = null)
        => _inner.GetStaticMediaSources(item, enablePathSubstitution, user);

    public Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken)
        => _inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken);

    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken)
        => _inner.OpenLiveStream(request, cancellationToken);

    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken)
        => _inner.OpenLiveStreamInternal(request, cancellationToken);

    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken)
        => _inner.GetLiveStream(id, cancellationToken);

    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken)
        => _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

    public ILiveStream GetLiveStreamInfo(string id)
        => _inner.GetLiveStreamInfo(id);

    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId)
        => _inner.GetLiveStreamInfoByUniqueId(uniqueId);

    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken)
        => _inner.GetRecordingStreamMediaSources(info, cancellationToken);

    public Task CloseLiveStream(string id)
        => _inner.CloseLiveStream(id);

    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken)
        => _inner.GetLiveStreamMediaInfo(id, cancellationToken);

    public bool SupportsDirectStream(string path, MediaProtocol protocol)
        => _inner.SupportsDirectStream(path, protocol);

    public MediaProtocol GetPathProtocol(string path)
        => _inner.GetPathProtocol(path);

    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user)
        => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

    public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken)
        => _inner.AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);

    public void Dispose()
    {
        if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
