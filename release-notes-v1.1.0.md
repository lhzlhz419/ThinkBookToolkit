# ThinkBook Toolkit v1.1.0

> **免责声明：** ThinkBook Toolkit 是独立开发的实验性项目，与联想公司无关，不是联想官方项目，也未获得联想的认可、支持或赞助。本软件会读取并写入硬件和固件设置，使用者须自行承担全部风险和后果。所有功能仅在 **ThinkBook 16p G6 IAX（BIOS R2CN57WW）** 上测试，不保证在其他机型或 BIOS 版本上可用或安全。去售后前，请卸载 ThinkBook Toolkit 或拔掉安装该软件的硬盘，避免不必要的麻烦。

## 主要更新

### 插件系统

- 新增版本化插件 API 与独立工作进程；支持插件设置、传感器、概览读数、页面替换、风扇后端和插件自绘 WPF 页面。
- 可从插件页安全导入 ZIP：校验文件路径、链接、重复项、清单和解压大小；新导入插件默认停用，审核权限后才能启用。
- 支持插件设置分组、分别选择概览/OSD/记录使用的插件传感器、传感器历史曲线范围，以及带来源信息和频率限制的插件提示。
- 新增安全卸载和待卸载清理流程；如果插件 DLL 正在使用，Toolkit 会在安全重启后完成清理。插件示例单独构建，不包含在正式发布包中。

### 界面和硬件行为

- 插件页面和设置对话框跟随主题及中英文设置；传感器和概览布局在插件读数变化时保持稳定。
- 新增开机时恢复上次确认的键盘背光亮度；OSD 可通过显示器 EDID 身份在 GPU 路径变化后恢复到原屏幕。
- 传感器记录改为后台压缩，异常退出后可恢复已关闭的记录文件；每个记录使用唯一文件名，压缩失败时保留源文件。
- 驱动更新安装进入跨页面共享队列；辅助进程增加运行时限。
- 功耗锁定保留用户请求值和硬件确认值；读取验证后的固件钳制结果，避免反复覆盖硬件限制。

### 集成与安全

- 移除内置本机 HTTP 数据/控制接口；Toolkit 不再监听本机端口。需要集成时使用经过审核的插件 API。旧版联动设置会被忽略。
- 插件是可信代码模型，并非恶意代码沙箱。普通插件逻辑运行在独立进程；带 `ui.custom` 权限的 WPF 页面和风扇后端会在 Toolkit 进程中运行。只启用审阅过的插件。

## 下载

- **`ThinkBookToolkit-1.1.0-Setup.exe`（推荐）**：在线安装程序，可选择安装位置；检测不到 x64 .NET 9 Desktop Runtime 时会下载微软官方运行时，并注册 Guardian 服务和 UIAccess 自签名证书。
- **`ThinkBookToolkit-1.1.0-win-x64-framework-dependent.zip`**：便携版，需要系统已安装 x64 .NET 9 Desktop Runtime；不会自动注册服务或信任证书。
- **`SHA256SUMS-v1.1.0.txt`**：发布文件的 SHA-256 校验值。

安装和退出前请正常退出 Toolkit，让风扇恢复自动控制。Toolkit 使用项目自签名证书，不代表商业 CA 签名或 SmartScreen 信誉。公开发布包不包含 Lenovo Vantage/电脑管家的专有 DLL；NVAPI、Intel MMIO 和 AMD ZenStates 功耗功能仍为 Beta。

软件版本为 `1.1.0`，插件 API 版本为 `1`，可替换风扇后端 API 版本为 `1.1`，配置文件格式版本为 `1.0`。

完整变更：[v1.0.4...v1.1.0](https://github.com/lhzlhz419/ThinkBookToolkit/compare/v1.0.4...v1.1.0)

---

> **Disclaimer:** ThinkBook Toolkit is an independently developed experimental project. It is not affiliated with, endorsed, supported, or sponsored by Lenovo. The application reads and writes hardware and firmware settings; you accept all risks and consequences. Every feature was tested only on **ThinkBook 16p G6 IAX with BIOS R2CN57WW** and is not guaranteed to work or be safe elsewhere. Before requesting after-sales service, uninstall ThinkBook Toolkit or remove the drive containing it to avoid unnecessary complications.

## Highlights

### Plugin system

- Added a versioned plugin API and isolated worker processes. Plugins can provide settings, sensors, Overview rows, page replacements, fan backends, custom WPF pages and attributed toasts.
- Added ZIP import validation for paths, links, duplicates, manifests and expanded size. Imported plugins are disabled until reviewed and enabled.
- Added setting groups, independent plugin-sensor selection for Overview/OSD/recording, plugin chart bounds and safe plugin removal with deferred cleanup for loaded assemblies.
- The sample plugin is built separately and is not included in public release packages.

### Interface and hardware behavior

- Plugin pages and setting dialogs follow the selected theme and language. Sensor and Overview layouts remain stable as plugin values change.
- Added startup restoration of the last confirmed keyboard-backlight level. OSD monitor placement can follow validated EDID identity across GPU-path changes.
- Sensor recordings are compressed in the background and closed files can be recovered after an interrupted session. Unique file names prevent collisions; failed compression retains its source.
- Driver installations continue in a shared queue when navigating away. Helper processes now have execution time limits.
- Power locks preserve the requested target separately from confirmed hardware values and accept verified firmware clamps without repeatedly fighting the hardware.

### Integration and security

- Removed the built-in localhost HTTP data/control listener. Toolkit no longer opens a local port; use reviewed plugins for integration. Existing HTTP integration settings are ignored.
- Plugins are trusted code, not a malicious-code sandbox. Ordinary plugin logic runs in a separate process; WPF pages granted `ui.custom` and fan backends run inside Toolkit. Enable only code you have reviewed.

## Downloads

- **`ThinkBookToolkit-1.1.0-Setup.exe` (recommended):** online installer with a selectable destination. Downloads the official Microsoft x64 .NET 9 Desktop Runtime when required and registers Guardian and the project's self-signed UIAccess certificate.
- **`ThinkBookToolkit-1.1.0-win-x64-framework-dependent.zip`:** portable package; requires the x64 .NET 9 Desktop Runtime. It does not automatically register services or trust certificates.
- **`SHA256SUMS-v1.1.0.txt`:** SHA-256 checksums for the release assets.

Exit Toolkit normally before installation so it can restore firmware automatic fan control. This version uses a project self-signed certificate, which does not imply commercial CA trust or SmartScreen reputation. Public packages do not include proprietary DLLs from Lenovo Vantage or Lenovo PC Manager. NVAPI, Intel MMIO and AMD ZenStates power controls remain Beta features.

The application version is `1.1.0`, the plugin API version is `1`, the replaceable fan-backend API is `1.1`, and the configuration-file format is `1.0`.

Full diff: [v1.0.4...v1.1.0](https://github.com/lhzlhz419/ThinkBookToolkit/compare/v1.0.4...v1.1.0)
