# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [3.3.0] - 2026-11-02

### ✨ 新增

- 未探测 `.strm` 起播后可选 **不补探测**（默认）或 **后台补探测**。
- 后台补探测延迟可配置，默认 **5 秒**（0–3600）。
- 新增「播放停止后取消补探测」开关，默认开启。
- `GET /FastStrm/Summary` 新增后台补探测相关字段。

### 💬 优化

- 关闭后台补探测时保留播放位置，不再补全时长。

### ⚠️ 变更

- 未探测 `.strm` 默认不再后台补探测；需要时长/轨道信息时请在配置页开启「后台补探测」。

---

## [3.2.3] - 2026-11-01

### 🐛 修复

- 后台探测以 `RunTimeTicks` 是否写入判定成败，失败不再打印 `finished`。
- 起播 10 秒后再探测，失败按 `10s → 30s` 重试，最多 3 次。
- 批量预探测运行时不再叠加单集重试。

---

## [3.2.2] - 2026-11-01

### 💬 优化

- 精简控制台「未探测的 STRM 文件也跳过探测」的说明文字。

---

## [3.2.1] - 2026-11-01

### 🐛 修复

- 修复未探测 `.strm` 播放进度丢失、退出时被误标为已观看的问题。
- 起播后自动在后台补探测时长与流信息，探测期间进度仍正常保存。

---

## [3.2.0] - 2026-10-31

### ⬆️ 跟随 Jellyfin 12 / .NET 10 升级

- **目标框架升级**：`net9.0` → `net10.0`，插件需使用 .NET 10 SDK 构建。
- **Jellyfin 依赖升级**：`Jellyfin.Controller` / `Jellyfin.Model` 由 `10.11.3` 升级到 `12.0.0`，`targetAbi` 更新为 `12.0.0.0`。
- **兼容性**：本版本起需要 **Jellyfin 12.0+（.NET 10）**；Jellyfin 10.11（.NET 9）请继续使用 3.1.0。
- **无需改动的部分**：Jellyfin 12 的 `IMediaSourceManager` 接口与 10.11 保持一致，播放拦截、预探测引擎、控制台面板与 API 逻辑均未变动。
- **CI**：GitHub Actions 的 .NET SDK 更新为 `10.0.x`，产物路径改为 `bin/Release/net10.0/FastSTRM.dll`。

---

## [3.1.0] - 2026-10-31

### ⚡ 未探测 STRM 直接跳过探测（极速起播）

- **播放拦截管线**：
  - 未探测（缺少 `RunTimeTicks` / 流信息）的 `.strm` 文件在播放时同样以 `allowMediaProbe = false` 调用底层 `IMediaSourceManager`，不再触发远程 `ffprobe`，消除首次播放的探测等待。
  - 非 `.strm` 文件依旧 100% 原始透传；已有流信息的 `.strm` 行为不变。
  - 新增配置项 `SkipProbeForUnprobedStrm`（默认 `true`）。关闭后，未探测的 `.strm` 会安全回退至官方媒体探测并写入数据库，保留兼容性兜底。
- **控制台面板**：
  - 新增「未探测的 STRM 文件也跳过探测」开关，可随时在极速模式与官方探测兜底模式间切换。
- **API**：
  - `GET /FastStrm/Summary` 新增 `SkipProbeForUnprobedStrm` 字段，便于外部脚本确认当前起播策略。
- **诊断日志**：
  - 未探测 `.strm` 跳过探测且无视频流信息时，每个条目仅记录一条 Warning，提示可通过增量探测补全时长与轨道信息。

### 升级说明
- 插件配置文件新增字段，旧配置按「跳过探测」处理，无需手动修改。

---

## [3.0.0] - 2026-08-27

### 🚀 Major Architectural Rewrite & Features (重大重构与全新特性)

- **核心播放拦截管线重构 (IMediaSourceManager Decorator)**:
  - 彻底摒弃旧版侵入式的 HTTP ActionFilter / PlaybackInfoFilter 机制，改为在 Jellyfin 依赖注入容器中以装饰器模式包装 `IMediaSourceManager`。
  - 实现高效智能的三路分流策略：
    1. **非 `.strm` 文件**：100% 原始透传，不产生任何副作用；
    2. **已有流信息的 `.strm` 文件**（`RunTimeTicks > 0`）：直接禁用 `allowMediaProbe`，跳过远程 `ffprobe` 网络探测，真正实现秒开起播；
    3. **缺少流信息的 `.strm` 文件**（首次入库或未探测）：放行官方媒体探测，保证流信息正确写入数据库并维持兼容性。
  - 除核心拦截点 `GetPlaybackMediaSources` 外，其余所有接口方法均 100% 委托底层原生实现，保证稳定性。

- **新增后台预探测服务 (`ProbeService`)**:
  - 新增 `ProbeService` 作为 Hosted Singleton 运行在 Jellyfin 生命周期中。
  - 支持全媒体库 `.strm` 文件的后台异步探测：
    - **增量探测 (Incremental)**：仅扫描并探测数据库中缺失流信息的 `.strm` 媒体；
    - **全量探测 (Full)**：对全库所有 `.strm` 媒体执行元数据与流信息深度刷新。
  - 支持 **入库自动探测 (Auto-Probe on Item Added)**：监听 `ILibraryManager.ItemAdded` 事件，当新 `.strm` 文件入库时自动在后台触发媒体探测，无需用户手动干预。
  - 提供探测任务的取消与中断控制机制（支持优雅停止与异常跳过）。

- **全新控制台 Web 管理面板 (Admin Dashboard)**:
  - 新增专用的 Jellyfin 控制台插件配置页面（`configPage.html` & `configPage.js`）。
  - **媒体统计仪表盘**：实时展示 `.strm 媒体总数`、`已有流信息数` 以及 `缺少流信息数`。
  - **任务控制中心**：支持一键选择“增量探测”或“全量探测”模式并启动/停止任务。
  - **实时进度监控**：可视化进度条与状态显示（空闲、运行中、停止中），精确显示当前正在探测的媒体名称与索引百分比。
  - **自动化选项配置**：支持在界面中一键开启/关闭“Strm 入库自动探测”功能。

- **新增 RESTful 管理 API (`FastStrmController`)**:
  - 提供需管理员权限 (`RequiresElevation`) 的 API 接口：
    - `GET /FastStrm/Summary`: 获取媒体库 `.strm` 统计概览。
    - `POST /FastStrm/Probe/Start?mode={Incremental|Full}`: 启动预探测任务。
    - `POST /FastStrm/Probe/Stop`: 停止当前预探测任务。
    - `GET /FastStrm/Probe/Status`: 查询当前探测进度及状态。

- **规范化打包与清单支持**:
  - 新增 `meta.json` 与 `build.yaml` 清单文件，支持发布至自定义 Jellyfin 插件源仓库。
  - 升级适配 **.NET 9.0** 与 **Jellyfin 10.11+**。

---

## [2.0.0] - 2026-08-20

### Added
- 引入 `FastStrmPlaybackInfoFilter` 尝试在播放请求阶段拦截并修改媒体源信息。
- 完善针对直链流媒体播放探测超时的基础处理。

### Changed
- 重构部分服务注册逻辑。

---

## [1.0.0] - 2026-08-10

### Added
- 初始版本发布。
- 提供基础的 `.strm` 播放探测拦截原型，尝试消除 Web 端起播等待延迟。
