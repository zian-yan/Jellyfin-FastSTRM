using System;
using MediaBrowser.Model.Plugins;

namespace FastSTRM.Configuration;

/// <summary>
/// Plugin configuration model for FastSTRM.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Upper bound for the background probe delay, so a typo cannot postpone the probe for days.
    /// </summary>
    public const int MaxBackgroundProbeDelaySeconds = 3600;

    /// <summary>
    /// Lower bound for the configurable probe concurrency.
    /// </summary>
    public const int MinConcurrentProbes = 1;

    /// <summary>
    /// Upper bound for the configurable probe concurrency, so a typo cannot flood the
    /// stream endpoints with parallel probes.
    /// </summary>
    public const int MaxConcurrentProbesLimit = 16;

    /// <summary>
    /// Gets or sets a value indicating whether to automatically probe
    /// newly added .strm items that lack stream info.
    /// </summary>
    public bool EnableAutoProbe { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether .strm items without stream info
    /// should also skip the official media probe on playback
    /// (allowMediaProbe = false) for instant start.
    /// When disabled, such items fall back to the official ffprobe
    /// and the probe result is written to the database.
    /// </summary>
    public bool SkipProbeForUnprobedStrm { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether an unprobed .strm item that already started
    /// playing should additionally be probed in the background.
    /// Default <c>false</c>: playback starts and no probe is scheduled, so the stream endpoint
    /// is not requested a second time.
    /// Only used when <see cref="SkipProbeForUnprobedStrm"/> is enabled.
    /// </summary>
    public bool EnableBackgroundProbeForUnprobedStrm { get; set; }

    /// <summary>
    /// Gets or sets the delay, in seconds, between playback start and the background probe.
    /// Default 5 seconds. Only used when <see cref="EnableBackgroundProbeForUnprobedStrm"/> is enabled.
    /// </summary>
    public int BackgroundProbeDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets a value indicating whether stopping playback should cancel a pending or
    /// running background probe. Default <c>true</c>, so a probe does not keep running after
    /// the client has stopped playback. Only used when background probing is enabled.
    /// </summary>
    public bool CancelBackgroundProbeOnPlaybackStop { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of concurrent probes. Default <c>1</c>
    /// keeps the original one-at-a-time behaviour.
    /// </summary>
    public int MaxConcurrentProbes { get; set; } = 1;

    /// <summary>
    /// Gets the configured background probe delay clamped to a sane range (0–3600 seconds).
    /// </summary>
    public int GetBackgroundProbeDelaySeconds()
        => Math.Clamp(BackgroundProbeDelaySeconds, 0, MaxBackgroundProbeDelaySeconds);

    /// <summary>
    /// Gets the configured probe concurrency clamped to
    /// [<see cref="MinConcurrentProbes"/>, <see cref="MaxConcurrentProbesLimit"/>].
    /// </summary>
    public int GetMaxConcurrentProbes()
        => Math.Clamp(MaxConcurrentProbes, MinConcurrentProbes, MaxConcurrentProbesLimit);
}
