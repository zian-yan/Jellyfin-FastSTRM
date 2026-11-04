# 按用户播放策略

全局配置（跳过探测、起播后策略、补探测延迟）是**基线**，按用户策略是**覆盖项**。
一个部署里可以同时存在两类用户：一部分人走「跳过探测 + 不补探测」的极速路径，
另一部分人（例如用 TV 端、需要完整轨道的用户）只关掉「跳过探测」，回退官方 ffprobe。

---

## 一、策略模型

用户级策略不是一个开关集合，而是**一个模式**。模式在解析时展开成完整的生效策略，
因此不会出现「跳过探测取了用户值、补探测延迟取了默认值」这种中间态。

| 模式 | 含义 | 跳过探测 | 后台补探测 |
| :--- | :--- | :---: | :---: |
| `Inherit` | 继承全局（等同未配置） | 取全局 | 取全局 |
| `InstantNoProbe` | 跳过探测 + 不补探测 | 是 | 否 |
| `InstantBackgroundProbe` | 跳过探测 + 后台补探测 | 是 | 是 |
| `OfficialProbe` | 不跳过探测（官方 ffprobe） | 否 | 否 |

补充两点：

- **有流条目不受策略影响**：`RunTimeTicks > 0` 的 `.strm` 依然无条件 `allowMediaProbe = false`，
  这是插件的核心行为，任何用户策略都不会让它变慢。
- **`OfficialProbe` 会连带关闭补探测**：官方探测自己会把流信息写库，再补一次没有意义。

---

## 二、优先级规则

```
用户级策略  >  全局策略  >  内置默认值
```

严格按用户的**生效策略**做播放决策，解析顺序如下（`Source` 是接口返回的来源标记）：

| 情况 | 生效策略 | `Source` |
| :--- | :--- | :--- |
| 请求没有用户上下文（`user == null`） | 全局 | `Global` |
| 用户没有策略条目 | 全局 | `Global` |
| 条目模式为 `Inherit` | 全局 | `Inherit` |
| 条目模式合法且非 `Inherit` | 用户级 | `User` |
| 条目模式非法 / 为空 | 全局 | `Fallback`（`FallbackReason = InvalidMode`） |
| 配置整体读取失败 | 内置默认值 | `Global` |

用户级配置只覆盖它显式声明的部分：`OfficialProbe` / `InstantNoProbe` 不带延迟，
补探测延迟与「播放停止后取消补探测」继续沿用全局值；只有
`InstantBackgroundProbe` 允许用 `BackgroundProbeDelaySeconds` 单独覆盖延迟（留空即继承全局）。

---

## 三、全局策略的默认值来源

全局策略的四个字段只有一个默认值来源：`PluginConfiguration` 里的 `Default*` 常量
（`EffectivePlaybackPolicy.Defaults` 引用同一批常量）。三条路径因此得到完全相同的策略：

| 路径 | 取值方式 | 结果 |
| :--- | :--- | :--- |
| 新安装 | 属性初始值 = 常量 | 跳过探测 **开**、后台补探测 **关**、延迟 **5s**、停止取消 **开** |
| 旧配置文件缺字段 | XmlSerializer 不覆盖属性初始值 | 同上 |
| 配置对象不可用（未初始化 / 读取抛异常） | `EffectivePlaybackPolicy.Defaults` | 同上 |

也正因为如此，升级插件不会改变任何现有部署的行为：旧配置文件里没有 `UserPolicies`，
所有人都继续继承全局策略。

---

## 四、回退行为

原则：**一条坏配置只影响它自己，绝不阻断播放。**

| 场景 | 行为 |
| :--- | :--- |
| 用户级配置缺失 | 静默继承全局，不记日志（这是正常状态） |
| 模式值非法（拼写错误、空值、旧版本残留） | 回退全局；每个用户只记 **一条** Warning，播放不受影响 |
| 同一用户重复配置 | 以**第一条**为准（顺序在 XML 与配置页里都可见），记一条 Warning |
| 条目 `UserId` 为空 | 忽略该条目，记一条 Warning |
| 用户已被删除 | 条目保留但永不命中；`GET /FastStrm/PlaybackPolicy` 在 `Orphans` 中列出，便于清理 |
| 条目数超过 1000 | 超出部分忽略，记一条 Warning |
| 读取配置时抛异常 | 回退内置默认值，记一条 Warning |
| 延迟值越界 | 夹到 0–3600 秒，不报错 |

非法模式**不会**被自动改写：插件在日志和接口里保留原值，方便定位是谁写了什么。
在配置页打开并保存一次会把它归一为「继承全局」。

---

## 五、配置读取与生效时机

策略在**每次播放决策时现取**，不做「进程启动时缓存一份」：

| 时机 | 读取点 | 决定什么 |
| :--- | :--- | :--- |
| 客户端请求 PlaybackInfo（每次起播） | `MediaSourceInterceptor.GetPlaybackMediaSources` | 是否 `allowMediaProbe = false`（秒开还是官方探测）、是否调度补探测、补探测延迟 |
| 客户端上报播放进度 | `PlaybackStateInterceptor.UpdatePlayState` | 是否调度补探测（从正在播放该条目的会话反查用户；查不到则按全局） |
| 播放停止 | `ProbeService.OnPlaybackStopped` | 是否取消补探测（全局项，见「限制」） |
| 全库预探测 / 入库自动探测 | `ProbeService` | **与用户策略无关**，没有用户上下文，始终按全局策略 |

因此：

- **保存即生效**：配置页保存后，下一次播放请求就按新策略执行，不需要重启 Jellyfin。
- **不影响正在播放的会话**：`allowMediaProbe` 已在 PlaybackInfo 响应里确定，中途改策略不会打断当前播放；
  下一次起播才换策略。
- **解析成本**：解析结果按配置内容做指纹缓存，配置未变时热路径上只多一次字典查找；
  只有与播放相关的字段变化才触发重建（改「探测并发数」不会）。

---

## 六、配置方法

**控制台 → 插件 → FastSTRM → 按用户播放策略**：

1. 在底部一行选择用户、选择模式（需要单独延迟时选「跳过探测 · 后台补探测」并填秒数）；
2. 点「添加」，该用户出现在上方列表；
3. 列表内可直接改模式、改延迟或「移除」；
4. 点页面底部「保存」——用户策略与全局配置**一起**写入插件配置。

「继承全局」的行在保存时不会写入配置文件，语义与「没有配置」完全一致。

对应的 XML（`plugins/configurations/FastSTRM.xml`）长这样：

```xml
<UserPolicies>
  <UserPlaybackPolicy>
    <UserId>0f8fad5b-d9cb-469f-a165-70867728950e</UserId>
    <Mode>InstantNoProbe</Mode>
  </UserPlaybackPolicy>
  <UserPlaybackPolicy>
    <UserId>1c2d3e4f-5a6b-7c8d-9e0f-1a2b3c4d5e6f</UserId>
    <Mode>InstantBackgroundProbe</Mode>
    <BackgroundProbeDelaySeconds>12</BackgroundProbeDelaySeconds>
  </UserPlaybackPolicy>
</UserPolicies>
```

> 手工编辑 XML 需要重启 Jellyfin；通过配置页保存则即时生效。

---

## 七、接口

两个只读接口（需管理员权限），用于核对「某个用户到底会走哪条路径」：

| 方法 | 路径 | 说明 |
| :--- | :--- | :--- |
| `GET` | `/FastStrm/PlaybackPolicy` | 全局策略 + 每个用户的生效策略与来源；`?userId=` 只查一个用户 |
| `GET` | `/FastStrm/PlaybackPolicy/Effective` | 只返回生效策略；不带 `userId` 时返回全局策略 |

```json
{
  "Global": {
    "SkipProbe": true, "EnableBackgroundProbe": false,
    "BackgroundProbeDelaySeconds": 5, "CancelBackgroundProbeOnPlaybackStop": true,
    "Mode": "InstantNoProbe", "Source": "Global", "FallbackReason": null
  },
  "Users": [
    {
      "UserId": "0f8fad5b-d9cb-469f-a165-70867728950e",
      "UserName": "alice",
      "HasOverride": true,
      "ConfiguredMode": "InstantNoProbe",
      "ConfiguredModeValid": true,
      "ConfiguredDelaySeconds": null,
      "Effective": {
        "SkipProbe": true, "EnableBackgroundProbe": false,
        "BackgroundProbeDelaySeconds": 5, "CancelBackgroundProbeOnPlaybackStop": true,
        "Mode": "InstantNoProbe", "Source": "User", "FallbackReason": null
      }
    }
  ],
  "Orphans": [],
  "PoliciesTruncated": false
}
```

`Source = User` 表示用户级生效；`Global` 表示该用户继承全局；`Fallback` 表示用户条目无效已回退。

---

## 八、限制

- **有流条目不受策略约束**：`RunTimeTicks > 0` 永远秒开。
- **「播放停止后取消补探测」仍是全局项**：它属于资源治理，不随用户变化。
- **同一条目多用户并发播放**：进度上报链路没有用户参数，按下第一个匹配会话解析；
  该条目本身只会被补探测一次，差异仅在「这次是否为该条目调度补探测」。
- **批量探测与入库自动探测没有用户上下文**：始终按全局策略执行，用户策略只影响播放链路。
