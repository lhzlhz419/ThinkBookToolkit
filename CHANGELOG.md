# Changelog

All notable changes to ThinkBook Toolkit are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

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

[Unreleased]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.1.2...v0.2.0
[0.1.2]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/lhzlhz419/ThinkBookToolkit/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/lhzlhz419/ThinkBookToolkit/releases/tag/v0.1.0
