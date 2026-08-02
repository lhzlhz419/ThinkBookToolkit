# Changelog

All notable changes to ThinkBook Toolkit are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

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

[Unreleased]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.2.2...HEAD
[0.2.2]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.1.2...v0.2.0
[0.1.2]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/lhzlhz419/ThinkBookToolkit/releases/tag/v0.1.0
