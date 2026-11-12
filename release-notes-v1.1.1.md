# ThinkBook Toolkit v1.1.1

> **免责声明：** ThinkBook Toolkit 是独立开发的实验性项目，与联想公司无关，未获得联想的认可、支持或赞助。本软件会读取并写入硬件和固件设置，使用者须自行承担全部风险和后果。测试覆盖有限，不能保证在所有机型或 BIOS 版本上可用或安全。静音性能的切换逻辑已通过模拟测试，仍需在支持机型上验证运行效果。去售后前，请卸载 ThinkBook Toolkit 或拔掉安装该软件的硬盘，避免不必要的麻烦。

## 主要更新

### 插件系统与自动化

- 插件可订阅电源、睡眠唤醒、游戏、模式和设置变化，提供自动化动作、触发器与条件。传感器阈值支持持续时间、回差和冷却时间。
- 宿主命令增加执行回执、失败原因和实际返回数据。插件独立调度，可按需订阅数据并选择有限通信重试。修复界面繁忙时插件通信被误判为超时的问题。
- 新增限定范围的本体及跨插件设置修改权限，启用时展示修改目标和风险。插件设置增加滑块、文件/目录/颜色选择、条件显隐及动态可用选项。
- 插件页新增更新、回退和取消待处理变更。重启后切换插件包，保留旧包和配置备份，更新后重新审核。增加依赖检查和配置迁移，继续兼容旧版 v1 插件。
- 自动化补齐本体控制项目，增加启动、唤醒、锁屏、解锁事件，以及单条启用、排队、取消、进度与运行记录。CPU 功耗 Beta 选项按处理器类型显示。

### 界面与显示

- 设置页新增“主题颜色”，可分别设置浅色和深色模式的强调色、背景主题色及概览顶部渐变，支持预览与恢复默认。卡片、侧栏和文字配色随背景协调变化。
- 刷新率、内屏分辨率和 DPI 缩放集中在同一区域。支持内屏 DPI 预设及 100–500% 自定义缩放，后者作用于当前账户的全部屏幕，需要注销后生效。
- 开机启动到托盘时直接后台初始化。初始化期间主动打开窗口后，窗口会保持打开。
- 取消收起状态下通过滚轮改变下拉框选项的行为，保留页面及展开列表的滚动。
- 免责声明首次确认要求完整手动输入。已经确认过旧版的用户，在确认其他版本时可复制粘贴完整确认文字。

### 性能与稳定性

- 支持的机型新增“静音性能模式”，可在概览、性能页、自动化和 Fn+Q 配置中选择。通过原厂 Dispatcher 切到性能档、设置 BIOS 开关，并确认服务缓存一致。
- 睡眠或休眠唤醒后恢复上次确认的性能模式。
- 核心与显存超频偏移量的上下限改为从驱动读取，增加范围刷新，并在写入前重新校验。超出范围的旧值保留并提示调整。
- NVPCF 缺失、禁用或读取异常时暂停 NVAPI Beta。异常前开启的状态会在驱动恢复且检测成功后自动恢复，用户主动关闭则取消恢复。
- 优化功耗锁定的部分读回、固件钳制和重试行为，报错注明后端、阶段及受影响的锁定项，减少重复提示。
- 修复 GPU 工作进程断管后清理失败、无法重新连接的问题。
- 针对运行期间 .NET 更新后发生的 PenIMC 输入线程闪退，启动时采用 WPF Pointer 输入路径。Guardian 的异常风扇恢复改由新进程执行，并检查结果和超时。
- 插件传感器记录保存当时的名称、单位、类别、提供者版本及绘图范围，便于更新或卸载插件后查看历史数据。

## 下载

- **`ThinkBookToolkit-1.1.1-Setup.exe`（推荐）**：在线安装程序，可选择安装位置。检测不到 x64 .NET 9 Desktop Runtime 时下载微软官方运行时，并注册 Guardian 服务和 UIAccess 自签名证书。
- **`ThinkBookToolkit-1.1.1-win-x64-framework-dependent.zip`**：便携版，需要系统已安装 x64 .NET 9 Desktop Runtime。服务注册及证书信任需要单独配置。
- **`SHA256SUMS-v1.1.1.txt`**：发布文件的 SHA-256 校验值。

安装前请正常退出 Toolkit，让风扇恢复自动控制。本次更新包含 Guardian 与 GPU 工作进程，请完整更新发布包。Toolkit 使用项目自签名证书，SmartScreen 可能提示未知发布者。公开发布包排除 Lenovo Vantage/电脑管家的专有 DLL 和测试插件。NVAPI、Intel MMIO 和 AMD ZenStates 功耗功能仍为 Beta。

软件版本为 `1.1.1`，插件清单支持版本 `1 / 2`，插件程序集和 IPC 协议保持 `1`，可替换风扇后端 API 为 `1.1`，配置文件格式为 `1.0`。

完整变更：[v1.1.0...v1.1.1](https://github.com/lhzlhz419/ThinkBookToolkit/compare/v1.1.0...v1.1.1)

---

> **Disclaimer:** ThinkBook Toolkit is an independently developed experimental project. It is not affiliated with, endorsed, supported, or sponsored by Lenovo. The application reads and writes hardware and firmware settings. You accept all risks and consequences. Testing coverage is limited, and compatibility or safety across all models and BIOS versions cannot be guaranteed. Quiet Performance switching has passed simulated tests and still requires validation on supported hardware. Before requesting after-sales service, uninstall ThinkBook Toolkit or remove the drive containing it to avoid unnecessary complications.

## Highlights

### Plugins and automation

- Plugins can subscribe to power, sleep/resume, game, mode and setting events and provide automation actions, triggers and conditions. Sensor thresholds support duration, hysteresis and cooldown.
- Host commands return execution receipts, errors and available result data. Plugins have independent scheduling, data subscriptions and optional bounded communication retries. Fixed false IPC timeouts when the WPF UI was busy.
- Added scoped host/other-plugin setting writes with explicit permission review. New setting editors support sliders, files, folders, colors, conditional visibility and dynamic available choices.
- Added plugin updates, rollback and cancellation of pending changes. Packages switch after restart, with package/settings backups, renewed approval, dependency checks and settings migration. Existing v1 plugins remain supported.
- Expanded built-in automation actions and startup, resume, lock and unlock triggers. Added per-rule enable switches, queuing, cancellation, progress and run history. CPU power Beta choices follow the processor vendor.

### Interface and display

- Added independent light/dark accent and background colors, Overview header gradients, previews and defaults. Sidebar, card and text colors adapt to the background.
- Grouped refresh rate, internal-panel resolution and DPI settings. Added panel DPI presets and 100–500% custom scaling. Custom scaling affects all displays for the current account and takes effect after signing out.
- Startup-to-tray initializes in the background. Opening the main window during initialization keeps it open.
- Disabled mouse-wheel selection changes on closed combo boxes while preserving page and expanded-list scrolling.
- First-time acknowledgement requires typing the full sentence. Users who previously accepted the disclaimer can copy/paste the complete sentence when confirming another version.

### Performance and reliability

- Added Quiet Performance on compatible devices through Overview, Performance, automation and Fn+Q settings. Switching uses Lenovo Dispatcher and verifies the performance base, BIOS flag and service cache.
- Restore the last confirmed performance mode after sleep or hibernation.
- Read core and memory overclock offset limits from the driver, with a refresh action and validation before writes. Saved values outside the new range are retained for correction.
- Suspend NVAPI Beta when NVPCF is missing, disabled or fails to read. A previously enabled preference resumes after successful detection. An explicit disable cancels recovery.
- Improved partial power readback, firmware-clamp handling and retries. Errors identify the backend, operation stage and affected locks, with fewer duplicate notifications.
- Fixed GPU-worker broken-pipe cleanup preventing reconnection.
- Use WPF pointer input to avoid the PenIMC activation failure observed after .NET runtime servicing during a long-running session. Guardian launches a fresh process for emergency fan restoration and checks its result and timeout.
- New plugin-sensor recordings retain names, units, categories, provider versions and chart bounds for later viewing after plugin updates or removal.

## Downloads

- **`ThinkBookToolkit-1.1.1-Setup.exe` (recommended)** — online installer with a selectable destination. Downloads the official Microsoft x64 .NET 9 Desktop Runtime when required and registers Guardian and the project's self-signed UIAccess certificate.
- **`ThinkBookToolkit-1.1.1-win-x64-framework-dependent.zip`** — portable package requiring the x64 .NET 9 Desktop Runtime. Configure service registration and certificate trust separately.
- **`SHA256SUMS-v1.1.1.txt`** — SHA-256 checksums for the release assets.

Exit Toolkit normally before installation so it can restore firmware automatic fan control. Update the complete package, including Guardian and GPU workers. Toolkit uses a project self-signed certificate, and SmartScreen may warn about an unknown publisher. Public packages exclude proprietary Lenovo Vantage/PC Manager DLLs and the sample plugin. NVAPI, Intel MMIO and AMD ZenStates power controls remain Beta features.

The application version is `1.1.1`. Plugin manifests support `1 / 2`, plugin assemblies and the IPC protocol remain at `1`, the replaceable fan-backend API is `1.1`, and the configuration format is `1.0`.

Full diff: [v1.1.0...v1.1.1](https://github.com/lhzlhz419/ThinkBookToolkit/compare/v1.1.0...v1.1.1)
