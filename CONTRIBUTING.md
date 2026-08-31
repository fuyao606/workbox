# Contributing to workbox

感谢你对 workbox 的关注。提交 Issue 或 Pull Request 前，请先确认问题可以在不暴露个人数据的前提下复现。

## 开发环境

- Windows 10/11
- .NET SDK 10.0
- Python 3.10+（修改 `AgentRuntime` 时）
- Node.js 18+（修改 Playwright 自动化时）

安装依赖并运行测试：

```powershell
dotnet restore Timenotifier.Tests\Timenotifier.Tests.csproj
dotnet build Timenotifier.csproj --configuration Release --no-restore
dotnet test Timenotifier.Tests\Timenotifier.Tests.csproj --configuration Release --no-restore
python -m pytest AgentRuntime\tests -q
```

## 分支与提交

- 从默认分支创建短生命周期功能分支。
- 提交保持单一目的，使用清晰的英文提交主题，例如 `feat: ...`、`fix: ...`、`docs: ...`。
- 不提交 `bin`、`obj`、`publish`、截图、会话记录、应用状态、API Key 或数据库文件。
- 不提交真实 Excel 模板；个人模板应放在已忽略的 `Templates/private/`。
- 涉及核心业务规则、跨模块契约或较大重构时，先在 `openspec/changes` 添加变更说明。
- 版本号统一维护在 `Directory.Build.props`，提交用户可见变化时同步更新 `CHANGELOG.md`。

## Pull Request

PR 描述应说明：

- 变更解决的问题和用户可见行为。
- 影响到的模块与配置项。
- 已运行的测试命令及结果。
- 是否需要更新 README、模板或迁移本地数据。
- 是否包含隐私、凭据、自动提交或第三方服务风险。

新增核心逻辑应补充 xUnit 测试。修改模板字段时，必须同步检查导出服务和对应测试。

## 发布

发布流程使用语义化版本号和带注释的 `vMAJOR.MINOR.PATCH` 标签。请按
[docs/RELEASING.md](docs/RELEASING.md) 完成测试、敏感信息扫描和 Windows 发布包检查，
再推送标签触发 GitHub Actions Release 工作流。

## 隐私与安全

截图、活动窗口标题、OCR 文本、AI 会话、日报内容和 Excel 模板可能包含个人或公司信息。提交前请使用占位数据，并检查 Git diff 和 Git 历史。发现凭据泄露时，应立即撤销并轮换凭据，再通过 Git 历史清理处理泄露内容。

## 行为准则

参与本项目即表示你愿意以尊重、具体和可复现的方式交流。维护者可以关闭不符合项目范围、包含敏感数据或无法安全验证的 Issue/PR。
