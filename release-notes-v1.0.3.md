# ThinkBook Toolkit v1.0.3

> **免责声明：** ThinkBook Toolkit 是独立开发的实验性项目，与联想公司无关，不是联想官方项目，也未获得联想的认可、支持或赞助。本软件会读取并写入硬件和固件设置，使用者须自行承担全部风险和后果。所有功能仅在 **ThinkBook 16p G6 IAX（BIOS R2CN57WW）** 上测试，不保证在其他机型或 BIOS 版本上可用或安全。去售后前，请卸载 ThinkBook Toolkit 或拔掉安装该软件的硬盘，避免不必要的麻烦。

## 主要更新

### 背景媒体与硬件加速

- **背景媒体**：支持图片、GIF、视频；可调整大小模式、缩放、透明度、模糊、基础颜色及播放速度。GIF 使用文件内延迟，预览显示首帧。
- **硬件加速设置**：关闭、自动、省电（核显）、高性能（独显），根据实际显卡显示选项，更改后需重新启动。混合模式下使用独显加速可能阻止独显卸载。
- 修复背景遮挡、主题配色、GIF 合成、视频白屏、模糊及预览问题。

### 传感器 OSD 与读数

- **传感器 OSD**：横向/纵向布局、独立刷新、文字大小、不透明度、固定位置、贴边吸附和多屏拖动；常规、颜色、临界值、传感器设置。
- **FPS 数据**：FPS、1% Low、帧间隔；支持在前台读数不可用时使用配置的游戏进程。GPU 与显存分组显示，并提供内存、已提交、设备温度、电池容量/功率和风扇读数。
- 概览、OSD 和记录增加性能核/能效核平均频率及电池容量（Wh）；记录默认关闭电池功率。
- 修复 OSD 锁屏/解锁、多屏位置、选项裁切、显存利用率及大小核分类问题。

### 传感器记录与展示

- **传感器记录**：可配置采样间隔和传感器；按分钟批量落盘，停止后压缩为 `.jsonl.gz`。展示支持时间范围、平均采样、曲线显隐、悬停数值。关机时收尾压缩，开关保持开启时下次启动新建记录。
- 记录点数小于等于最大绘制点数时保留原始数据；超过上限时取平均，连续有效读数之间的空桶通过插值补齐，实际缺失的读数仍保持断开。
- 查看压缩记录时临时解压一次，切换文件或关闭展示窗口时清理临时文件和图表缓存。

### 软件联动与自动化

- **传感器与联动**独立导航页；OSD、位置锁定、传感器记录可用于自动化。本机 HTTP 接口新增可选性能模式、风扇策略和风扇满转控制，默认不允许控制。
- 自动化游戏检测与固定转速的“自动检测游戏”开关解耦。

### 兼容性与启动

- **超频启动选项**：新增“再次打开软件时自动打开超频”，默认关闭；不会删除已保存的参数。
- 保修摘要仅使用 `baseinfo`，详情仅显示有效 `detailinfo`。
- 改进旧机型 LITSSVC 性能模式切换及混合核显切换的延迟确认、重试行为。
- 修复可读功耗多于可写功耗时的设置、锁定和 Beta 转换。
- 修复设备信息页切走后访问已释放取消源导致的异常。
- 修复开机自启及安装完成时的启动提权问题；Guardian 改为独立的非 UIAccess 服务入口，解决服务启动时报“请求的操作需要提升”。

### 性能与资源优化

- 减少概览重复配置复制和无变化通知，缓存核心分类，共用近期采样结果；电池动态值快速读取，静态信息低频缓存。
- 移除风扇后台旧版布局及曲线编辑器，释放离开的页面、关闭的图表和不再使用的媒体资源。具体内存占用取决于硬件、媒体分辨率及启用功能。

## 下载

- **`ThinkBookToolkit-1.0.3-Setup.exe`（推荐）**：在线安装程序，可选择安装位置；检测不到 x64 .NET 9 Desktop Runtime 时会下载微软官方运行时，并注册 Guardian 服务和 UIAccess 自签名证书。
- **`ThinkBookToolkit-1.0.3-win-x64-framework-dependent.zip`**：便携版，需要系统已安装 x64 .NET 9 Desktop Runtime；不会自动注册服务或信任证书。
- **`SHA256SUMS-v1.0.3.txt`**：发布文件的 SHA-256 校验值。

**修复 Guardian 服务需要使用新安装包覆盖安装，更新服务路径。** 安装和退出前请正常退出 Toolkit，让风扇恢复自动控制。

本版本使用项目自签名证书，不代表商业 CA 签名或 SmartScreen 信誉；OSD 的全屏显示取决于安装位置、Windows、游戏及显示模式。视频输出最长边为 1280；普通视频显示最高 30 FPS，模糊视频最高 15 FPS。

公开发布包不包含 Lenovo Vantage/电脑管家的专有 DLL。现有 NVAPI、Intel MMIO、AMD ZenStates 功耗功能仍为 Beta。项目为独立开发，非联想官方软件，硬件控制存在风险。

软件版本为 `1.0.3`，可替换风扇后端 API 版本为 `1.1`，配置文件格式版本为 `1.0`。

完整变更：[v1.0.2...v1.0.3](https://github.com/lhzlhz419/ThinkBookToolkit/compare/v1.0.2...v1.0.3)

---

> **Disclaimer:** ThinkBook Toolkit is an independently developed experimental project. It is not affiliated with, endorsed, supported, or sponsored by Lenovo. The application reads and writes hardware and firmware settings; you accept all risks and consequences. Every feature was tested only on **ThinkBook 16p G6 IAX with BIOS R2CN57WW** and is not guaranteed to work or be safe elsewhere. Before requesting after-sales service, uninstall ThinkBook Toolkit or remove the drive containing it to avoid unnecessary complications.

## Highlights

### Background media and hardware acceleration

- Added image, GIF and video backgrounds with size modes, scaling, transparency, blur, base color and playback speed. GIF playback follows embedded delays, and animated-media previews show the first frame.
- Added disabled, automatic, integrated-GPU and discrete-GPU acceleration options according to available adapters. Changes require a restart; using the discrete GPU in hybrid mode may prevent it from being disconnected.
- Fixed background occlusion, theme colors, GIF composition, video white frames, blur and previews.

### Sensor OSD and readings

- Added horizontal/vertical layouts, independent refresh, font size, opacity, position locking, edge snapping and multi-monitor dragging, with General, Colors, Thresholds and Sensors settings.
- Added FPS, 1% Low and frame-interval readings, with configured-game fallback when foreground readings are unavailable. GPU and VRAM are separate groups, alongside RAM, committed memory, device temperatures, battery capacity/power and fan readings.
- Added P-core/E-core average frequencies and battery capacity (Wh) to Overview, OSD and recording. Battery power recording is disabled by default.
- Fixed OSD lock/unlock behavior, multi-monitor placement, clipped options, VRAM utilization and hybrid-core classification.

### Sensor recording and history

- Added configurable sampling intervals and sensor selection, minute-batched file writes and automatic `.jsonl.gz` compression when recording ends.
- History supports time ranges, averaged sampling, clickable legends and hover values. Shutdown finalizes and compresses the current file; when recording remains enabled, the next launch starts a new file.
- Records within the plot-point limit retain their original samples. Larger records are averaged; empty buckets between valid adjacent readings are interpolated without bridging genuinely missing readings.
- Compressed recordings are extracted once for viewing. Temporary files and chart caches are released when switching files or closing the viewer.

### Software integration and automation

- Added a dedicated Sensors and integration page. OSD, position locking and sensor recording can be controlled by automation.
- Expanded the loopback HTTP API with optional performance-mode, fan-strategy and full-fan-speed controls. Control access is disabled by default.
- Game start/stop automation detection is independent of the fixed-RPM automatic-game-detection option.

### Compatibility and startup

- Added “Automatically enable overclocking when Toolkit starts again,” disabled by default, without deleting saved overclock values.
- Warranty summaries use `baseinfo`; details show valid `detailinfo` entries only.
- Improved legacy LITSSVC performance-mode switching and deferred confirmation/retry behavior for Hybrid iGPU switching.
- Fixed settings, locks and Beta conversions when more power values are readable than writable.
- Fixed device-page asynchronous work accessing a disposed cancellation source after navigation.
- Fixed startup and installer launch elevation handling. Guardian now uses a dedicated non-UIAccess service executable to resolve the service elevation error.

### Performance and resource usage

- Reduced repeated Overview configuration copying and unchanged property notifications, cached core classification and shared recent sensor samples. Dynamic battery readings refresh quickly while static information is cached.
- Removed the legacy layout and curve editors from the background fan runtime, and release departed pages, closed charts and unused media resources. Actual memory use depends on hardware, media resolution and enabled features.

## Downloads

- **`ThinkBookToolkit-1.0.3-Setup.exe` (recommended):** online installer with a selectable destination. Downloads the official Microsoft x64 .NET 9 Desktop Runtime when required and registers Guardian and the project's self-signed UIAccess certificate.
- **`ThinkBookToolkit-1.0.3-win-x64-framework-dependent.zip`:** portable package; requires the x64 .NET 9 Desktop Runtime. It does not automatically register services or trust certificates.
- **`SHA256SUMS-v1.0.3.txt`:** SHA-256 checksums for the release assets.

**Install the new Setup package over the existing installation to update the Guardian service executable path.** Exit Toolkit normally before installation so it can restore firmware automatic fan control.

This version uses a project self-signed certificate, which does not imply commercial CA trust or SmartScreen reputation. Full-screen OSD support depends on the installation location, Windows, the game and its display mode. Video output is capped at a 1280-pixel long edge, with up to 30 FPS normally or 15 FPS when blurred.

Public packages do not include proprietary DLLs from Lenovo Vantage or Lenovo PC Manager. NVAPI, Intel MMIO and AMD ZenStates power controls remain Beta features. ThinkBook Toolkit is independently developed, is not an official Lenovo application, and hardware control carries risks.

The application version is `1.0.3`, the replaceable fan-backend API is `1.1`, and the configuration-file format is `1.0`.

Full diff: [v1.0.2...v1.0.3](https://github.com/lhzlhz419/ThinkBookToolkit/compare/v1.0.2...v1.0.3)
