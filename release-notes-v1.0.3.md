# ThinkBook Toolkit v1.0.3

以下变更以 **v1.0.2** 为基准。

## 新增功能

- **背景媒体**：支持图片、GIF、视频；可调整大小模式、缩放、透明度、模糊、基础颜色及播放速度。GIF 使用文件内延迟，预览显示首帧。
- **硬件加速设置**：关闭、自动、省电（核显）、高性能（独显），根据实际显卡显示选项，更改后需重新启动。混合模式下使用独显加速可能阻止独显卸载。
- **传感器 OSD**：横向/纵向布局、独立刷新、文字大小、不透明度、固定位置、贴边吸附和多屏拖动；常规、颜色、临界值、传感器设置。
- **FPS 数据**：FPS、1% Low、帧间隔；支持在前台读数不可用时使用配置的游戏进程。GPU 与显存分组显示，并提供内存、已提交、设备温度、电池容量/功率和风扇读数。
- **传感器记录**：可配置采样间隔和传感器；按分钟批量落盘，停止后压缩为 `.jsonl.gz`。展示支持时间范围、平均采样、曲线显隐、悬停数值。关机时收尾压缩，开关保持开启时下次启动新建记录。
- **传感器与联动**独立导航页；OSD、位置锁定、传感器记录可用于自动化。本机 HTTP 接口新增可选性能模式、风扇策略和风扇满转控制，默认不允许控制。
- **超频启动选项**：新增“再次打开软件时自动打开超频”，默认关闭；不会删除已保存的参数。
- 概览、OSD 和记录增加性能核/能效核平均频率及电池容量（Wh）；记录默认关闭电池功率。

## 修复与优化

- 保修摘要仅使用 `baseinfo`，详情仅显示有效 `detailinfo`。
- 自动化游戏检测与固定转速的“自动检测游戏”开关解耦。
- 改进旧机型 LITSSVC 性能模式切换及混合核显切换的延迟确认、重试行为。
- 修复可读功耗多于可写功耗时的设置、锁定和 Beta 转换。
- 修复背景遮挡、主题配色、GIF 合成、视频白屏、模糊及预览问题。
- 修复 OSD 锁屏/解锁、多屏位置、选项裁切、显存利用率及大小核分类问题。
- 减少概览重复配置复制和无变化通知，缓存核心分类，共用近期采样结果；电池动态值快速读取，静态信息低频缓存。
- 移除风扇后台旧版布局及曲线编辑器，释放离开的页面、关闭的图表和不再使用的媒体资源。具体内存占用取决于硬件、媒体分辨率及启用功能。
- 修复设备信息页切走后访问已释放取消源导致的异常。
- 修复开机自启及安装完成时的启动提权问题；Guardian 改为独立的非 UIAccess 服务入口，解决服务启动时报“请求的操作需要提升”。

## 下载与升级

- `ThinkBookToolkit-1.0.3-Setup.exe`：推荐，在线安装包；按需下载 x64 .NET 9 Desktop Runtime，注册 Guardian 服务和 UIAccess 自签名证书。
- `ThinkBookToolkit-1.0.3-win-x64-framework-dependent.zip`：便携包，需要 x64 .NET 9 Desktop Runtime；不自动注册服务或信任证书。
- `SHA256SUMS-v1.0.3.txt`：发布文件校验值。

**修复 Guardian 服务需要使用新安装包覆盖安装，更新服务路径。** 安装和退出前请正常退出 Toolkit，让风扇恢复自动控制。

本版本使用项目自签名证书，不代表商业 CA 签名或 SmartScreen 信誉；OSD 的全屏显示取决于安装位置、Windows、游戏及显示模式。视频输出最长边为 1280；普通视频显示最高 30 FPS，模糊视频最高 15 FPS。

公开发布包不包含 Lenovo Vantage/电脑管家的专有 DLL。现有 NVAPI、Intel MMIO、AMD ZenStates 功耗功能仍为 Beta。项目为独立开发，非联想官方软件，硬件控制存在风险。

应用版本 `1.0.3`，风扇后端 API `1.1`，配置格式 `1.0`。

[完整变更 v1.0.2 → v1.0.3](https://github.com/lhzlhz419/ThinkBookToolkit/compare/v1.0.2...v1.0.3)

## English

Compared with v1.0.2, this release adds configurable image/GIF/video backgrounds,
hardware-acceleration preferences, a sensor/FPS OSD, compressed sensor recordings
with interactive history charts, a Sensors and integration page, optional local
HTTP controls, sensor automation actions and opt-in overclock activation at startup.

It also improves legacy ITS/GPU switching, writable power-setting handling,
warranty source selection, media rendering and resource lifetime, sensor refresh
allocations, battery polling, OSD session/multi-monitor behavior and startup.
Device-page cancellation no longer accesses a disposed source. A dedicated
non-UIAccess Guardian executable fixes the service elevation error; install the
new setup package to update its registered executable path.

Use the Setup executable for runtime installation, service registration and local
trust of the project's self-signed UIAccess certificate. The portable ZIP does not
perform those steps. Self-signing does not imply commercial certificate trust or
SmartScreen reputation. Full-screen overlay support depends on Windows, installation
location and the game. See CHANGELOG.md for the detailed English changelog.
