using Timenotifier.Services.Reporting;
using Timenotifier.Views.Assistant;
using Xunit;

namespace Timenotifier.Tests;

public sealed class EvidenceSummaryDisplayFormatterTests
{
    [Fact]
    public void Format_splits_session_metadata_goals_and_results_without_mutating_meaning()
    {
        var display = EvidenceSummaryDisplayFormatter.Format(
            """
            来源：Codex
            工作目录：D:\Code\workbox
            目标概览：1. # Files mentioned by the user: ## codex-clipboard.png: C:/Users/test/AppData/Local/Temp/codex-clipboard.png ## My request for Codex: 优化配置中心中的回顾输出；2. 补充截图验证
            工作结果概括：1. 已将回顾卡片拆分为可读字段；2. 已补充格式化测试
            """);

        Assert.Equal("Codex", display.Source);
        Assert.Equal("D:\\Code\\workbox", display.WorkingDirectory);
        Assert.Collection(
            display.Sections,
            goals =>
            {
                Assert.Equal("目标概览", goals.Title);
                Assert.Equal(["优化配置中心中的回顾输出", "补充截图验证"], goals.Items);
            },
            results =>
            {
                Assert.Equal("工作结果", results.Title);
                Assert.Equal(["已将回顾卡片拆分为可读字段", "已补充格式化测试"], results.Items);
            });
    }

    [Fact]
    public void Format_keeps_unstructured_evidence_as_readable_summary_items()
    {
        var display = EvidenceSummaryDisplayFormatter.Format(
            "用户主动保存的全屏工作截图。\r\n活跃窗口：日报配置中心");

        var summary = Assert.Single(display.Sections);
        Assert.Equal("摘要", summary.Title);
        Assert.Equal(["用户主动保存的全屏工作截图。", "活跃窗口：日报配置中心"], summary.Items);
    }

    [Fact]
    public void Assistant_output_normalizer_formats_legacy_session_summary()
    {
        var markdown = AssistantOutputFormatter.NormalizeMarkdown(
            """
            来源：Codex
            工作目录：D:\Code\workbox
            目标概览：1. # Files mentioned by the user: ## image.png: C:/Users/test/Desktop/image.png ## My request for Codex: 让回顾内容更容易阅读
            工作结果概括：1. 已完成回顾卡片格式化；2. 已验证显示结果
            """);

        Assert.Contains("**来源**：Codex", markdown);
        Assert.Contains("**工作目录**：D:\\Code\\workbox", markdown);
        Assert.Contains("**目标概览**", markdown);
        Assert.Contains("- 让回顾内容更容易阅读", markdown);
        Assert.Contains("**工作结果概括**", markdown);
        Assert.DoesNotContain("Files mentioned by the user", markdown);
    }
}
