# Security Policy

## Supported versions

安全修复优先应用于默认分支和最近的版本标签。项目处于早期阶段，旧版本可能不会持续获得维护。

## Reporting a vulnerability

不要在公开 Issue 中提交 API Key、数据库密钥、截图、会话日志或可识别个人信息。

请通过 GitHub Security Advisories（启用后）或维护者私下渠道报告安全问题，并提供：

- 受影响的版本或提交。
- Windows 与 .NET 环境信息。
- 最小复现步骤和影响范围。
- 已脱敏的日志或示例数据。

收到报告后，维护者会先确认复现和影响，再评估修复与公开披露时间。

## Sensitive data guidance

workbox 会处理截图、窗口标题、OCR 文本、AI 会话、报告草稿、模型凭据和 Trae CN 数据库密钥。请始终使用本地测试数据；不要把这些内容提交到仓库。若凭据已经提交到 Git 历史，立即吊销并轮换，同时清理历史记录。

组织或个人 Excel 模板应放在 `Templates/private/`。该目录已被 `.gitignore` 排除；公开
模板只包含示例占位值。
