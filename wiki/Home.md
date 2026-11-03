# FastSTRM Wiki

Jellyfin `.strm` 秒开插件的补充文档。安装、配置项与控制台说明见 [README](https://github.com/zian-yan/Jellyfin-FastSTRM#readme)。

## 文档

| 页面 | 内容 |
| :--- | :--- |
| [播放模式：有流与无流](Playback-Modes) | 两类 `.strm` 的判定依据、播放路径、三条无流子路径、对比矩阵与选择建议 |
| [排障](Troubleshooting) | 起播慢、无时长、进度丢失、补探测失败、任务无法启动等常见现象 |

## 一句话理解

- **有流**（`RunTimeTicks > 0`）：库里有流信息，永远秒开，信息完整。
- **无流**：默认也秒开且不再请求流地址；需要时长/轨道时，开启后台补探测或跑一次增量预探测。

## 相关链接

- [源码](https://github.com/zian-yan/Jellyfin-FastSTRM)
- [Releases](https://github.com/zian-yan/Jellyfin-FastSTRM/releases)
- [CHANGELOG](https://github.com/zian-yan/Jellyfin-FastSTRM/blob/main/CHANGELOG.md)
- [Issues](https://github.com/zian-yan/Jellyfin-FastSTRM/issues)
