# Changelog

All notable changes to ThinkBook Toolkit are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-08-24

### Added

- Added a full Automation page with ordered device-control, application,
  delay, and macro steps; manual execution; AC-power and game-state triggers;
  and configurable Fn-key single- and double-press bindings.
- Added editable keyboard macros with low-level recording, per-event key,
  Down/Up state and delay editing, ordinary-key bindings, serialized playback,
  safe key release, and detailed execution logging.
- Added Fn-key discovery for both Lenovo WMI and EnergyDrv events. Every
  discovered raw key can be added to the custom key list without conflating
  identical values from different event sources.
- Added an independent Lenovo driver, firmware, and BIOS update page. Toolkit
  now reads Lenovo's public machine-type catalog directly, evaluates package
  applicability, verifies hashes and Lenovo signatures, and serializes
  per-package download and installation without Lenovo System Update DLLs.
- Added BIOS I/O and virtualization controls for the settings actually exposed
  by the current firmware, with up to three responsive controls per row.
- Added custom game-detection include and exclude application lists. Exclusions
  override Windows GameConfigStore, explicit inclusions, effective Game Mode,
  and inherited child-process detection.
- Added a bilingual, per-version startup risk acknowledgement. Hardware
  initialization and fan-backend notices do not continue until the exact
  acknowledgement is typed and saved for the current application version.
- Added Dynamic Refresh Rate discovery and switching alongside fixed panel
  refresh rates, with external Windows/Fn+R changes reflected in the Display
  page.
- Added independently enabled NVIDIA overclock parameters and optional core
  and memory clock ranges. Disabled parameters are left untouched.
- Added separate feature monitoring for native full fan speed, keyboard
  macros, automation, driver updates, BIOS I/O controls, Fn takeover, refresh
  rates, GPU management, and GPU overclocking.

### Changed

- Feature detection now records the completion time and result of every probe,
  logs concrete partial/unavailable reasons, avoids opening the hardware stack
  twice, and lets Overview become usable before slower background readings.
- Hardware monitoring now initializes storage and discrete-GPU telemetry in
  the background, handles GPU removal without terminating the application, and
  falls back to integrated-GPU or CPU-only data when appropriate.
- Game detection now combines GameConfigStore entries, effective Windows Game
  Mode, foreground processes, descendant processes, and the custom path lists.
- Automation and macro cards share the same controls, colors, responsive rows,
  editor cards, collapsed-by-default behavior, and unique case-insensitive
  names with generated numeric suffixes.
- Fn-triggered UI updates now refresh visible controls in place instead of
  destroying and recreating pages, eliminating page flashes and preserving
  responsive input.
- Fn double-press handling no longer blocks the driver event queue, and custom
  raw-key bindings retain microphone-event deduplication.
- Native full fan speed is probed independently from ordinary fan control. If
  native full speed is unavailable, Toolkit can default to the configured
  maximum-RPM fallback without hiding ordinary controls.
- The Performance-mode controller now prefers modern
  `LenovoProcessManagement`, but falls back to legacy `LITSSVC` commands on
  older ITS systems; unsupported Geek mode is omitted on the legacy path.
- Driver-update cards now show Lenovo package titles and categories, place
  required updates before current packages, expose independent queued install
  actions, and optionally retain successfully installed packages as current.
- Expanded automation and macro logs with trigger source, queueing, step/event
  progress, elapsed time, failure/cancellation details, and privacy-safe
  redaction of application paths and arguments.

### Fixed

- Fixed fan curves retaining a stale target when GPU temperature is unavailable;
  CPU temperature alone now remains sufficient for curve calculation.
- Fixed external refresh-rate changes not updating the current Display-page
  selection.
- Fixed old Delay enum values being interpreted as Macro steps after macro
  support was introduced. Numeric delays and GUID macro references are now
  migrated in both directions while their numeric identifiers remain stable.
- Fixed duplicate automation and macro names, stale bindings after deletion,
  empty macro editor borders, theme-inconsistent game lists, and mismatched
  automation/macro control sizes.
- Fixed partial power-setting displays reserving empty grid slots and improved
  handling when only a subset of WMI power values is readable.
- Fixed status-refresh actions rebuilding entire pages when Fn keys or
  automations changed an input, display, sound, battery, cooling, or performance
  control.

## [0.2.7] - 2026-08-12

### Added

- Added discrete-GPU lifecycle management for Hybrid Auto and Hybrid iGPU
  modes. Toolkit releases NVIDIA monitoring resources before firmware removes
  the adapter, follows PnP-confirmed disconnect and reconnect events, and shows
  desktop notifications when the discrete GPU changes state.
- Added discrete-GPU status and NVIDIA performance-state reporting to Overview
  and Performance, together with views for GPU-using applications and an
  optional action to close those applications.
- Added NVIDIA GPU overclock controls for core and memory offsets plus an
  optional core-clock range, with explicit safety guidance and reset support.
- Added optional Lenovo Hotkeys takeover for ThinkBook Fn keys, topmost OSD
  notifications, configurable Fn+Q performance-mode order, Fn+R refresh-rate
  cycling, and Toolkit-provided CapsLock and NumLock OSD handling.
- Added laptop-panel refresh-rate selection and a configurable list shared by
  the Display page and Fn+R. The default cycle contains 60 Hz and the highest
  available refresh rate.
- Added disabled, immediate, and delayed Windows startup modes.
- Added the application version to the native title bar and Settings. Manual
  update checking uses the latest stable GitHub Release and exposes a download
  button inline when a newer version is available.

### Changed

- Upgraded LibreHardwareMonitorLib to `0.9.7-pre717` and aligned its sensor
  support dependencies with the versions required by that build.
- Moved NVIDIA telemetry and control into the isolated GPU worker and tightened
  startup and mode-transition coordination so sensor polling no longer keeps
  an eject-capable discrete GPU connected.
- Performance-mode and fan-strategy linkage now waits two seconds after a
  performance-mode switch is confirmed before applying the requested fan
  strategy. Fan-initiated linkage no longer changes the fan strategy when the
  linked performance-mode switch fails.
- Power-limit locks now retain their per-performance-mode values across mode
  changes, and Geek mode is omitted while the computer is running on battery.
- Refined the fan-curve editor layout: profile and name share one row, fan
  selection moved beside the chart, and the independent-fan toggle now uses
  direct rather than inverted wording.
- Settings navigation now uses a short fade-and-slide transition, and update,
  GPU, refresh-rate, and Fn-key capabilities appear in feature monitoring.

### Fixed

- Restored the previously selected fan strategy after a normal restart rather
  than always returning to firmware automatic control.
- Fixed discrete-GPU removal failures caused by active hardware telemetry and
  improved fallback to integrated-GPU readings after the adapter disappears.
- Reduced Fn-key input latency and missed rapid key presses, synchronized mute
  and microphone indicator LEDs, restored Fn+R handling, and restored Lenovo
  Hotkeys whenever Toolkit exits or Windows shuts down.
- Preserved the current performance mode across Windows shutdown and startup
  without changing ordinary manual-exit behavior.
- Hardened microphone endpoint handling against unsupported COM interfaces and
  moved operation results to non-blocking Toolkit notifications.
- Removed the unavailable PCH temperature reading and its unused embedded
  hardware module.

## [0.2.6] - 2026-08-09

### Added

- Added Compact and Detailed Overview modes. Compact mode includes configurable
  CPU, GPU, battery, memory, fan, and warranty cards; Detailed mode retains the
  complete hardware, power-limit, and warranty presentation.
- Added two-way, independently configurable linkage between performance modes
  and fan strategies, including per-mode curve-profile selection and a
  no-switch whitelist.
- Added a demand-start Windows guardian service that monitors the Toolkit
  process, restores firmware automatic fan control after forced termination,
  and then stops itself.

### Changed

- Moved the compact live-status panel to the Cooling page, added combined VRAM
  and hot-spot temperatures, and removed live status from the Performance page.
- Isolated GPU telemetry in a child process so native display-driver failures do
  not terminate the main application; non-NVIDIA telemetry is selected while
  the discrete adapter is unavailable.
- Renamed the supported 14-inch model to `ThinkBook 14 G6+ IMH` throughout the
  application and tests.
- Updated the application icon with transparent corners and aligned Overview
  subtitles with the readings that remain enabled.

### Fixed

- Setup now asks a running Toolkit instance to restore firmware automatic fan
  control and exit before files are replaced. Older versions that do not
  support the update-exit signal receive a focused tray-exit prompt, and the
  guardian service is stopped before Restart Manager checks file locks.
- Restored firmware automatic fan control during Windows session shutdown and
  blocked late background writes once shutdown preparation begins.
- Prevented disabled or removed discrete-GPU telemetry failures from crashing
  the main process, and allowed integrated-GPU readings to take over afterward.
- Removed empty slots when only part of the power-setting interface is readable.
- Corrected compact fan-target formatting and warranty remaining-day visibility.

## [0.2.5] - 2026-08-08

### Added

- Added configurable Overview cards and readings, including power-limit and
  warranty cards. The Performance live-status cards follow the same choices.
- Expanded CPU, GPU, memory, storage, battery, and fan telemetry, including
  per-chip VRAM temperatures when supported.
- Added writable power profiles for ThinkBook 16p G5 IRX and ThinkBook 14 G6+
  IMH, partial-read support on other WMI-capable devices, and power locks
  stored independently for each performance mode.
- Added per-session logging, force-refreshing of hardware readers, and
  single-instance startup handling.

### Changed

- Split Performance and Cooling into separate navigation pages and moved fan
  controls to Cooling.
- Refined responsive layouts, Overview power-value visibility, compact setting
  rows, dark native title bars, and the startup/application behavior section.
- On ThinkBook 16p G6 IAX, GPU Power Boost retains a 0–15 slider while manual
  input accepts any non-negative integer.

### Fixed

- Added display-adapter presence checks and NVIDIA telemetry-handle refreshes
  to avoid stale discrete-GPU access after the adapter is removed or disabled.
- Improved logging for hardware refreshes, fan sampling, GPU-mode changes, and
  unhandled task exceptions.

## [0.2.4] - 2026-08-02

### Changed

- Moved each per-parameter power lock switch to the far right of its setting
  row, after the slider or selector, for consistent alignment and scanning.

## [0.2.3] - 2026-08-02

### Changed

- Replaced the single power-setting lock switch with an independent lock
  switch for every available power parameter. The common check interval remains
  configurable at 1, 2, 3, 5, or 10 seconds.
- Background enforcement now compares and writes only locked parameters;
  changes to unlocked parameters no longer trigger a restore.
- Existing v0.2.2 configurations with the global power lock enabled are
  migrated to per-parameter locks without changing their saved target values.

## [0.2.2] - 2026-08-02

### Added

- Added persistent power-setting locking. Toolkit can check the current power
  values every 1, 2, 3, 5, or 10 seconds and restore the saved target only
  when a value differs.
- Added optional ATPP read/write support through Lenovo Other Method feature
  `0x02040000`, including a 25–105 W slider, unrestricted positive-integer
  manual input, live readout, and per-performance-mode defaults.

### Changed

- Power-setting reads, manual writes, and background lock enforcement are now
  serialized so they cannot access the firmware interface concurrently.
- ATPP availability is reported independently: an unavailable ATPP interface
  no longer reduces the availability of the original power controls, while a
  detected writable interface is identified in feature monitoring.

## [0.2.1] - 2026-07-31

### Changed

- The installer now explicitly warns when an existing custom fan backend has
  an incompatible or unreadable file version and will be replaced.
- Raised the replaceable fan-backend API to 1.1. Backends can now optionally
  provide localized startup notice text; notice suppression is reset whenever
  the backend DLL contents change.
- Changed the lowest Advanced Curve default target from 0/0 RPM to
  1500/1500 RPM. Advanced Curve endpoints now obey the configured fan RPM
  limits as well.
- Public Release packages no longer include PDB files, and user-facing crash
  dialogs show a concise error while retaining full diagnostics in the crash
  log.

### Fixed

- Fixed the Performance page crashing while loading fixed-RPM drafts when the
  selected fan backend is unavailable.
- Replaced build-machine source paths in development symbols with a stable
  mapped source root.

## [0.2.0] - 2026-07-29

### Added

- Added the Advanced Curve fan strategy with editable CPU/GPU hysteresis
  thresholds, per-point fan targets, temperature smoothing, and independent
  ramp-up and ramp-down limits.
- Added a horizontally scrollable point table. Every point can insert a new
  point to its right or be removed while retaining at least two points.
- Added full-range ramp-up and ramp-down limits to the existing Fan Curve
  strategy, while retaining the separate post-high-temperature ramp-down limit.
- Added installer support for preserving a compatible replaceable fan backend
  during an overwrite installation.

### Changed

- Hardware targets produced by curve strategies are rounded to 100 RPM.
- Updated the Advanced Curve default rates to 50/20 RPM/s for points 1-4,
  100/50 RPM/s for points 5-9, and unlimited/100 RPM/s for points 10-12.
- Chinese rate selectors now display `无限制` instead of `inf`.
- Existing configurations that still contain an earlier built-in Advanced
  Curve are migrated to the new defaults without replacing customized curves.
- The tray fan-strategy menu now includes Advanced Curve.

### Fixed

- Fixed vertical and horizontal wheel interaction over the Advanced Curve
  table and aligned its fixed labels with the scrollable cells.
- Fixed the installer so a saved custom Lenovo DLL directory is preselected
  and placed in the directory input field.
- Fixed error 740 when launching Toolkit from the installer completion page by
  launching Toolkit as the original non-elevated user.

## [0.1.2] - 2026-07-28

### Fixed

- Corrected GPU-mode pending-restart tracking and presentation when changing
  the requested mode more than once before restarting.

## [0.1.1] - 2026-07-26

### Added

- Added the .NET 9 online installer and optional custom Lenovo DLL directory.

## [0.1.0] - 2026-07-26

### Added

- First public release with the replaceable fan-backend contract and the core
  ThinkBook Toolkit device controls.

[Unreleased]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.2.7...v1.0.0
[0.2.7]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.2.6...v0.2.7
[0.2.6]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.2.5...v0.2.6
[0.2.5]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.2.4...v0.2.5
[0.2.4]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.2.3...v0.2.4
[0.2.3]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.2.2...v0.2.3
[0.2.2]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.1.2...v0.2.0
[0.1.2]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/lhzlhz419/ThinkBookToolkit/releases/tag/v0.1.0
