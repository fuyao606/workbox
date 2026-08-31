# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and releases use
[Semantic Versioning](https://semver.org/).

## [0.1.1] - 2026-08-31

### Fixed

- Install the AgentRuntime test runner dependency in the standard Python requirements.
- Restore the Windows x64 runtime before the tag-triggered publish job runs.

## [0.1.0] - 2026-08-31

### Added

- Windows WPF floating ball, tray entry, reminders and snooze support.
- Local evidence capture for screenshots, active windows, OCR and supported session sources.
- Daily report drafts, evidence-aware summaries and weekly Excel export.
- Optional OpenAI-compatible assistant and LangGraph AgentRuntime boundary.
- Human-reviewed Playwright CLI daily-report automation with failure fallback.
- Sanitized example report templates and public contribution/security guidance.
- GitHub Actions CI for .NET and Python, plus tag-triggered Windows release packaging.

### Security

- Public defaults use reserved example values and do not target a real organization system.
- Personal templates are kept under the ignored `Templates/private/` directory.

[0.1.0]: https://github.com/fuyao606/workbox/releases/tag/v0.1.0
[0.1.1]: https://github.com/fuyao606/workbox/releases/tag/v0.1.1
