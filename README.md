# workbox

Windows 桌面时间提醒、工作证据采集与日报/周报自动化工作台。应用本体在
`Timenotifier` 项目中实现，仓库名称保留为 `workbox` 以兼容现有发布与脚本。

[![CI](https://github.com/fuyao606/workbox/actions/workflows/ci.yml/badge.svg)](https://github.com/fuyao606/workbox/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/fuyao606/workbox?display_name=tag)](https://github.com/fuyao606/workbox/releases)
[![License](https://img.shields.io/github/license/fuyao606/workbox)](LICENSE)

workbox 以本地优先为原则：提醒、截图、会话读取、报告草稿和配置由桌面端
协调；AI 和浏览器自动化是可选增强，即使它们不可用，基础提醒与本地记录仍可用。
项目目前主要面向 Windows 和中文工作流。

## 功能概览

- **悬浮球与托盘入口**：常驻显示时间，支持展开、隐藏、贴边停靠，并将提醒弹窗
  定位到当前工作区。
- **定时提醒**：提供默认规则、稍后提醒、工作日与节假日调整。
- **多来源证据采集**：支持手动记录、全屏截图、活动窗口信息、OCR，以及可选的
  Codex、Trae、Trae CN 和 DeepSeek Harness 会话读取。
- **日报草稿**：按日期聚合证据，生成可人工检查的日报，保留时间范围、来源和证据
  关联。
- **周报导出**：将周报草稿写入企业或学校 Excel 模板，并支持从历史草稿重建累计
  内容。
- **自动化辅助提交**：通过 Playwright CLI 打开目标页面、填写并截图；提交前提供
  审核入口，失败时保留人工处理路径。
- **AI 助手**：支持配置 OpenAI Responses 或 Chat Completions 兼容服务，用于查询
  本地状态、整理证据和润色报告。
- **Python AgentRuntime**：`AgentRuntime` 提供 LangGraph 扩展边界，不承载 WPF
  窗口生命周期，运行失败不会阻断桌面端启动。

## 安全默认值

开源版本不会指向任何真实组织系统。日报自动化默认关闭，目标地址、项目名和任务名
均为 `example.invalid`/示例值；启用自动化前必须在配置中心填入自己的地址、测试账号
和字段，并人工确认最终提交内容。

截图、窗口标题、OCR 文本、AI 会话、报告草稿、Excel 模板和模型凭据都可能包含敏感
信息。请遵守以下边界：

1. 不要把 API Key、数据库密钥、截图、会话日志或真实报告提交到 Git。
2. 真实模板放在 `Templates/private/`，该目录已加入 `.gitignore`。
3. 使用第三方模型前确认其数据处理政策；应用只在触发 AI 功能时向配置的地址发送
   请求。
4. 发现凭据泄露时立即撤销并轮换，再清理 Git 历史；只删除当前文件并不能撤销泄露。

## 技术栈

- .NET 10、WPF、Windows Forms、C# Nullable/Implicit Usings
- MaterialDesignInXaml `5.3.2`
- DocumentFormat.OpenXml、Markdig、PdfPig、SkiaSharp
- SQLitePCLRaw SQLCipher、ZstdSharp
- xUnit、Microsoft.NET.Test.Sdk
- Python 3.10+、LangGraph、PyInstaller（仅 AgentRuntime）
- Node.js 18+、Playwright CLI（仅浏览器自动化）

## 环境要求

- Windows 10/11
- .NET SDK 10.0（项目目标框架为 `net10.0-windows`）
- Python 3.10+（运行 AgentRuntime 或其测试时）
- Node.js 18+（使用日报浏览器自动化时）
- 可选：全局安装 Playwright CLI

```powershell
npm install --global @playwright/cli
```

## 快速开始

```powershell
git clone https://github.com/fuyao606/workbox.git
Set-Location workbox

# 还原依赖并启动桌面端
dotnet restore Timenotifier.Tests\Timenotifier.Tests.csproj
dotnet run --project Timenotifier.csproj

# 运行 .NET 测试
dotnet test Timenotifier.Tests\Timenotifier.Tests.csproj --configuration Release
```

只构建桌面端：

```powershell
dotnet build Timenotifier.csproj --configuration Release
```

启用 Python AgentRuntime：

```powershell
py -3 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r AgentRuntime\requirements.txt
.\.venv\Scripts\python.exe -m pytest AgentRuntime\tests -q
```

如果系统没有 `py` 启动器，可将其替换为 `python`。AgentRuntime 使用标准输入/输出
传输 JSON 请求，详见 `AgentRuntime/timenotifier_agent/server.py`。

## 配置与数据位置

应用首次启动会在 `%APPDATA%\Timenotifier` 创建本地数据目录：

| 内容 | 默认位置 |
| --- | --- |
| 设置 | `%APPDATA%\Timenotifier\settings.json` |
| 证据 | `%APPDATA%\Timenotifier\evidence` |
| 截图 | `%APPDATA%\Timenotifier\screenshots` |
| 报告草稿 | `%APPDATA%\Timenotifier\reports` |
| 浏览器配置 | `%APPDATA%\Timenotifier\browser-profile` |

模型服务在配置中心填写 Base URL、模型名称和 API Key。API Key 使用 Windows 当前
用户的 DPAPI 保护后保存，不要通过配置文件或环境变量共享。

Trae CN 原生数据库采集是可选的。只有在确认本机数据库密钥来源并理解其敏感性后，
才在当前用户环境中设置：

```powershell
setx TRAE_CN_AI_AGENT_DB_KEY "<64位hex密钥>"
```

密钥无效、数据库不存在或单个会话损坏时，采集器会跳过该来源，不影响其他来源和基础
提醒功能。

## 报告模板

仓库提供两份去标识化示例模板：

- `Templates/enterprise-weekly-report-template.xlsx`
- `Templates/school-weekly-report-template.xlsx`

示例中的姓名、单位、联系方式和工作内容都是占位数据。复制模板到自己的工作目录后，
替换占位字段，再从配置中心执行导出。导出器会把累计状态写入应用数据目录中的副本，
不会修改仓库里的源模板。组织模板请放入 `Templates/private/`，不要强制加入 Git。

## 目录结构

```text
.
├── Application/             # WPF 启动组装与生命周期协调
├── Views/                   # 窗口 XAML、交互代码与 UI 生命周期
├── Models/                  # 配置、提醒、证据和报告数据结构
├── Services/
│   ├── Assistant/           # AI 助手上下文、动作和会话
│   ├── Automation/          # 日报自动化与外部提交流程
│   ├── Evidence/            # 截图、手动证据、存储与聚类
│   ├── Infrastructure/      # 路径、托盘、坐标、时间和模型客户端
│   ├── Reporting/           # 日报、周报、模板和导出
│   ├── Scheduling/          # 提醒、自动截图和自动化调度
│   └── Settings/            # 配置读取、写入与默认值
├── AgentRuntime/            # Python/LangGraph 辅助运行时
├── Assets/                  # 图标和静态资源
├── Templates/               # 去标识化报告模板
├── Timenotifier.Tests/      # xUnit 测试
├── docs/                    # 维护与发布文档
├── openspec/                # 可追溯变更说明
├── Directory.Build.props    # 集中版本和仓库元数据
└── Timenotifier.csproj      # 桌面端项目文件
```

`Views` 只处理展示、交互和窗口生命周期；业务规则放在 `Services/*`。Python
AgentRuntime 不能直接持有 WPF UI 状态或窗口引用。关键业务服务位于
`Services/Automation`、`Services/Evidence`、`Services/Infrastructure`、
`Services/Reporting`、`Services/Scheduling` 和 `Services/Settings`。

## 测试与质量门禁

本地提交前运行：

```powershell
dotnet restore Timenotifier.Tests\Timenotifier.Tests.csproj
dotnet build Timenotifier.csproj --configuration Release --no-restore
dotnet test Timenotifier.Tests\Timenotifier.Tests.csproj --configuration Release --no-restore
python -m pytest AgentRuntime\tests -q
```

GitHub Actions 会在 Windows runner 上执行 .NET 还原、构建和测试，并在 Linux runner
上执行 AgentRuntime 测试。UI Loop 和真实浏览器提交依赖登录态及桌面环境，不在 CI 中
自动提交；请按 `docs/RELEASING.md` 做发布前人工检查。

## 版本与发布

版本号集中定义在 `Directory.Build.props`，遵循语义化版本 `MAJOR.MINOR.PATCH`：

1. 在 `CHANGELOG.md` 中记录用户可见变化。
2. 更新 `Directory.Build.props` 的 `VersionPrefix`。
3. 运行完整测试和 Release 构建。
4. 创建带注释标签并推送，例如：

   ```powershell
   git tag -a v0.1.1 -m "Release v0.1.1"
   git push origin main
   git push origin v0.1.1
   ```

推送 `v*.*.*` 标签会触发 `.github/workflows/release.yml`，生成 Windows x64 压缩包
并创建 GitHub Release。完整清单见 [docs/RELEASING.md](docs/RELEASING.md)。

## 常见问题

### AgentRuntime 必须启动吗？

不需要。提醒、悬浮球、基础配置、本地证据和报告草稿在 Python 运行时不可用时仍应
继续工作。

### 数据会自动上传吗？

不会。证据和配置默认保存在本机；只有用户配置模型服务并触发 AI 功能，或显式运行
浏览器自动化时，相关数据才会离开本机。

### 为什么导出文件名是英文？

开源默认模板使用跨平台的中性文件名，避免把个人姓名或学号写入源码。使用者可以在
本地复制模板并按自己的归档规范重命名。

### 如何报告安全问题？

请阅读 [SECURITY.md](SECURITY.md)，不要在公开 Issue 中粘贴凭据、截图、会话内容或
可识别个人信息。

## 参与贡献

请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。新增核心业务规则应补充 xUnit 测试；
涉及跨层契约或较大重构时，在 `openspec/changes` 添加可追溯变更说明。

## 许可证

本项目采用 [MIT License](LICENSE)。第三方依赖遵循各自的许可证。
