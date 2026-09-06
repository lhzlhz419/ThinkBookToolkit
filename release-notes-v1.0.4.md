# ThinkBook Toolkit v1.0.4

> **免责声明：** ThinkBook Toolkit 是独立开发的实验性项目，与联想公司无关，不是联想官方项目，也未获得联想的认可、支持或赞助。本软件会读取并写入硬件和固件设置，使用者须自行承担全部风险和后果。所有功能仅在 **ThinkBook 16p G6 IAX（BIOS R2CN57WW）** 上测试，不保证在其他机型或 BIOS 版本上可用或安全。去售后前，请卸载 ThinkBook Toolkit 或拔掉安装该软件的硬盘，避免不必要的麻烦。

## 主要更新

### 独立显卡监控与恢复

- 新增独立的非 UIAccess GPU worker，用于隔离 NVIDIA 遥测、显卡超频和占用应用管理；避免 UIAccess 主程序阻止混合显卡卸载。
- 独立显卡状态区分“无”（设备未枚举）和“关闭”（设备已断电）；支持在 Hybrid、Hybrid iGPU 和 Hybrid Auto 模式下重启独立显卡。
- 显卡重新出现后恢复可用功能；NVAPI 功耗能力仅在 NVIDIA 独立显卡处于活跃状态时检测。
- 临时断开独立显卡时保留 NVAPI 功耗开关和独立的模式锁定配置，恢复后重新应用。

### 日志与兼容性

- 设置中新增 Log 等级：INFO、WARN、ERROR 和“无”；默认仅记录错误。
- 改进 GPU worker 在显卡状态变化和显式重启后的重启、命名管道握手与资源释放。
- 保修日期提取排除“智询常伴”服务项目，并将保修缓存格式升级到版本 4。

### 发布与安全

- 公共发布包包含独立 GPU worker，并对主程序、GPU worker、Guardian 和安装包使用项目自签名证书签名。
- 发布脚本会检查公共包，发现 Lenovo Vantage/电脑管家专有 DLL 时停止打包。

## 下载

- **`ThinkBookToolkit-1.0.4-Setup.exe`（推荐）**：在线安装程序，可选择安装位置；检测不到 x64 .NET 9 Desktop Runtime 时会下载微软官方运行时，并注册 Guardian 服务和 UIAccess 自签名证书。
- **`ThinkBookToolkit-1.0.4-win-x64-framework-dependent.zip`**：便携版，需要系统已安装 x64 .NET 9 Desktop Runtime；不会自动注册服务或信任证书。
- **`SHA256SUMS-v1.0.4.txt`**：发布文件的 SHA-256 校验值。

安装和退出前请正常退出 Toolkit，让风扇恢复自动控制。重启独立显卡需要管理员权限，并可能暂时中断显示或硬件监控。

本版本使用项目自签名证书，不代表商业 CA 签名或 SmartScreen 信誉。公开发布包不包含 Lenovo Vantage/电脑管家的专有 DLL。NVAPI、Intel MMIO 和 AMD ZenStates 功耗功能仍为 Beta。项目为独立开发，非联想官方软件，硬件控制存在风险。

软件版本为 `1.0.4`，可替换风扇后端 API 版本为 `1.1`，配置文件格式版本为 `1.0`。

完整变更：[v1.0.3...v1.0.4](https://github.com/lhzlhz419/ThinkBookToolkit/compare/v1.0.3...v1.0.4)

---

> **Disclaimer:** ThinkBook Toolkit is an independently developed experimental project. It is not affiliated with, endorsed, supported, or sponsored by Lenovo. The application reads and writes hardware and firmware settings; you accept all risks and consequences. Every feature was tested only on **ThinkBook 16p G6 IAX with BIOS R2CN57WW** and is not guaranteed to work or be safe elsewhere. Before requesting after-sales service, uninstall ThinkBook Toolkit or remove the drive containing it to avoid unnecessary complications.

## Highlights

### Isolated GPU monitoring and recovery

- Added a dedicated non-UIAccess GPU worker for isolated NVIDIA telemetry, GPU overclocking and application management, preventing the UIAccess main executable from blocking hybrid-GPU removal.
- Discrete-GPU status now distinguishes an absent adapter (`None`) from a powered-off adapter (`Off`). Added a Restart discrete GPU action for Hybrid, Hybrid iGPU and Hybrid Auto modes.
- GPU capabilities recover when the adapter returns; NVAPI power capability probing is deferred until an active NVIDIA discrete GPU is available.
- NVAPI power preferences and independent per-mode locks survive temporary dGPU disappearance and are reapplied after recovery.

### Logging and compatibility

- Added INFO, WARN, ERROR and None log-level settings; the default records errors only.
- Improved GPU-worker restart, named-pipe handshakes and resource cleanup after adapter changes and explicit GPU restarts.
- Excluded the `智询常伴` service entry from warranty-date extraction and upgraded the warranty cache schema to version 4.

### Release packaging

- Public release packages include the isolated GPU worker. The main executable, GPU worker, Guardian and installer are signed with the project's self-signed certificate.
- The release script now stops if proprietary Lenovo Vantage/Lenovo PC Manager DLLs are present in a public package.

## Downloads

- **`ThinkBookToolkit-1.0.4-Setup.exe` (recommended):** online installer with a selectable destination. Downloads the official Microsoft x64 .NET 9 Desktop Runtime when required and registers Guardian and the project's self-signed UIAccess certificate.
- **`ThinkBookToolkit-1.0.4-win-x64-framework-dependent.zip`:** portable package; requires the x64 .NET 9 Desktop Runtime. It does not automatically register services or trust certificates.
- **`SHA256SUMS-v1.0.4.txt`:** SHA-256 checksums for the release assets.

Exit Toolkit normally before installation so it can restore firmware automatic fan control. Restarting the discrete GPU requires administrator permission and may briefly interrupt display output or hardware monitoring.

This version uses a project self-signed certificate, which does not imply commercial CA trust or SmartScreen reputation. Public packages do not include proprietary DLLs from Lenovo Vantage or Lenovo PC Manager. NVAPI, Intel MMIO and AMD ZenStates power controls remain Beta features. ThinkBook Toolkit is independently developed, is not an official Lenovo application, and hardware control carries risks.

The application version is `1.0.4`, the replaceable fan-backend API is `1.1`, and the configuration-file format is `1.0`.

Full diff: [v1.0.3...v1.0.4](https://github.com/lhzlhz419/ThinkBookToolkit/compare/v1.0.3...v1.0.4)
