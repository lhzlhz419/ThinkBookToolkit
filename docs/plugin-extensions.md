# 插件扩展与兼容指南

## 版本与旧插件

PluginApi / PluginUi 程序集版本和命名管道协议保持 1；原有构造函数、接口方法、字段及宿主命令名称保留。旧清单 `ApiVersion: 1` 继续加载，默认每秒刷新、传输原有完整快照、故障后挂起。

依赖本页新能力的插件建议写 `ApiVersion: 2`。这是清单能力版本，不是新的 IPC 协议；旧 Toolkit 会明确拒绝它，避免忽略新字段后错误运行。新版同时接受 1 和 2。`RequiredCapabilities` 用于精确声明依赖；`MinimumHostVersion` 可额外指定最低 Toolkit 版本。新接口都采用附加属性或默认接口实现，旧 DLL 不需要重新编译。

可用能力：`commands.results`、`events.v1`、`scheduling.v1`、`automation.v1`、`settings.external`、`ui.editors`、`packages.v1`。可从 `PluginRequest.Context.Capabilities` 检查能力。

## 调度、数据与故障

- `RefreshIntervalMilliseconds`：250–3600000，默认 1000。
- `TransientRetryLimit`：0–3，默认 0。只对进程通信中断和超时重试，退避 2、4、8 秒；插件逻辑异常、错误协议、权限或输出非法仍会挂起。正常成功后重置连续失败次数。
- 每个插件串行运行自己的请求，不同插件可独立刷新；最多四个后台评估同时进行。慢插件不会占住插件管理锁。停用、卸载或退出后的旧响应会被丢弃。
- `DataSubscriptions` 可选择 `Snapshot`、`Features`、`Fps`、`Profiles`、`Paths`、`Recording`。省略保持 v1 行为，空数组不传 `Context.Data` 内容；仍须声明 `data.read`。相同订阅的同一轮请求共享基础快照。
- `toolkit.recording.query` 命令需要 `data.read`，参数 `count` 默认 100、范围 1–1000；返回当前内存缓冲的最近记录，不扫描全部历史文件。
- 管理页技术详情显示刷新间隔、最近响应耗时、重试次数和最近故障。传感器旧值仍按原有 10 秒规则过期，因此实时传感器不宜使用长刷新间隔。

## 命令与回执

原 `SetItsModeAsync` 等方法名仍可使用，但入口已冻结为明确注册表，本体新增方法不会自动暴露。`Context.Operations` 说明参数、可选参数和所需权限。另提供稳定别名：

| 新 ID | 原入口 |
| --- | --- |
| `toolkit.performance.mode.set` | `SetItsModeAsync` |
| `toolkit.gpu.mode.set` | `SetGpuModeAsync` |
| `toolkit.power.apply` | `ApplyPowerSettingsAsync` |
| `toolkit.fans.mode.set` | `SetFanModeAsync` |
| `toolkit.fans.full-speed.set` | `SetFullSpeedAsync` |
| `toolkit.osd.enabled.set` | `TrySetOsdEnabled` |
| `toolkit.recording.enabled.set` | `TrySetSensorRecordingEnabled` |

```csharp
new HostCommand("toolkit.performance.mode.set", new Dictionary<string, JsonElement>
{
    ["mode"] = JsonSerializer.SerializeToElement("Intelligent")
}) { RequestId = "switch-2026-001" };
```

普通插件通过下次 `PluginRequest.CommandResults` 接收逐条回执；自绘页使用 `ExecuteWithResultAsync` 直接获取。状态包括 `completed`、`failed`、`denied`、`cancelled`、`timed-out`，重复提交仍在缓存中的请求 ID 会返回原结果，不再次执行。每个会话保留最近 128 个命令结果、最多 64 个待交付回执；这是有限去重，不是跨重启的事务保证。

`Result` 保留 Runtime 方法实际返回的数据，例如功耗设置返回的状态。没有返回值的命令，`completed` 只说明原有控制方法正常完成，不能额外推断硬件回读结果。

异步宿主操作等待最多 30 秒；超时或取消等待时，已经开始的硬件操作仍可能完成，后续批内命令停止，禁止自动重发不确定的写入。同步宿主方法仍需遵守本体已有的硬件保护，超时机制不能强制中断正在执行的同步本机调用。

合法命令被硬件拒绝会返回失败回执，不再把整个插件当作崩溃挂起。协议错误、越权输出和插件自身异常仍按故障规则处理。

## 事件

通过 `EventSubscriptions` 订阅：

`plugin.enabled`、`plugin.disabled`、`host.started`、`host.suspend`、`host.resume`、`host.exiting`、`session.locked`、`session.unlocked`、`power.connected`、`power.disconnected`、`game.started`、`game.stopped`、`performance.changed`、`gpu.changed`、`settings.changed`、`state.resync`。其他运行中的订阅者可以通过事件来源识别用户停用的插件。

事件出现在 `PluginRequest.Events`，包含 `Sequence`、`Timestamp`、`Kind`、`Source` 和 `Data`。每个插件最多保留 64 条；唤醒时清除积压事件，并以最新快照重新同步。事件不是持久化消息队列，不保证睡眠或崩溃期间不丢失，插件必须结合当前快照判断状态。

退出时只给订阅者一次最多 750 ms 的通知机会，忽略返回的控制命令；不能依靠退出事件保证硬件恢复。风扇后端继续使用独立的守护恢复契约。

## 自动化、动作与快捷入口

```json
"Actions": [{
  "Id": "example.plugin.apply",
  "Title": { "Chinese": "应用预设", "English": "Apply preset" },
  "Parameters": [{ "Id": "level", "Title": { "Chinese": "级别", "English": "Level" },
    "Kind": "number", "DefaultValue": 50, "Minimum": 0, "Maximum": 100 }],
  "PageId": "example.plugin.page", "QuickAccess": ["overview", "tray"]
}],
"Triggers": [{ "Id": "example.plugin.changed", "Title": { "Chinese": "状态改变", "English": "State changed" }, "CooldownSeconds": 5 }],
"Conditions": [{ "Id": "example.plugin.ready", "Title": { "Chinese": "已就绪", "English": "Ready" } }]
```

动作进入 `EvaluateAsync` 时，`Operation` 为 `action`，`Action` 为动作 ID，`Argument` 为已经验证并补齐默认值的 JSON 对象。插件失败可返回 `ActionError`；动作没有自动重试。参数支持 boolean、number、string、choice，每个动作最多 16 项。

自动化编辑器会生成参数控件；队列、取消、运行记录沿用本体执行管理。选中的插件条件在执行前必须全部为真且未过期。插件被禁用或卸载后保留动作、事件和条件 ID，明确提示不可用，不静默跳过。

插件通过 `TriggerSignals` 返回本次发生的事件 ID，通过 `ConditionValues` 返回条件值。信号按清单冷却时间限频。快捷键可绑定包含插件动作的普通自动化；概览快捷动作仅在完整概览显示；托盘快捷动作使用清单参数默认值。`PageId` 在声明式插件页面中生成动作表单；自绘页可以调用 `IPluginPageContext.InvokeActionAsync`。

触发器或条件还可声明由宿主判断的阈值：

```json
"Threshold": { "SensorId": "toolkit.sensor.cpuTemperatureC", "Direction": "above",
  "Value": 85, "Hysteresis": 5, "DurationSeconds": 3 }
```

内置传感器需要读取权限；也可引用本插件声明的传感器。支持 above/below、持续时间和回差。触发器只在从未满足变为满足时触发，仍受冷却限制；缺失、非有限读数或过长采样间隔会中断持续判定。插件返回的布尔值不能覆盖宿主阈值判断。

## 跨设置联动

新权限 `settings.write` 与 `host.control` 独立。清单 `WritableSettings` 必须逐个列出目标：

```json
"Permissions": ["settings.write"],
"WritableSettings": [
  { "PluginId": "toolkit", "SettingId": "toolkit.setting.Theme" },
  { "PluginId": "example.lighting", "SettingId": "preset" }
]
```

其他插件必须在目标设置上声明 `AllowExternalWrite: true`，且该设置不能是替换项。目标插件需处于可用状态。启用确认框会列出修改范围和风险，旧权限不会自动获得写入能力。

- `toolkit.settings.write` 参数：`pluginId`、`settingId`、`value`，可选 `expectedValue` 用于拒绝覆盖已经变化的新值。
- `toolkit.settings.writeBatch` 参数：`changes`，为 1–32 个 `PluginSettingWrite` 的数组。先检查全部目标权限、类型、范围、预期值，再依次应用。
- 本体稳定设置目前包括刷新间隔、语言、主题、日志等级、日志保留天数、概览模式；硬件功能继续通过明确的宿主命令控制。
- 每个目标至少间隔 2 秒，抑制插件互相修改形成的反馈循环。变更记录来源并发出 `settings.changed`。
- 批量操作不是跨文件/硬件的原子事务。持久化失败或插件中途停止时，回执 `Result` 列出已经完成的目标，调用方应向用户说明部分完成，不能声称全部回滚。

## UI 工具

设置可选 `Editor`：`slider`（需要上下限的 number）、`file`、`folder`、`color`、`multiline`（string）。保存仍通过原类型和范围校验。

`VisibleWhen` / `EnabledWhen` 使用 `{ "SettingId": "enabled", "EqualsValue": true }`，只引用本插件设置；`DisabledReason` 提供双语禁用说明。`PluginResult.AvailableChoices` 可以按设置 ID 返回清单 Choices 的当前可用子集，不能动态引入未声明选项；旧保存值保留并提示不可用。

自绘页面的 `PluginPageState.Theme` 提供本体实际色板（包括透明度）。可选 `PluginControls` 包含卡片、响应式设置行、状态文本、只读表格和简单曲线。调用 `ConfirmAsync` 使用本体风格的确认框。宿主 UI 仍不是沙箱，不保证强制卸载任意 WPF 代码。

## 更新、回退与迁移

管理页每个插件提供“更新插件”和“回退版本”。更新确认展示旧/新版本和新权限；更新包必须为同 ID。文件先验证、暂存并写入任务记录，重启后再切换，不覆盖运行中 DLL。原包和配置备份保留在插件 `.rollback` 与配置 `backups` 目录。

重启前可以在插件卡片上取消暂存的变更。切换完成后默认停用，需要重新审核。回退同时恢复最近备份的包与设置。执行前重新检查目录范围、链接和文件指纹。安全启动跳过待更新任务。

`Dependencies` 声明依赖 ID、`MinimumVersion` 与 `MaximumVersionExclusive`，启用时检查版本、可用状态和循环依赖；不自动安装或启用依赖。替换冲突显示具体插件与目标，仍由用户停用冲突提供者后切换。

配置继续保存原 JSON 字典格式。增加 `SettingsVersion`，以及可选 `SettingRenames: [{"From":"old","To":"new"}]`；升级时先备份，仅把兼容值复制到尚不存在的新键，不执行插件迁移代码。不兼容的设置使用新清单默认值，原文件备份保留；迁移失败会禁用插件并允许回退。

## 历史记录与验证

记录文件的 v2/v3 首行和采样格式保持兼容，额外写入一行 `toolkit.plugin-metadata.v1` 元数据。旧读取器会忽略它；新查看器优先使用记录当时的插件 ID、版本、名称、单位、类别及绘图范围。提供者元数据变化时开始新文件，避免同一列跨版本含义混杂。

`--test-plugins` 包含扩展集成回归。设置 `TBT_TEST_PLUGIN_ARCHIVE` 为升级前保存的测试插件 ZIP，可直接验证旧逻辑 DLL 和旧 WPF DLL。设置 `TBT_TEST_LEGACY_FAN_BACKEND` 为此前编译的测试风扇后端 DLL，可复用相同的模拟后端运行加载和恢复测试。测试探针编译在 UI 测试程序中，测试插件单独分发。
