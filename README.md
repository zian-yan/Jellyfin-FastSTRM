<p align="center">
  <img src="images/faststrm-banner.png" alt="FastSTRM" width="520">
</p>

<h1 align="center">Jellyfin-FastSTRM</h1>

<p align="center">为 Jellyfin 打造的 <code>.strm</code> 媒体秒开插件：跳过远程 <code>ffprobe</code> 探测，点击即播。</p>

<p align="center">
  <a href="https://github.com/zian-yan/Jellyfin-FastSTRM/releases"><img src="https://img.shields.io/badge/Release-v3.3.0-blue" alt="Release"></a>
  <img src="https://img.shields.io/badge/Jellyfin-12.0%2B-purple" alt="Jellyfin 12.0+">
  <img src="https://img.shields.io/badge/.NET-10.0-blueviolet" alt=".NET 10.0">
</p>

---

## 简介

FastSTRM 注入 `IMediaSourceManager` 装饰器，让 `.strm` 媒体跳过远程网络探测，消除起播时 3~15 秒的等待与探测超时，实现毫秒级秒开，Web、TV、手机及第三方客户端均生效。

## 特性

- **毫秒级起播**：`.strm` 跳过网络 probe，直接起播。
- **安全分流**：非 `.strm` 本地文件 100% 透传，零副作用。
- **全库预探测**：增量探测（仅补缺少流信息的条目）与全量探测。
- **入库自动探测**：监听媒体库新增事件，新 STRM 后台自动补探测。
- **起播后策略可选**：默认不补探测；可开启延迟后台补探测（默认 5 秒，可配置）。
- **停止即取消**：播放停止后默认取消后台补探测，可关闭以继续补全。
- **进度不丢失**：跳过探测时保留播放位置，开启补探测后恢复「已观看」判定。
- **控制台面板**：媒体统计、实时进度、一键启停。
- **REST API**：支持脚本查询状态、启停任务。

## 工作原理

```text
[ 播放请求 ] ──▶ [ MediaSourceInterceptor ]
                      │
                      ├── 非 .strm ──────────▶ 100% 透传原生管线
                      │
                      └── .strm
                          ├── 已有流信息 ──────▶ allowMediaProbe = false ──▶ 🚀 秒开播放
                          ├── 未探测 + 开关开启 ▶ allowMediaProbe = false ──▶ ⚡ 直接起播（默认不补探测，可选 N 秒补探测）
                          └── 未探测 + 开关关闭 ▶ 沿用调用方参数 ─────────▶ 📡 原生探测
```

## 安装

**方式一：下载 Release**

1. 下载 [最新版本](https://github.com/zian-yan/Jellyfin-FastSTRM/releases) 的 `FastSTRM.dll`。
2. 放入 Jellyfin 插件目录下的 `FastSTRM` 文件夹：

| 运行环境 | 插件路径 |
| :--- | :--- |
| Docker / NAS | `/config/plugins/FastSTRM/FastSTRM.dll` |
| Linux | `/var/lib/jellyfin/plugins/FastSTRM/FastSTRM.dll` |
| Windows 服务版 | `C:\ProgramData\Jellyfin\Server\plugins\FastSTRM\FastSTRM.dll` |
| Windows 便携版 | `%LOCALAPPDATA%\jellyfin\plugins\FastSTRM\FastSTRM.dll` |

3. 重启 Jellyfin，在 **控制台 → 插件** 中确认 FastSTRM 已启用；随后可在控制台菜单点击 **FastSTRM 设置** 直达配置页。

**方式二：源码编译**（需 [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)）

```bash
git clone https://github.com/zian-yan/Jellyfin-FastSTRM.git
cd Jellyfin-FastSTRM
dotnet build -c Release
```

将 `bin/Release/net10.0/FastSTRM.dll` 复制到上述插件目录。

## 使用

在 **控制台** 菜单点击 **FastSTRM 设置** 即可进入（等价于 **控制台 → 插件 → FastSTRM → 设置**；若菜单未显示，可访问 `…/web/index.html#!/configurationpage?name=FastStrmConfigurationPage`）：

- **统计仪表盘**：查看 STRM 总数、已有流信息与缺少流信息的数量。
- **执行预探测**：选择增量（推荐）或全量，点击 **启动预探测**，可查看进度并随时停止。
- **入库自动探测**：勾选 **Strm入库自动探测** 并保存。
- **未探测 STRM 秒开策略**：默认勾选 **未探测的 STRM 文件也跳过探测**；关闭则回退官方探测。
- **起播后补探测**：可选 **不补探测**（默认）或 **后台补探测**；延迟默认 **5 秒**（0–3600）；播放停止后默认取消补探测，可关闭。

## API

需管理员权限 Token 或已登录会话。

| 方法     | 路径                                       | 说明                           |
| :----- | :--------------------------------------- | :--------------------------- |
| `GET`  | `/FastStrm/Summary`                      | 统计概览与秒开策略                    |
| `POST` | `/FastStrm/Probe/Start?mode=Incremental` | 启动探测（`Incremental` / `Full`） |
| `POST` | `/FastStrm/Probe/Stop`                   | 停止探测                         |
| `GET`  | `/FastStrm/Probe/Status`                 | 任务状态、进度与当前文件                 |

## 插件仓库（自动更新）

1. 打开 **控制台 → 插件 → 目录**，点击 **添加**；
2. 名称填 `FastSTRM`，地址填：

   ```text
   https://raw.githubusercontent.com/zian-yan/Jellyfin-FastSTRM/main/manifest.json
   ```

3. 保存后在「插件 → 目录」搜索 `FastSTRM` 安装。

若 `raw.githubusercontent.com` 访问不稳定，可改用 jsDelivr 镜像（缓存约 12 小时）：

```text
https://cdn.jsdelivr.net/gh/zian-yan/Jellyfin-FastSTRM@main/manifest.json
```

## English

**FastSTRM** eliminates playback start delay for `.strm` files (AList, WebDAV, CloudDrive, remote cloud storage) by intercepting `IMediaSourceManager` and skipping remote `ffprobe` calls. Includes full/incremental batch probing, automatic background probing for newly added media, and a dashboard UI. Requires Jellyfin 12.0+ and .NET 10.0.
