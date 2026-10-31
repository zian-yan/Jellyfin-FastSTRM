using MediaBrowser.Model.Plugins;

namespace FastSTRM.Configuration;

/// <summary>
/// Plugin configuration model for FastSTRM.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
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
}
