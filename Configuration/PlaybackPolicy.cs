using System;

namespace FastSTRM.Configuration;

/// <summary>
/// 播放策略模式标识。
/// </summary>
/// <remarks>
/// 策略模式以**字符串**持久化（插件 XML 配置与配置页 JSON 都保持可读），而不是枚举：
/// XML 反序列化会把无法识别的枚举值静默变成 0（即「继承」），管理员写错一个字母也无从发现。
/// 字符串原样保留非法值，交给 <see cref="PlaybackPolicyResolver"/> 判定为无效并回退，
/// 同时在日志与 API 中给出明确原因。
/// </remarks>
public static class PlaybackPolicyMode
{
    /// <summary>继承全局策略（等于未单独配置）。</summary>
    public const string Inherit = "Inherit";

    /// <summary>跳过探测 + 起播后不补探测。</summary>
    public const string InstantNoProbe = "InstantNoProbe";

    /// <summary>跳过探测 + 起播后后台补探测。</summary>
    public const string InstantBackgroundProbe = "InstantBackgroundProbe";

    /// <summary>不跳过探测，使用官方 ffprobe。</summary>
    public const string OfficialProbe = "OfficialProbe";

    /// <summary>所有合法模式（顺序即配置页下拉顺序）。</summary>
    public static readonly string[] All =
    {
        Inherit,
        InstantNoProbe,
        InstantBackgroundProbe,
        OfficialProbe
    };

    /// <summary>
    /// 规范化配置值：去空白、大小写不敏感匹配。
    /// 返回 <c>false</c> 表示该值不是合法模式，调用方必须回退而不是猜测意图。
    /// </summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = Inherit;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        foreach (var mode in All)
        {
            if (string.Equals(mode, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                normalized = mode;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 由生效后的两个开关反推模式名，用于 API / 界面展示「最终生效的是什么」。
    /// </summary>
    public static string Describe(bool skipProbe, bool enableBackgroundProbe)
        => !skipProbe
            ? OfficialProbe
            : enableBackgroundProbe
                ? InstantBackgroundProbe
                : InstantNoProbe;
}

/// <summary>
/// 单个用户的播放策略覆盖项，持久化在 <see cref="PluginConfiguration.UserPolicies"/> 中。
/// </summary>
public sealed class UserPlaybackPolicy
{
    /// <summary>Gets or sets 用户 Id（Jellyfin 用户主键）。</summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets 策略模式，取值见 <see cref="PlaybackPolicyMode"/>。
    /// 非法值不会阻断播放：解析时按「无效 → 回退全局」处理。
    /// </summary>
    public string Mode { get; set; } = PlaybackPolicyMode.Inherit;

    /// <summary>
    /// Gets or sets 补探测延迟覆盖值（秒）。仅 <see cref="PlaybackPolicyMode.InstantBackgroundProbe"/> 生效；
    /// <c>null</c> 表示沿用全局延迟。
    /// </summary>
    public int? BackgroundProbeDelaySeconds { get; set; }
}

/// <summary>
/// 生效策略的来源，回答「这条策略是谁决定的」。
/// </summary>
public static class PlaybackPolicySource
{
    /// <summary>使用全局策略：该用户没有任何覆盖项。</summary>
    public const string Global = "Global";

    /// <summary>用户级覆盖项生效。</summary>
    public const string User = "User";

    /// <summary>用户显式选择了「继承全局」。</summary>
    public const string Inherit = "Inherit";

    /// <summary>用户级覆盖项存在但无效，已回退到全局策略。</summary>
    public const string Fallback = "Fallback";
}

/// <summary>
/// 回退原因标识。<see cref="EffectivePlaybackPolicy.Source"/> 为
/// <see cref="PlaybackPolicySource.Fallback"/> 时给出具体原因。
/// </summary>
public static class PlaybackPolicyFallbackReason
{
    /// <summary><see cref="UserPlaybackPolicy.Mode"/> 不是合法模式。</summary>
    public const string InvalidMode = "InvalidMode";
}

/// <summary>
/// 解析完成后真正用于播放决策的不可变策略快照。
/// </summary>
/// <param name="SkipProbe">是否为「无流」条目跳过官方 ffprobe。</param>
/// <param name="EnableBackgroundProbe">起播后是否调度后台补探测（已与 <paramref name="SkipProbe"/> 求过与）。</param>
/// <param name="BackgroundProbeDelaySeconds">后台补探测延迟（已夹到 0–3600）。</param>
/// <param name="CancelBackgroundProbeOnPlaybackStop">播放停止是否取消补探测（当前仅全局可配）。</param>
/// <param name="Source">策略来源，见 <see cref="PlaybackPolicySource"/>。</param>
/// <param name="FallbackReason">回退原因，见 <see cref="PlaybackPolicyFallbackReason"/>；正常解析为 <c>null</c>。</param>
public readonly record struct EffectivePlaybackPolicy(
    bool SkipProbe,
    bool EnableBackgroundProbe,
    int BackgroundProbeDelaySeconds,
    bool CancelBackgroundProbeOnPlaybackStop,
    string Source,
    string? FallbackReason)
{
    /// <summary>
    /// 配置对象完全不可用时的兜底策略（插件尚未初始化 / 配置读取抛异常）。
    /// 取值与 <see cref="PluginConfiguration"/> 的属性初始值同源，避免两处默认值漂移。
    /// </summary>
    public static EffectivePlaybackPolicy Defaults { get; } = new(
        PluginConfiguration.DefaultSkipProbeForUnprobedStrm,
        PluginConfiguration.DefaultSkipProbeForUnprobedStrm
            && PluginConfiguration.DefaultEnableBackgroundProbeForUnprobedStrm,
        PluginConfiguration.DefaultBackgroundProbeDelaySeconds,
        PluginConfiguration.DefaultCancelBackgroundProbeOnPlaybackStop,
        PlaybackPolicySource.Global,
        null);

    /// <summary>Gets a value indicating whether 起播后需要调度后台补探测。</summary>
    public bool ShouldProbeInBackground => SkipProbe && EnableBackgroundProbe;

    /// <summary>Gets 生效策略对应的模式名。</summary>
    public string Mode => PlaybackPolicyMode.Describe(SkipProbe, EnableBackgroundProbe);
}
