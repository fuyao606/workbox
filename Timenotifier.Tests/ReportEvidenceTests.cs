using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO.Compression;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Xml.Linq;
using SQLitePCL;
using Timenotifier.Application;
using Timenotifier.Models;
using Xunit;
using Timenotifier.Services.Evidence;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Reporting;
using Timenotifier.Services.Scheduling;
using Timenotifier.Services.Settings;
using ZstdSharp;

namespace Timenotifier.Tests;

public sealed class ReportEvidenceTests
{
    [Fact]
    public async Task Codex_reader_extracts_daily_session_evidence()
    {
        var root = CreateTempDirectory();
        try
        {
            var sessionDate = new DateOnly(2026, 6, 26);
            var sessionId = "019f-test-session";
            Directory.CreateDirectory(Path.Combine(root, "sessions", "2026", "06", "26"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "session_index.jsonl"),
                JsonSerializer.Serialize(new { id = sessionId, thread_name = "修复悬浮球隐藏后丢失" }) + Environment.NewLine);
            var sessionPath = Path.Combine(root, "sessions", "2026", "06", "26", "rollout-test.jsonl");
            await File.WriteAllLinesAsync(sessionPath,
            [
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-26T01:00:00Z",
                    type = "session_meta",
                    payload = new { session_id = sessionId, cwd = "D:\\Code\\项目\\Timenotifier" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-26T01:01:00Z",
                    type = "event_msg",
                    payload = new { type = "user_message", message = "修复悬浮球隐藏后丢失" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-26T01:02:00Z",
                    type = "response_item",
                    payload = new { type = "function_call", name = "shell_command" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-26T01:03:00Z",
                    type = "event_msg",
                    payload = new { type = "agent_message", message = "已完成修复并通过测试。" }
                })
            ]);

            var reader = new CodexSessionEvidenceReader(root);
            var evidence = await reader.ReadAsync(new EvidenceRange(sessionDate, sessionDate));

            var item = Assert.Single(evidence);
            Assert.Equal($"codex:{sessionId}:20260626", item.Id);
            Assert.Equal(EvidenceKinds.CodexSession, item.Kind);
            Assert.Equal("修复悬浮球隐藏后丢失", item.Title);
            Assert.DoesNotContain("执行工具调用", item.Summary);
            Assert.Contains("已完成修复并通过测试", item.Summary);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Codex_reader_attributes_historical_session_continuation_to_current_day()
    {
        var root = CreateTempDirectory();
        try
        {
            var yesterday = new DateOnly(2026, 7, 6);
            var today = yesterday.AddDays(1);
            var sessionId = "019f-cross-day-session";
            var directory = Path.Combine(root, "sessions", yesterday.Year.ToString("0000"), yesterday.Month.ToString("00"), yesterday.Day.ToString("00"));
            Directory.CreateDirectory(directory);
            await File.WriteAllLinesAsync(Path.Combine(directory, $"{sessionId}.jsonl"),
            [
                JsonSerializer.Serialize(new { timestamp = $"{yesterday:yyyy-MM-dd}T23:59:00+08:00", type = "session_meta", payload = new { session_id = sessionId, cwd = "D:\\Code\\项目\\Timenotifier" } }),
                JsonSerializer.Serialize(new { timestamp = $"{today:yyyy-MM-dd}T09:01:00+08:00", type = "event_msg", payload = new { type = "user_message", message = "今天继续处理日报证据" } }),
                JsonSerializer.Serialize(new { timestamp = $"{today:yyyy-MM-dd}T09:02:00+08:00", type = "event_msg", payload = new { type = "agent_message", message = "已将历史会话按今天归档。" } })
            ]);

            var reader = new CodexSessionEvidenceReader(root);
            var evidence = await reader.ReadAsync(new EvidenceRange(today, today));

            var item = Assert.Single(evidence);
            Assert.Equal($"codex:{sessionId}:20260707", item.Id);
            Assert.Equal(today, DateOnly.FromDateTime(item.CapturedAt));
            Assert.Contains("今天继续处理日报证据", item.Summary);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Codex_reader_collects_trae_and_trae_cn_session_sources()
    {
        var traeRoot = CreateTempDirectory();
        var traeCnRoot = CreateTempDirectory();
        try
        {
            var sessionDate = new DateOnly(2026, 7, 7);
            var sessionId = "019f-shared-session";
            await WriteSessionAsync(
                traeRoot,
                sessionDate,
                sessionId,
                "Trae 调整日报证据",
                "接入 Trae 工作记录作为日报证据。",
                "已整理 Trae 侧会话摘要。");
            await WriteSessionAsync(
                traeCnRoot,
                sessionDate,
                sessionId,
                "Trae CN 调整日报证据",
                "接入 Trae CN 工作记录作为日报证据。",
                "已整理 Trae CN 侧会话摘要。");

            var reader = new CodexSessionEvidenceReader(
            [
                new CodexSessionEvidenceSource("Trae", traeRoot, "trae"),
                new CodexSessionEvidenceSource("Trae CN", traeCnRoot, "trae-cn")
            ]);
            var evidence = await reader.ReadAsync(new EvidenceRange(sessionDate, sessionDate));

            Assert.Equal(2, evidence.Count);
            var trae = Assert.Single(evidence, item => item.Id == $"trae:{sessionId}:20260707");
            var traeCn = Assert.Single(evidence, item => item.Id == $"trae-cn:{sessionId}:20260707");
            Assert.Equal("Trae 调整日报证据", trae.Title);
            Assert.Equal("Trae CN 调整日报证据", traeCn.Title);
            Assert.Contains("来源：Trae", trae.Summary);
            Assert.Equal(EvidenceKinds.TraeCnSession, traeCn.Kind);
            Assert.Equal("traecn", traeCn.DisplayKind);
            Assert.Empty(traeCn.Tags);
            Assert.Contains("来源：Trae CN", traeCn.Summary);
            Assert.Contains("Trae CN 工作记录", traeCn.Summary);
        }
        finally
        {
            Directory.Delete(traeRoot, recursive: true);
            Directory.Delete(traeCnRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Deepseek_harness_reader_extracts_daily_events_from_zstd_session()
    {
        var root = CreateTempDirectory();
        try
        {
            var sessionId = "session-dsh-test";
            var sessionDate = new DateOnly(2026, 7, 7);
            var sessionDirectory = Path.Combine(root, "project", sessionId);
            Directory.CreateDirectory(sessionDirectory);
            var createdAt = new DateTimeOffset(2026, 7, 7, 8, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
            var userAt = createdAt + 60_000;
            var assistantAt = createdAt + 120_000;
            var jsonl = string.Join(Environment.NewLine,
                JsonSerializer.Serialize(new { type = "session", version = 0, id = sessionId, createdAt, cwd = "D:\\Code\\项目\\Timenotifier" }),
                JsonSerializer.Serialize(new { type = "user/message", seq = 0, time = userAt, data = new { role = "user", content = new[] { new { type = "text", text = "接入 DSH 日报证据" } } } }),
                JsonSerializer.Serialize(new { type = "assistant/message", seq = 1, time = assistantAt, data = new { message = new { role = "assistant", content = new[] { new { type = "text", text = "已接入并完成测试。" }, new { type = "tool-call", text = "ignored" } } } } })) + Environment.NewLine;
            using (var output = File.Create(Path.Combine(sessionDirectory, "session.jsonl.zstd")))
            using (var compressor = new CompressionStream(output, 3, 0, false))
            {
                var bytes = Encoding.UTF8.GetBytes(jsonl);
                compressor.Write(bytes, 0, bytes.Length);
            }

            var reader = new DeepSeekHarnessEvidenceReader(root);
            var evidence = await reader.ReadAsync(new EvidenceRange(sessionDate, sessionDate));

            var item = Assert.Single(evidence);
            Assert.Equal($"dsh:{sessionId}:20260707", item.Id);
            Assert.Equal(EvidenceKinds.DeepSeekHarnessSession, item.Kind);
            Assert.Equal("DSH", item.DisplayKind);
            Assert.Contains("接入 DSH 日报证据", item.Summary);
            Assert.Contains("已接入并完成测试", item.Summary);
            Assert.DoesNotContain("ignored", item.Summary);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Codex_reader_default_sources_include_trae_cn_modular_data_bridge()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var expectedHome = Path.Combine(appData, "Trae CN", "ModularData", "ai-agent");

        var sources = CodexSessionEvidenceReader.CreateDefaultSources();

        Assert.Contains(
            sources,
            source => source.Name == "Trae CN"
                && source.IdPrefix == "trae-cn"
                && string.Equals(source.Home, expectedHome, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Codex_reader_collects_trae_cn_modular_data_bridge_sessions()
    {
        var modularRoot = CreateTempDirectory();
        try
        {
            var sessionDate = new DateOnly(2026, 7, 7);
            var sessionId = "019f-trae-cn-modular-session";
            await WriteSessionAsync(
                modularRoot,
                sessionDate,
                sessionId,
                "Trae CN ModularData 日报证据",
                "通过 Trae CN ModularData 明文桥接接入工作记录。",
                "已把 Trae CN 桥接会话转换为日报证据。");

            var reader = new CodexSessionEvidenceReader(
            [
                new CodexSessionEvidenceSource("Trae CN", modularRoot, "trae-cn")
            ]);
            var evidence = await reader.ReadAsync(new EvidenceRange(sessionDate, sessionDate));

            var item = Assert.Single(evidence);
            Assert.Equal($"trae-cn:{sessionId}:20260707", item.Id);
            Assert.Equal("Trae CN ModularData 日报证据", item.Title);
            Assert.Equal(EvidenceKinds.TraeCnSession, item.Kind);
            Assert.Equal("traecn", item.DisplayKind);
            Assert.Empty(item.Tags);
            Assert.Contains("ModularData 明文桥接", item.Summary);
            Assert.Contains("桥接会话转换为日报证据", item.Summary);
        }
        finally
        {
            Directory.Delete(modularRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Codex_reader_deduplicates_legacy_and_modular_trae_cn_sources_by_session_id()
    {
        var legacyRoot = CreateTempDirectory();
        var modularRoot = CreateTempDirectory();
        try
        {
            var sessionDate = new DateOnly(2026, 7, 7);
            var sessionId = "019f-trae-cn-shared-session";
            await WriteSessionAsync(
                legacyRoot,
                sessionDate,
                sessionId,
                "旧 Trae CN 日报证据",
                "旧 Trae CN sessions 目录工作记录。",
                "旧目录生成的日报证据。");
            await WriteSessionAsync(
                modularRoot,
                sessionDate,
                sessionId,
                "新 Trae CN 日报证据",
                "新版 Trae CN ModularData 桥接工作记录。",
                "新版桥接目录生成的日报证据。");

            var reader = new CodexSessionEvidenceReader(
            [
                new CodexSessionEvidenceSource("Trae CN", legacyRoot, "trae-cn"),
                new CodexSessionEvidenceSource("Trae CN", modularRoot, "trae-cn")
            ]);
            var evidence = await reader.ReadAsync(new EvidenceRange(sessionDate, sessionDate));

            var item = Assert.Single(evidence);
            Assert.Equal($"trae-cn:{sessionId}:20260707", item.Id);
            Assert.Equal("新 Trae CN 日报证据", item.Title);
            Assert.Equal(EvidenceKinds.TraeCnSession, item.Kind);
            Assert.Equal("traecn", item.DisplayKind);
            Assert.Empty(item.Tags);
            Assert.Contains("新版 Trae CN ModularData", item.Summary);
        }
        finally
        {
            Directory.Delete(legacyRoot, recursive: true);
            Directory.Delete(modularRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Codex_reader_returns_empty_when_modular_data_bridge_has_no_sessions()
    {
        var modularRoot = CreateTempDirectory();
        try
        {
            var sessionDate = new DateOnly(2026, 7, 7);
            var reader = new CodexSessionEvidenceReader(
            [
                new CodexSessionEvidenceSource("Trae CN", modularRoot, "trae-cn")
            ]);

            var evidence = await reader.ReadAsync(new EvidenceRange(sessionDate, sessionDate));

            Assert.Empty(evidence);
        }
        finally
        {
            Directory.Delete(modularRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Trae_cn_ai_agent_reader_collects_native_database_messages()
    {
        var root = CreateTempDirectory();
        try
        {
            var sessionDate = new DateOnly(2026, 7, 7);
            var sessionId = "019f-trae-cn-native-session";
            CreateTraeCnAiAgentDatabase(
                root,
                sessionDate,
                sessionId,
                "Trae CN 原生日报证据",
                "修复 Trae CN 今日工作记录采集为空",
                "已接入 SQLCipher 原生数据库读取并补充测试。");

            var reader = new TraeCnAiAgentEvidenceReader(root, TestTraeCnDbKey);
            var evidence = await reader.ReadAsync(new EvidenceRange(sessionDate, sessionDate));

            var item = Assert.Single(evidence);
            Assert.Equal($"trae-cn:{sessionId}:20260707", item.Id);
            Assert.Equal("Trae CN 原生日报证据", item.Title);
            Assert.Equal(EvidenceKinds.TraeCnSession, item.Kind);
            Assert.Equal("traecn", item.DisplayKind);
            Assert.Empty(item.Tags);
            Assert.Contains("来源：Trae CN", item.Summary);
            Assert.Contains("修复 Trae CN 今日工作记录采集为空", item.Summary);
            Assert.Contains("SQLCipher 原生数据库读取", item.Summary);
            Assert.Equal(Path.Combine(root, "database.db"), item.SourcePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Composite_evidence_deduplicates_legacy_trae_cn_jsonl_and_native_database()
    {
        var dataRoot = CreateTempDirectory();
        var legacyRoot = CreateTempDirectory();
        var nativeRoot = CreateTempDirectory();
        try
        {
            var sessionDate = new DateOnly(2026, 7, 7);
            var sessionId = "019f-trae-cn-dedup-native-session";
            await WriteSessionAsync(
                legacyRoot,
                sessionDate,
                sessionId,
                "旧 Trae CN 日报证据",
                "旧 Trae CN sessions 目录工作记录。",
                "旧目录生成的日报证据。");
            CreateTraeCnAiAgentDatabase(
                nativeRoot,
                sessionDate,
                sessionId,
                "新 Trae CN 原生证据",
                "新版 Trae CN 原生数据库工作记录。",
                "新版原生库生成的日报证据。");

            var store = new EvidenceStore(new AppDataPaths(dataRoot));
            var collector = new CompositeEvidenceCollector(
            [
                new CodexSessionEvidenceCollector(new CodexSessionEvidenceReader(
                [
                    new CodexSessionEvidenceSource("Trae CN", legacyRoot, "trae-cn")
                ])),
                new TraeCnAiAgentEvidenceCollector(new TraeCnAiAgentEvidenceReader(nativeRoot, TestTraeCnDbKey))
            ], store);

            var evidence = await collector.CollectAndStoreAsync(new EvidenceRange(sessionDate, sessionDate));

            var item = Assert.Single(evidence);
            Assert.Equal($"trae-cn:{sessionId}:20260707", item.Id);
            Assert.Equal("新 Trae CN 原生证据", item.Title);
            Assert.Contains("新版 Trae CN 原生数据库", item.Summary);
            Assert.DoesNotContain("旧 Trae CN sessions", item.Summary);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
            Directory.Delete(legacyRoot, recursive: true);
            Directory.Delete(nativeRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Codex_reader_skips_locked_session_file()
    {
        var root = CreateTempDirectory();
        try
        {
            var sessionDate = new DateOnly(2026, 6, 26);
            var directory = Path.Combine(root, "sessions", "2026", "06", "26");
            Directory.CreateDirectory(directory);
            var lockedPath = Path.Combine(directory, "locked.jsonl");
            await File.WriteAllTextAsync(lockedPath, "{}");

            await using var locked = new FileStream(
                lockedPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            var reader = new CodexSessionEvidenceReader(root);
            var evidence = await reader.ReadAsync(new EvidenceRange(sessionDate, sessionDate));

            Assert.Empty(evidence);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Codex_reader_uses_valid_lines_when_latest_line_is_incomplete()
    {
        var root = CreateTempDirectory();
        try
        {
            var sessionDate = new DateOnly(2026, 6, 26);
            var sessionId = "019f-partial-session";
            Directory.CreateDirectory(Path.Combine(root, "sessions", "2026", "06", "26"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "session_index.jsonl"),
                JsonSerializer.Serialize(new { id = sessionId, thread_name = "实时刷新证据" }) + Environment.NewLine);
            var sessionPath = Path.Combine(root, "sessions", "2026", "06", "26", "partial.jsonl");
            await File.WriteAllLinesAsync(sessionPath,
            [
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-26T07:00:00Z",
                    type = "session_meta",
                    payload = new { session_id = sessionId, cwd = "D:\\Code\\项目\\Timenotifier" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-26T07:01:00Z",
                    type = "event_msg",
                    payload = new { type = "user_message", message = "点击刷新今日证据需要更新页面" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-26T07:02:00Z",
                    type = "event_msg",
                    payload = new { type = "agent_message", message = "已读取到最新有效内容。" }
                }),
                "{\"timestamp\":\"2026-06-26T07:03:00Z\",\"type\":\"event_msg\""
            ]);

            var reader = new CodexSessionEvidenceReader(root);
            var evidence = await reader.ReadAsync(new EvidenceRange(sessionDate, sessionDate));

            var item = Assert.Single(evidence);
            Assert.Equal("实时刷新证据", item.Title);
            Assert.Contains("已读取到最新有效内容", item.Summary);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Codex_reader_summarizes_multiple_work_items_in_one_long_session()
    {
        var root = CreateTempDirectory();
        try
        {
            var sessionDate = new DateOnly(2026, 6, 30);
            var sessionId = "019f-long-session";
            Directory.CreateDirectory(Path.Combine(root, "sessions", "2026", "06", "30"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "session_index.jsonl"),
                JsonSerializer.Serialize(new { id = sessionId, thread_name = "优化 Timenotifier 报告链路" }) + Environment.NewLine);
            var sessionPath = Path.Combine(root, "sessions", "2026", "06", "30", "long.jsonl");
            await File.WriteAllLinesAsync(sessionPath,
            [
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-30T01:00:00Z",
                    type = "session_meta",
                    payload = new { session_id = sessionId, cwd = "D:\\Code\\项目\\Timenotifier" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-30T01:01:00Z",
                    type = "event_msg",
                    payload = new { type = "user_message", message = "优化报告生成内容，调整提示词和测试。" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-30T01:02:00Z",
                    type = "event_msg",
                    payload = new { type = "agent_message", message = "已优化报告提示词，补充职场简洁、合并归纳和证据约束，并通过测试。" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-30T01:03:00Z",
                    type = "response_item",
                    payload = new { type = "function_call", name = "shell_command" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-30T01:04:00Z",
                    type = "event_msg",
                    payload = new { type = "user_message", message = "修复模型连接测试，API 基地址不要强行拼到 responses。" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-30T01:05:00Z",
                    type = "response_item",
                    payload = new
                    {
                        type = "message",
                        content = new[]
                        {
                            new { text = "已将 Analysis 测试默认改为 Chat Completions，并保留 Responses 兜底。" }
                        }
                    }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-30T01:06:00Z",
                    type = "event_msg",
                    payload = new { type = "user_message", message = "删除补齐 v1 按钮并重新打包 exe。" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-30T01:07:00Z",
                    type = "event_msg",
                    payload = new { type = "agent_message", message = "已删除补齐 /v1 按钮，移除后台事件方法，完成测试并发布测试包。" }
                })
            ]);

            var reader = new CodexSessionEvidenceReader(root);
            var evidence = await reader.ReadAsync(new EvidenceRange(sessionDate, sessionDate));

            var item = Assert.Single(evidence);
            Assert.Equal("优化 Timenotifier 报告链路", item.Title);
            Assert.Contains("目标概览", item.Summary);
            Assert.Contains("优化报告生成内容", item.Summary);
            Assert.Contains("修复模型连接测试", item.Summary);
            Assert.Contains("工作结果概括", item.Summary);
            Assert.Contains("已优化报告提示词", item.Summary);
            Assert.Contains("默认改为 Chat Completions", item.Summary);
            Assert.DoesNotContain("删除补齐 v1 按钮", item.Summary);
            Assert.DoesNotContain("已删除补齐 /v1 按钮", item.Summary);
            Assert.DoesNotContain("打包 exe", item.Summary);
            Assert.DoesNotContain("发布测试包", item.Summary);
            Assert.DoesNotContain("结果：已删除补齐 /v1 按钮", item.Summary);
            Assert.True(item.Summary.Length <= 903);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Codex_reader_filters_process_noise_from_session_summary()
    {
        var root = CreateTempDirectory();
        try
        {
            var sessionDate = new DateOnly(2026, 6, 30);
            var sessionId = "019f-noise-session";
            Directory.CreateDirectory(Path.Combine(root, "sessions", "2026", "06", "30"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "session_index.jsonl"),
                JsonSerializer.Serialize(new { id = sessionId, thread_name = "LangChain 学习与调试台" }) + Environment.NewLine);
            var sessionPath = Path.Combine(root, "sessions", "2026", "06", "30", "noise.jsonl");
            await File.WriteAllLinesAsync(sessionPath,
            [
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-30T02:00:00Z",
                    type = "session_meta",
                    payload = new { session_id = sessionId, cwd = "D:\\Code\\项目\\langchain1.2" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-30T02:01:00Z",
                    type = "event_msg",
                    payload = new { type = "user_message", message = "安装 playwright-cli 技能并打包 exe。" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-30T02:02:00Z",
                    type = "event_msg",
                    payload = new { type = "agent_message", message = "打包 exe 并生成 zip 测试包，dotnet test 已通过。" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-30T02:03:00Z",
                    type = "event_msg",
                    payload = new { type = "user_message", message = "生成 LangChain 学习导图，输出 4K PNG 与 SVG 矢量版。" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-30T02:04:00Z",
                    type = "event_msg",
                    payload = new { type = "agent_message", message = "完成 LangChain 学习导图高清版本输出，覆盖 RAG、Embedding、切分算法和上下文工程主题。" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-30T02:05:00Z",
                    type = "event_msg",
                    payload = new { type = "user_message", message = "实现 Embedding 与文档切分调试台练习入口。" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-06-30T02:06:00Z",
                    type = "event_msg",
                    payload = new { type = "agent_message", message = "实现 python -m src.embeddinglab 调试台，支持观察 chunk 切分、embedding 检索结果和 /sources 逻辑，调试台验证通过。" }
                })
            ]);

            var reader = new CodexSessionEvidenceReader(root);
            var evidence = await reader.ReadAsync(new EvidenceRange(sessionDate, sessionDate));

            var item = Assert.Single(evidence);
            Assert.Contains("LangChain 学习导图", item.Summary);
            Assert.Contains("Embedding 与文档切分调试台", item.Summary);
            Assert.Contains("RAG", item.Summary);
            Assert.DoesNotContain("打包 exe", item.Summary);
            Assert.DoesNotContain("生成 zip", item.Summary);
            Assert.DoesNotContain("dotnet test", item.Summary);
            Assert.DoesNotContain("执行工具调用", item.Summary);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Evidence_store_preserves_included_edits()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new EvidenceStore(new AppDataPaths(root));
            var capturedAt = new DateTime(2026, 6, 26, 10, 0, 0);
            await store.UpsertEvidenceAsync(
            [
                new EvidenceItem
                {
                    Id = "e1",
                    Kind = EvidenceKinds.Screenshot,
                    CapturedAt = capturedAt,
                    Title = "截图",
                    Included = true
                }
            ]);

            await store.UpsertEvidenceAsync(
            [
                new EvidenceItem
                {
                    Id = "e1",
                    Kind = EvidenceKinds.Screenshot,
                    CapturedAt = capturedAt,
                    Title = "截图",
                    Included = false
                }
            ]);

            var evidence = await store.ReadEvidenceAsync(new DateOnly(2026, 6, 26), new DateOnly(2026, 6, 26));

            Assert.False(Assert.Single(evidence).Included);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Screenshot_note_updates_same_screenshot_evidence_with_text_and_tags()
    {
        var evidence = new EvidenceItem
        {
            Id = "screenshot:20260708103000",
            Kind = EvidenceKinds.Screenshot,
            CapturedAt = new DateTime(2026, 7, 8, 10, 30, 0),
            Title = "工作截图 10:30:00",
            Summary = "用户主动保存的全屏工作截图。" + Environment.NewLine + "活跃窗口：日报配置中心",
            Tags = ["旧标签"],
            ImagePath = "D:\\shots\\manual.png",
            OcrText = "日报配置中心",
            WindowTitle = "Timenotifier",
            ProcessName = "Timenotifier.exe",
            Included = true
        };

        AppUiCoordinator.ApplyScreenshotNote(
            evidence,
            "完成日报证据联调\n补充截图说明",
            ["日报", "开发", "日报", " "]);

        Assert.Equal(EvidenceKinds.Screenshot, evidence.Kind);
        Assert.Equal("D:\\shots\\manual.png", evidence.ImagePath);
        Assert.Equal("日报配置中心", evidence.OcrText);
        Assert.Equal("Timenotifier", evidence.WindowTitle);
        Assert.Equal("Timenotifier.exe", evidence.ProcessName);
        Assert.StartsWith("工作记录：完成日报证据联调", evidence.Summary);
        Assert.Contains("补充截图说明", evidence.Summary);
        Assert.Contains("用户主动保存的全屏工作截图。", evidence.Summary);
        Assert.Equal("完成日报证据联调", evidence.Title);
        Assert.Equal(["日报", "开发"], evidence.Tags);
    }

    [Fact]
    public async Task Screenshot_record_cancel_deletes_evidence_and_image_file()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new EvidenceStore(new AppDataPaths(root));
            var date = new DateOnly(2026, 7, 8);
            var imagePath = Path.Combine(root, "cancelled.png");
            await File.WriteAllTextAsync(imagePath, "image");
            await store.UpsertEvidenceAsync(
            [
                new EvidenceItem
                {
                    Id = "cancelled-screenshot",
                    Kind = EvidenceKinds.Screenshot,
                    CapturedAt = date.ToDateTime(new TimeOnly(10, 30)),
                    ImagePath = imagePath,
                    Summary = "用户主动保存的全屏工作截图。"
                }
            ]);

            var deleted = await store.DeleteEvidenceAsync("cancelled-screenshot");
            var evidence = await store.ReadEvidenceAsync(date, date);

            Assert.True(deleted);
            Assert.Empty(evidence);
            Assert.False(File.Exists(imagePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Settings_store_round_trips_capture_settings()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new AppSettingsStore(new AppDataPaths(root));
            await store.SaveEvidenceCaptureSettingsAsync(new EvidenceCaptureSettings
            {
                AutoScreenshotEnabled = true,
                ScreenshotIntervalMinutes = 7,
                ScreenshotDirectory = Path.Combine(root, "shots"),
                MaxAutoScreenshots = 12,
                ManualNoteHotkey = "Ctrl+Alt+R",
                StartWithWindows = true,
                OcrEnabled = true,
                PrivacyFilters = ["password", "secret.exe"],
                DefaultTags = ["开发", "排查"]
            });

            var settings = await store.LoadEvidenceCaptureSettingsAsync();

            Assert.True(settings.AutoScreenshotEnabled);
            Assert.Equal(7, settings.ScreenshotIntervalMinutes);
            Assert.Equal(12, settings.MaxAutoScreenshots);
            Assert.Equal("Ctrl+Alt+R", settings.ManualNoteHotkey);
            Assert.True(settings.StartWithWindows);
            Assert.True(settings.OcrEnabled);
            Assert.Contains("password", settings.PrivacyFilters);
            Assert.Contains("排查", settings.DefaultTags);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Settings_store_capture_defaults_are_useful()
    {
        var root = CreateTempDirectory();
        try
        {
            var settings = await new AppSettingsStore(new AppDataPaths(root)).LoadEvidenceCaptureSettingsAsync();

            Assert.Equal(10, settings.ScreenshotIntervalMinutes);
            Assert.Equal(50, settings.MaxAutoScreenshots);
            Assert.Equal("Ctrl+Alt+N", settings.ManualNoteHotkey);
            Assert.Contains("开发", settings.DefaultTags);
            Assert.False(settings.AutoScreenshotEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Evidence_store_prunes_oldest_auto_screenshots_only()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new EvidenceStore(new AppDataPaths(root));
            var date = new DateOnly(2026, 6, 26);
            var oldPath = Path.Combine(root, "old.png");
            var keepPath = Path.Combine(root, "keep.png");
            var manualPath = Path.Combine(root, "manual.png");
            await File.WriteAllTextAsync(oldPath, "old");
            await File.WriteAllTextAsync(keepPath, "keep");
            await File.WriteAllTextAsync(manualPath, "manual");

            await store.UpsertEvidenceAsync(
            [
                AutoScreenshot("auto-old", date.ToDateTime(new TimeOnly(9, 0)), oldPath),
                AutoScreenshot("auto-keep", date.ToDateTime(new TimeOnly(10, 0)), keepPath),
                new EvidenceItem
                {
                    Id = "manual",
                    Kind = EvidenceKinds.Screenshot,
                    CapturedAt = date.ToDateTime(new TimeOnly(8, 0)),
                    ImagePath = manualPath,
                    IsAutoCaptured = false
                }
            ]);

            await store.PruneAutoScreenshotEvidenceAsync(1);
            var evidence = await store.ReadEvidenceAsync(date, date);

            Assert.DoesNotContain(evidence, item => item.Id == "auto-old");
            Assert.Contains(evidence, item => item.Id == "auto-keep");
            Assert.Contains(evidence, item => item.Id == "manual");
            Assert.False(File.Exists(oldPath));
            Assert.True(File.Exists(keepPath));
            Assert.True(File.Exists(manualPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Evidence_store_deletes_selected_evidence_and_screenshot_file()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new EvidenceStore(new AppDataPaths(root));
            var date = new DateOnly(2026, 6, 26);
            var deletePath = Path.Combine(root, "delete.png");
            var keepPath = Path.Combine(root, "keep.png");
            await File.WriteAllTextAsync(deletePath, "delete");
            await File.WriteAllTextAsync(keepPath, "keep");
            await store.UpsertEvidenceAsync(
            [
                new EvidenceItem
                {
                    Id = "delete-me",
                    Kind = EvidenceKinds.Screenshot,
                    CapturedAt = date.ToDateTime(new TimeOnly(9, 0)),
                    ImagePath = deletePath
                },
                new EvidenceItem
                {
                    Id = "keep-me",
                    Kind = EvidenceKinds.Screenshot,
                    CapturedAt = date.ToDateTime(new TimeOnly(10, 0)),
                    ImagePath = keepPath
                }
            ]);

            var deleted = await store.DeleteEvidenceAsync("delete-me");
            var evidence = await store.ReadEvidenceAsync(date, date);

            Assert.True(deleted);
            Assert.DoesNotContain(evidence, item => item.Id == "delete-me");
            Assert.Contains(evidence, item => item.Id == "keep-me");
            Assert.False(File.Exists(deletePath));
            Assert.True(File.Exists(keepPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Evidence_store_deletes_multiple_evidence_items_in_one_call()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new EvidenceStore(new AppDataPaths(root));
            var date = new DateOnly(2026, 6, 26);
            await store.UpsertEvidenceAsync(
            [
                new EvidenceItem { Id = "delete-1", CapturedAt = date.ToDateTime(new TimeOnly(9, 0)) },
                new EvidenceItem { Id = "delete-2", CapturedAt = date.ToDateTime(new TimeOnly(10, 0)) },
                new EvidenceItem { Id = "keep", CapturedAt = date.ToDateTime(new TimeOnly(11, 0)) }
            ]);

            var deleted = await store.DeleteEvidenceAsync(["delete-1", "delete-2"]);
            var evidence = await store.ReadEvidenceAsync(date, date);

            Assert.Equal(2, deleted);
            Assert.DoesNotContain(evidence, item => item.Id == "delete-1");
            Assert.DoesNotContain(evidence, item => item.Id == "delete-2");
            Assert.Contains(evidence, item => item.Id == "keep");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Deleted_evidence_ids_are_not_collected_again()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new EvidenceStore(new AppDataPaths(root));
            var date = new DateOnly(2026, 6, 26);
            var evidence = new EvidenceItem
            {
                Id = "codex:deleted",
                Kind = EvidenceKinds.CodexSession,
                CapturedAt = date.ToDateTime(new TimeOnly(9, 0)),
                Title = "已删除证据",
                Included = true
            };
            await store.UpsertEvidenceAsync([evidence]);
            await store.DeleteEvidenceAsync(evidence.Id);

            var collector = new CompositeEvidenceCollector(
                [new FakeEvidenceCollector(evidence)],
                store);
            var collected = await collector.CollectAndStoreAsync(new EvidenceRange(date, date));

            Assert.DoesNotContain(collected, item => item.Id == evidence.Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Weekly_evidence_collection_deduplicates_same_id_across_days()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new EvidenceStore(new AppDataPaths(root));
            await store.UpsertEvidenceAsync(
            [
                new EvidenceItem
                {
                    Id = "codex:same-thread",
                    Kind = EvidenceKinds.CodexSession,
                    CapturedAt = new DateTime(2026, 6, 30, 10, 0, 0),
                    Title = "旧证据",
                    Included = false
                },
                new EvidenceItem
                {
                    Id = "codex:same-thread",
                    Kind = EvidenceKinds.CodexSession,
                    CapturedAt = new DateTime(2026, 7, 1, 10, 0, 0),
                    Title = "新证据",
                    Included = true
                }
            ]);
            var collector = new CompositeEvidenceCollector(
                [new FakeEvidenceCollector(new EvidenceItem
                {
                    Id = "codex:same-thread",
                    Kind = EvidenceKinds.CodexSession,
                    CapturedAt = new DateTime(2026, 7, 1, 11, 0, 0),
                    Title = "采集证据"
                })],
                store);

            var evidence = await collector.CollectAndStoreAsync(new EvidenceRange(
                new DateOnly(2026, 6, 29),
                new DateOnly(2026, 7, 3)));

            var item = Assert.Single(evidence, item => item.Id == "codex:same-thread");
            Assert.True(item.Included);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Manual_evidence_service_saves_note_with_tags_and_window()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new EvidenceStore(new AppDataPaths(root));
            var service = new ManualEvidenceService(
                store,
                new FakeTimeProvider(new DateTime(2026, 6, 26, 11, 0, 0)),
                new FakeActiveWindowService(new ActiveWindowInfo("需求文档", "notepad")));

            var evidence = await service.SaveNoteAsync("完成接口说明", ["文档", "开发"]);

            Assert.Equal(EvidenceKinds.ManualNote, evidence.Kind);
            Assert.Equal("完成接口说明", evidence.Summary);
            Assert.Contains("文档", evidence.Tags);
            Assert.Equal("需求文档", evidence.WindowTitle);
            Assert.Equal("notepad", evidence.ProcessName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Manual_screenshot_uses_configured_capture_directory()
    {
        var root = CreateTempDirectory();
        try
        {
            var configuredDirectory = Path.Combine(root, "configured-shots");
            var settings = new EvidenceCaptureSettings
            {
                ScreenshotDirectory = configuredDirectory,
                DefaultTags = ["开发"]
            };
            var store = new EvidenceStore(new AppDataPaths(root));
            var capture = new ScreenshotCaptureService(
                new AppDataPaths(root),
                store,
                new FakeTimeProvider(DateTime.Now),
                new FakeActiveWindowService(new ActiveWindowInfo("测试窗口", "test")));

            var evidence = await capture.CaptureFullScreenAsync(settings, isAutoCaptured: false);

            Assert.NotNull(evidence);
            Assert.StartsWith(configuredDirectory, evidence!.ImagePath, StringComparison.OrdinalIgnoreCase);
            Assert.False(evidence.IsAutoCaptured);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Screenshot_change_detector_detects_different_images()
    {
        using var first = new Bitmap(32, 24);
        using var second = new Bitmap(32, 24);
        using (var graphics = Graphics.FromImage(first))
        {
            graphics.Clear(Color.Blue);
        }

        using (var graphics = Graphics.FromImage(second))
        {
            graphics.Clear(Color.Red);
        }

        var detector = new ScreenshotChangeDetector();
        var firstFingerprint = detector.CreateFingerprint(first);
        var secondFingerprint = detector.CreateFingerprint(second);

        Assert.True(detector.HasMeaningfulChange(null, firstFingerprint));
        Assert.False(detector.HasMeaningfulChange(firstFingerprint, firstFingerprint));
        Assert.True(detector.HasMeaningfulChange(firstFingerprint, secondFingerprint));
    }

    [Fact]
    public async Task Auto_screenshot_scheduler_waits_for_interval_after_start()
    {
        var store = new FakeCaptureSettingsStore(new EvidenceCaptureSettings
        {
            AutoScreenshotEnabled = true,
            ScreenshotIntervalMinutes = 1
        });
        var capture = new FakeScreenshotCaptureService();
        var scheduler = new AutoScreenshotScheduler(store, capture, _ => { });

        await scheduler.StartAsync();
        await scheduler.StartAsync();

        Assert.Equal(0, capture.CaptureCount);
        scheduler.Stop();
    }

    [Fact]
    public async Task Evidence_store_keeps_multiple_report_drafts_for_same_day()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new EvidenceStore(new AppDataPaths(root));
            var date = new DateOnly(2026, 6, 26);
            await store.SaveReportDraftAsync(new ReportDraft
            {
                Id = "draft-1",
                Type = ReportTypes.Daily,
                StartDate = date,
                EndDate = date,
                Content = "first"
            });
            await store.SaveReportDraftAsync(new ReportDraft
            {
                Id = "draft-2",
                Type = ReportTypes.Daily,
                StartDate = date,
                EndDate = date,
                Content = "second"
            });

            var drafts = await store.ListReportDraftsAsync(ReportTypes.Daily, date, date);

            Assert.Equal(2, drafts.Count);
            Assert.Contains(drafts, draft => draft.Id == "draft-1");
            Assert.Contains(drafts, draft => draft.Id == "draft-2");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Evidence_store_prunes_expired_daily_and_weekly_drafts_when_saving()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new AppDataPaths(root);
            var now = DateTime.Now;
            var expiredDailyPath = await WriteRawReportDraftAsync(paths, new ReportDraft
            {
                Id = "expired-daily",
                Type = ReportTypes.Daily,
                StartDate = DateOnly.FromDateTime(now.AddDays(-31)),
                EndDate = DateOnly.FromDateTime(now.AddDays(-31)),
                Content = "expired daily",
                CreatedAt = now.AddDays(-31),
                UpdatedAt = now.AddDays(-30).AddMinutes(-1),
                Status = ReportStatuses.Draft
            });
            var expiredWeeklyPath = await WriteRawReportDraftAsync(paths, new ReportDraft
            {
                Id = "expired-weekly",
                Type = ReportTypes.Weekly,
                StartDate = DateOnly.FromDateTime(now.AddDays(-35)),
                EndDate = DateOnly.FromDateTime(now.AddDays(-31)),
                Content = "expired weekly",
                CreatedAt = now.AddDays(-35),
                UpdatedAt = now.AddDays(-30).AddMinutes(-1),
                Status = ReportStatuses.Draft
            });

            var store = new EvidenceStore(paths);
            await store.SaveReportDraftAsync(new ReportDraft
            {
                Id = "current-daily",
                Type = ReportTypes.Daily,
                StartDate = DateOnly.FromDateTime(now),
                EndDate = DateOnly.FromDateTime(now),
                Content = "current"
            });

            Assert.False(File.Exists(expiredDailyPath));
            Assert.False(File.Exists(expiredWeeklyPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Evidence_store_prunes_expired_drafts_when_listing_and_keeps_boundary_and_confirmed_reports()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new AppDataPaths(root);
            var now = DateTime.Now;
            var expiredPath = await WriteRawReportDraftAsync(paths, new ReportDraft
            {
                Id = "expired-on-list",
                Type = ReportTypes.Daily,
                StartDate = DateOnly.FromDateTime(now.AddDays(-31)),
                EndDate = DateOnly.FromDateTime(now.AddDays(-31)),
                Content = "expired",
                CreatedAt = now.AddDays(-31),
                UpdatedAt = now.AddDays(-30).AddMinutes(-1),
                Status = ReportStatuses.Draft
            });
            var boundaryPath = await WriteRawReportDraftAsync(paths, new ReportDraft
            {
                Id = "retain-at-boundary",
                Type = ReportTypes.Daily,
                StartDate = DateOnly.FromDateTime(now.AddDays(-30)),
                EndDate = DateOnly.FromDateTime(now.AddDays(-30)),
                Content = "boundary",
                CreatedAt = now.AddDays(-30),
                UpdatedAt = now.AddDays(-30).AddMinutes(1),
                Status = ReportStatuses.Draft
            });
            var confirmedPath = await WriteRawReportDraftAsync(paths, new ReportDraft
            {
                Id = "confirmed-history",
                Type = ReportTypes.Weekly,
                StartDate = DateOnly.FromDateTime(now.AddDays(-35)),
                EndDate = DateOnly.FromDateTime(now.AddDays(-31)),
                Content = "confirmed",
                CreatedAt = now.AddDays(-35),
                UpdatedAt = now.AddDays(-31),
                Status = ReportStatuses.Confirmed,
                ConfirmedAt = now.AddDays(-31)
            });

            var drafts = await new EvidenceStore(paths).ListReportDraftsAsync();

            Assert.DoesNotContain(drafts, draft => draft.Id == "expired-on-list");
            Assert.Contains(drafts, draft => draft.Id == "retain-at-boundary");
            Assert.Contains(drafts, draft => draft.Id == "confirmed-history");
            Assert.False(File.Exists(expiredPath));
            Assert.True(File.Exists(boundaryPath));
            Assert.True(File.Exists(confirmedPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Evidence_store_deletes_selected_report_draft()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new EvidenceStore(new AppDataPaths(root));
            var date = new DateOnly(2026, 6, 26);
            await store.SaveReportDraftAsync(new ReportDraft
            {
                Id = "delete-draft",
                Type = ReportTypes.Daily,
                StartDate = date,
                EndDate = date,
                Content = "delete"
            });
            await store.SaveReportDraftAsync(new ReportDraft
            {
                Id = "keep-draft",
                Type = ReportTypes.Daily,
                StartDate = date,
                EndDate = date,
                Content = "keep"
            });

            var deleted = await store.DeleteReportDraftAsync("delete-draft");
            var drafts = await store.ListReportDraftsAsync(ReportTypes.Daily, date, date);

            Assert.True(deleted);
            Assert.DoesNotContain(drafts, draft => draft.Id == "delete-draft");
            Assert.Contains(drafts, draft => draft.Id == "keep-draft");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Evidence_store_deletes_multiple_report_drafts_in_one_call()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new EvidenceStore(new AppDataPaths(root));
            var date = new DateOnly(2026, 6, 26);
            await store.SaveReportDraftAsync(new ReportDraft
            {
                Id = "delete-draft-1",
                Type = ReportTypes.Daily,
                StartDate = date,
                EndDate = date,
                Content = "delete 1"
            });
            await store.SaveReportDraftAsync(new ReportDraft
            {
                Id = "delete-draft-2",
                Type = ReportTypes.Daily,
                StartDate = date,
                EndDate = date,
                Content = "delete 2"
            });
            await store.SaveReportDraftAsync(new ReportDraft
            {
                Id = "keep-draft",
                Type = ReportTypes.Daily,
                StartDate = date,
                EndDate = date,
                Content = "keep"
            });

            var deleted = await store.DeleteReportDraftsAsync(["delete-draft-1", "delete-draft-2"]);
            var drafts = await store.ListReportDraftsAsync(ReportTypes.Daily, date, date);

            Assert.Equal(2, deleted);
            Assert.DoesNotContain(drafts, draft => draft.Id == "delete-draft-1");
            Assert.DoesNotContain(drafts, draft => draft.Id == "delete-draft-2");
            Assert.Contains(drafts, draft => draft.Id == "keep-draft");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Evidence_store_lists_legacy_single_report_draft()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new AppDataPaths(root);
            paths.EnsureCreated();
            var date = new DateOnly(2026, 6, 26);
            await File.WriteAllTextAsync(
                paths.ReportPath(ReportTypes.Daily, date),
                JsonSerializer.Serialize(new ReportDraft
                {
                    Id = "legacy",
                    Type = ReportTypes.Daily,
                    StartDate = date,
                    EndDate = date,
                    Content = "legacy"
                }));

            var drafts = await new EvidenceStore(paths).ListReportDraftsAsync(ReportTypes.Daily, date, date);

            var draft = Assert.Single(drafts);
            Assert.Equal("legacy", draft.Id);
            Assert.Equal("legacy", draft.Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Confirmed_report_sets_confirmed_at_when_saved()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new EvidenceStore(new AppDataPaths(root));
            var date = new DateOnly(2026, 6, 26);
            await store.SaveReportDraftAsync(new ReportDraft
            {
                Id = "confirmed",
                Type = ReportTypes.Daily,
                StartDate = date,
                EndDate = date,
                Status = ReportStatuses.Confirmed,
                Content = "done"
            });

            var draft = await store.LoadReportDraftByIdAsync("confirmed");

            Assert.NotNull(draft);
            Assert.Equal(ReportStatuses.Confirmed, draft.Status);
            Assert.NotNull(draft.ConfirmedAt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Composite_collector_refreshes_codex_evidence_with_latest_content()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new EvidenceStore(new AppDataPaths(root));
            var capturedAt = new DateTime(2026, 6, 26, 10, 0, 0);
            await store.UpsertEvidenceAsync(
            [
                new EvidenceItem
                {
                    Id = "codex:session-1",
                    Kind = EvidenceKinds.CodexSession,
                    CapturedAt = capturedAt,
                    Title = "旧标题",
                    Summary = "旧摘要",
                    Included = true
                }
            ]);
            var collector = new CompositeEvidenceCollector(
                [new FakeEvidenceCollector(new EvidenceItem
                {
                    Id = "codex:session-1",
                    Kind = EvidenceKinds.CodexSession,
                    CapturedAt = capturedAt.AddMinutes(5),
                    Title = "新标题",
                    Summary = "新摘要",
                    Included = true
                })],
                store);

            var evidence = await collector.CollectAndStoreAsync(new EvidenceRange(
                new DateOnly(2026, 6, 26),
                new DateOnly(2026, 6, 26)));

            var item = Assert.Single(evidence);
            Assert.Equal("新标题", item.Title);
            Assert.Equal("新摘要", item.Summary);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Composite_collector_preserves_existing_included_choice_when_refreshing_codex()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new EvidenceStore(new AppDataPaths(root));
            var capturedAt = new DateTime(2026, 6, 26, 10, 0, 0);
            await store.UpsertEvidenceAsync(
            [
                new EvidenceItem
                {
                    Id = "codex:session-1",
                    Kind = EvidenceKinds.CodexSession,
                    CapturedAt = capturedAt,
                    Title = "旧标题",
                    Summary = "旧摘要",
                    Included = false
                }
            ]);
            var collector = new CompositeEvidenceCollector(
                [new FakeEvidenceCollector(new EvidenceItem
                {
                    Id = "codex:session-1",
                    Kind = EvidenceKinds.CodexSession,
                    CapturedAt = capturedAt.AddMinutes(5),
                    Title = "新标题",
                    Summary = "新摘要",
                    Included = true
                })],
                store);

            var evidence = await collector.CollectAndStoreAsync(new EvidenceRange(
                new DateOnly(2026, 6, 26),
                new DateOnly(2026, 6, 26)));

            Assert.False(Assert.Single(evidence).Included);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Prompt_builder_requires_workplace_markdown_summary_constraints()
    {
        var prompt = new ReportPromptBuilder().Build(
            ReportTypes.Daily,
            new DateOnly(2026, 6, 26),
            new DateOnly(2026, 6, 26),
            [
                new EvidenceItem
                {
                    Kind = EvidenceKinds.CodexSession,
                    CapturedAt = new DateTime(2026, 6, 26, 9, 0, 0),
                    Title = "维护接口文档",
                    Summary = "更新接口说明并验证示例。"
                }
            ]);

        Assert.Contains("不要编造", prompt);
        Assert.Contains("待确认", prompt);
        Assert.Contains("职场简洁", prompt);
        Assert.Contains("今日工作内容简述", prompt);
        Assert.Contains("不写证据来源", prompt);
        Assert.Contains("多个相近事项合并成一条", prompt);
        Assert.Contains("只写今日做了什么、产出了什么、推进了什么", prompt);
        Assert.Contains("只保留对工作汇报有价值的成果和学习收获", prompt);
        Assert.Contains("过滤工具调用、路径、打包、纯测试通过、命令正常退出、点击按钮", prompt);
        Assert.Contains("不要输出“代码评审、技术风险和下一步计划暂无可确认内容”", prompt);
        Assert.Contains("暂无可确认内容", prompt);
        Assert.Contains("[E1]", prompt);
        Assert.Contains("维护接口文档", prompt);
    }

    [Fact]
    public void Prompt_builder_uses_template_cluster_and_selected_evidence()
    {
        var evidence = new[]
        {
            new EvidenceItem
            {
                Id = "e1",
                Kind = EvidenceKinds.CodexSession,
                CapturedAt = new DateTime(2026, 6, 26, 9, 0, 0),
                Title = "接口联调",
                Summary = "完成支付接口联调。",
                Included = true
            },
            new EvidenceItem
            {
                Id = "e2",
                Kind = EvidenceKinds.CodexSession,
                CapturedAt = new DateTime(2026, 6, 26, 10, 0, 0),
                Title = "不应出现",
                Summary = "未勾选内容。",
                Included = false
            }
        };
        var clusters = new EvidenceClusterer().Cluster(evidence);

        var prompt = new ReportPromptBuilder().Build(
            ReportTypes.Daily,
            new DateOnly(2026, 6, 26),
            new DateOnly(2026, 6, 26),
            evidence.Where(item => item.Included).ToList(),
            new ReportTemplate
            {
                Name = "研发",
                PromptInstructions = "突出研发结果。"
            },
            clusters,
            []);

        Assert.Contains("报告模板：研发", prompt);
        Assert.Contains("突出研发结果", prompt);
        Assert.Contains("接口联调", prompt);
        Assert.DoesNotContain("不应出现", prompt);
    }

    [Fact]
    public void Weekly_prompt_includes_confirmed_daily_drafts()
    {
        var prompt = new ReportPromptBuilder().BuildWeeklyDigestPrompt(
            new DateOnly(2026, 6, 22),
            new DateOnly(2026, 6, 28),
            [],
            null,
            [],
            [
                new ReportDraft
                {
                    Id = "daily-1",
                    Type = ReportTypes.Daily,
                    StartDate = new DateOnly(2026, 6, 22),
                    EndDate = new DateOnly(2026, 6, 22),
                    Status = ReportStatuses.Confirmed,
                    Content = "完成日报引用内容"
                }
            ]);

        Assert.Contains("本周已确认日报引用", prompt);
        Assert.Contains("完成日报引用内容", prompt);
    }

    [Fact]
    public async Task Settings_store_initializes_default_report_templates()
    {
        var root = CreateTempDirectory();
        try
        {
            var templates = await new AppSettingsStore(new AppDataPaths(root)).LoadReportTemplatesAsync();

            Assert.Contains(templates, template => template.Name == "研发");
            Assert.Contains(templates, template => template.Name == "测试");
            Assert.Contains(templates, template => template.Name == "产品");
            Assert.Contains(templates, template => template.Name == "运营");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Report_text_formatter_strips_markdown_marks()
    {
        var text = ReportTextFormatter.ToPlainText("""
            ## 完成事项
            - **完成接口** [E1]
            - [链接](https://example.com)
            """);

        Assert.Contains("完成事项", text);
        Assert.Contains("完成接口 [E1]", text);
        Assert.Contains("链接", text);
        Assert.DoesNotContain("##", text);
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("https://example.com", text);
    }

    [Fact]
    public void Prompt_builder_marks_empty_evidence_as_unconfirmed_content()
    {
        var prompt = new ReportPromptBuilder().Build(
            ReportTypes.Daily,
            new DateOnly(2026, 6, 26),
            new DateOnly(2026, 6, 26),
            []);

        Assert.Contains("无可用证据", prompt);
        Assert.Contains("暂无可确认内容", prompt);
        Assert.Contains("不要编造计划或成果", prompt);
    }

    [Fact]
    public void Weekly_prompt_uses_compact_evidence_without_paths()
    {
        var longSummary = new string('进', 600);
        var prompt = new ReportPromptBuilder().Build(
            ReportTypes.Weekly,
            new DateOnly(2026, 6, 22),
            new DateOnly(2026, 6, 28),
            Enumerable.Range(1, 20)
                .Select(index => new EvidenceItem
                {
                    Kind = EvidenceKinds.CodexSession,
                    CapturedAt = new DateTime(2026, 6, 22, 9, 0, 0).AddHours(index),
                    Title = $"任务 {index}",
                    Summary = longSummary,
                    SourcePath = $"C:\\Users\\15458\\.codex\\sessions\\2026\\06\\22\\session-{index}.jsonl"
                })
                .ToList());

        Assert.Contains("[E1]", prompt);
        Assert.Contains("[E20]", prompt);
        Assert.Contains("职场简洁", prompt);
        Assert.Contains("本周学习/工作总结", prompt);
        Assert.Contains("下周学习/工作计划", prompt);
        Assert.Contains("工作中遇到的问题、所需要的支持、解决问题措施及建议", prompt);
        Assert.DoesNotContain("session-1.jsonl", prompt);
        Assert.True(prompt.Length < 7000);
    }

    [Fact]
    public void Weekly_segment_prompt_preserves_evidence_number_offset()
    {
        var prompt = new ReportPromptBuilder().BuildWeeklySegmentPrompt(
            new DateOnly(2026, 6, 24),
            [
                new EvidenceItem
                {
                    Kind = EvidenceKinds.CodexSession,
                    CapturedAt = new DateTime(2026, 6, 24, 10, 0, 0),
                    Title = "接口文档",
                    Summary = "维护接口文档"
                },
                new EvidenceItem
                {
                    Kind = EvidenceKinds.CodexSession,
                    CapturedAt = new DateTime(2026, 6, 24, 11, 0, 0),
                    Title = "关系图",
                    Summary = "关联关系图"
                }
            ],
            evidenceStartIndex: 8);

        Assert.Contains("[E8]", prompt);
        Assert.Contains("[E9]", prompt);
        Assert.Contains("职场简洁", prompt);
        Assert.Contains("按同类工作合并归纳", prompt);
        Assert.DoesNotContain("[E1]", prompt);
    }

    [Fact]
    public void Weekly_final_prompt_uses_daily_summaries()
    {
        var prompt = new ReportPromptBuilder().BuildWeeklyFromSegmentsPrompt(
            new DateOnly(2026, 6, 22),
            new DateOnly(2026, 6, 28),
            ["【2026-06-22】完成接口维护 [E1]", "【2026-06-23】修复部署问题 [E2]"]);

        Assert.Contains("本周周报", prompt);
        Assert.Contains("职场简洁", prompt);
        Assert.Contains("本周学习/工作总结", prompt);
        Assert.Contains("下周学习/工作计划", prompt);
        Assert.Contains("避免逐日罗列或写成流水账", prompt);
        Assert.Contains("完成接口维护 [E1]", prompt);
        Assert.Contains("修复部署问题 [E2]", prompt);
    }

    [Fact]
    public void Weekly_digest_prompt_is_small_and_keeps_evidence_ids()
    {
        var evidence = Enumerable.Range(1, 40)
            .Select(index => new EvidenceItem
            {
                Kind = EvidenceKinds.CodexSession,
                CapturedAt = new DateTime(2026, 6, 22, 9, 0, 0).AddHours(index),
                Title = $"很长的工作标题 {index} " + new string('题', 120),
                Summary = "很长的证据摘要 " + new string('证', 600),
                Included = true
            })
            .ToList();

        var prompt = new ReportPromptBuilder().BuildWeeklyDigestPrompt(
            new DateOnly(2026, 6, 22),
            new DateOnly(2026, 6, 28),
            evidence);

        Assert.Contains("[E1]", prompt);
        Assert.Contains("[E40]", prompt);
        Assert.Contains("本周学习/工作总结", prompt);
        Assert.Contains("暂无可确认问题", prompt);
        Assert.True(prompt.Length < 7000);
    }

    [Fact]
    public void Responses_payload_includes_images_only_when_vision_enabled()
    {
        var root = CreateTempDirectory();
        try
        {
            var imagePath = Path.Combine(root, "shot.png");
            CreatePng(imagePath);
            var evidence = new[]
            {
                new EvidenceItem
                {
                    Kind = EvidenceKinds.Screenshot,
                    CapturedAt = DateTime.Now,
                    Title = "截图",
                    ImagePath = imagePath,
                    Included = true
                }
            };

            var visionBody = JsonSerializer.Serialize(ResponsesModelClient.BuildResponsesRequestBody(
                Provider("https://example.com", "key", "test"),
                useVision: true,
                "prompt",
                evidence));
            var textOnlyBody = JsonSerializer.Serialize(ResponsesModelClient.BuildResponsesRequestBody(
                Provider("https://example.com", "key", "test"),
                useVision: false,
                "prompt",
                evidence));

            Assert.Contains("input_image", visionBody);
            Assert.Contains("data:image/jpeg;base64,", visionBody);
            Assert.DoesNotContain("input_image", textOnlyBody);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Responses_payload_limits_vision_images_to_recent_two()
    {
        var root = CreateTempDirectory();
        try
        {
            var oldImagePath = Path.Combine(root, "old.png");
            var firstRecentImagePath = Path.Combine(root, "recent-1.png");
            var secondRecentImagePath = Path.Combine(root, "recent-2.png");
            CreatePng(oldImagePath);
            CreatePng(firstRecentImagePath);
            CreatePng(secondRecentImagePath);

            var evidence = new[]
            {
                new EvidenceItem
                {
                    Id = "old",
                    Kind = EvidenceKinds.Screenshot,
                    CapturedAt = new DateTime(2026, 6, 26, 9, 0, 0),
                    Title = "旧截图",
                    Summary = "old marker",
                    ImagePath = oldImagePath,
                    Included = true
                },
                new EvidenceItem
                {
                    Id = "recent-1",
                    Kind = EvidenceKinds.Screenshot,
                    CapturedAt = new DateTime(2026, 6, 26, 10, 0, 0),
                    Title = "近截图 1",
                    ImagePath = firstRecentImagePath,
                    Included = true
                },
                new EvidenceItem
                {
                    Id = "recent-2",
                    Kind = EvidenceKinds.Screenshot,
                    CapturedAt = new DateTime(2026, 6, 26, 11, 0, 0),
                    Title = "近截图 2",
                    ImagePath = secondRecentImagePath,
                    Included = true
                }
            };

            var body = JsonSerializer.Serialize(ResponsesModelClient.BuildResponsesRequestBody(
                Provider("https://example.com", "key", "test"),
                useVision: true,
                "prompt",
                evidence));

            Assert.Equal(2, CountOccurrences(body, "input_image"));
            Assert.Equal(2, CountOccurrences(body, "data:image/jpeg;base64,"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Responses_payload_uses_same_analysis_model_for_text_and_vision_understanding()
    {
        var root = CreateTempDirectory();
        try
        {
            var imagePath = Path.Combine(root, "shot.png");
            CreatePng(imagePath);
            var evidence = new[]
            {
                new EvidenceItem
                {
                    Kind = EvidenceKinds.Screenshot,
                    CapturedAt = new DateTime(2026, 6, 26, 10, 0, 0),
                    Title = "截图",
                    ImagePath = imagePath,
                    Included = true
                }
            };

            var visionBody = JsonSerializer.Serialize(ResponsesModelClient.BuildResponsesRequestBody(
                Provider("https://analysis.example.com", "analysis-key", "analysis-model"),
                useVision: true,
                "prompt",
                evidence));
            var textBody = JsonSerializer.Serialize(ResponsesModelClient.BuildResponsesRequestBody(
                Provider("https://analysis.example.com", "analysis-key", "analysis-model"),
                useVision: false,
                "prompt",
                evidence));

            Assert.Contains("\"model\":\"analysis-model\"", visionBody);
            Assert.Contains("\"model\":\"analysis-model\"", textBody);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Generate_retries_text_only_when_vision_request_gets_bad_gateway()
    {
        var root = CreateTempDirectory();
        try
        {
            var imagePath = Path.Combine(root, "shot.png");
            CreatePng(imagePath);
            var sentBodies = new List<string>();
            var sentUris = new List<string>();
            var sentAuthorization = new List<string?>();
            var client = new ResponsesModelClient(new HttpClient(new FakeHttpHandler(async request =>
            {
                sentUris.Add(request.RequestUri!.ToString());
                sentAuthorization.Add(request.Headers.Authorization?.ToString());
                sentBodies.Add(await request.Content!.ReadAsStringAsync());
                if (sentBodies.Count == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadGateway)
                    {
                        Content = new StringContent("{\"error\":{\"message\":\"Upstream service temporarily unavailable\",\"type\":\"upstream_error\"}}")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"output_text\":\"文本证据日报\"}")
                };
            })));

            var content = await client.GenerateAsync(
                new ModelProviderSettings
                {
                    Analysis = new SingleModelProviderSettings
                    {
                        BaseUrl = "https://analysis.example.com",
                        ApiKey = "analysis-key",
                        Model = "analysis-model"
                    },
                    ImageGeneration = new SingleModelProviderSettings
                    {
                        BaseUrl = "https://image.example.com",
                        ApiKey = "image-key",
                        Model = "image-generation-model"
                    },
                    UseVision = true
                },
                "prompt",
                [
                    new EvidenceItem
                    {
                        Kind = EvidenceKinds.Screenshot,
                        CapturedAt = new DateTime(2026, 6, 26, 10, 0, 0),
                        Title = "截图",
                        ImagePath = imagePath,
                        Included = true
                    }
                ]);

            Assert.Contains("截图多模态分析暂时不可用", content);
            Assert.Contains("文本证据日报", content);
            Assert.DoesNotContain("Bad Gateway", content);
            Assert.DoesNotContain("https://analysis.example.com", content);
            Assert.DoesNotContain("upstream_error", content);
            Assert.Equal("https://analysis.example.com/v1/responses", sentUris[0]);
            Assert.Equal("https://analysis.example.com/v1/responses", sentUris[1]);
            Assert.Equal("Bearer analysis-key", sentAuthorization[0]);
            Assert.Equal("Bearer analysis-key", sentAuthorization[1]);
            Assert.Contains("\"model\":\"analysis-model\"", sentBodies[0]);
            Assert.Contains("\"model\":\"analysis-model\"", sentBodies[1]);
            var visionPrompt = ExtractResponsesPrompt(sentBodies[0]);
            Assert.Contains("工作事实", visionPrompt);
            Assert.Contains("正在处理的任务、可见产出、问题提示、工具或页面状态", visionPrompt);
            Assert.Contains("不要生成日报或周报", visionPrompt);
            Assert.Contains("input_image", sentBodies[0]);
            Assert.DoesNotContain("input_image", sentBodies[1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Chat_completions_payload_uses_jpeg_data_url_for_vision_images()
    {
        var root = CreateTempDirectory();
        try
        {
            var imagePath = Path.Combine(root, "shot.png");
            CreatePng(imagePath);

            var body = JsonSerializer.Serialize(ResponsesModelClient.BuildChatCompletionsRequestBody(
                Provider("https://example.com", "key", "test"),
                useVision: true,
                "prompt",
                [
                    new EvidenceItem
                    {
                        Kind = EvidenceKinds.Screenshot,
                        CapturedAt = new DateTime(2026, 6, 26, 10, 0, 0),
                        Title = "截图",
                        ImagePath = imagePath,
                        Included = true
                    }
                ]));

            Assert.Contains("\"type\":\"image_url\"", body);
            Assert.Contains("data:image/jpeg;base64,", body);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://api.example.com", "https://api.example.com/v1/responses")]
    [InlineData("https://api.example.com/v1/responses", "https://api.example.com/v1/responses")]
    [InlineData("https://api.example.com/responses", "https://api.example.com/responses")]
    public void Responses_uri_preserves_explicit_responses_endpoint(string baseUrl, string expected)
    {
        Assert.Equal(expected, ResponsesModelClient.BuildResponsesUri(baseUrl).ToString());
    }

    [Theory]
    [InlineData("https://api.example.com", "https://api.example.com/v1/chat/completions")]
    [InlineData("https://api.example.com/v1/responses", "https://api.example.com/v1/chat/completions")]
    [InlineData("https://api.example.com/responses", "https://api.example.com/chat/completions")]
    [InlineData("https://api.example.com/v1/chat/completions", "https://api.example.com/v1/chat/completions")]
    public void Chat_completions_uri_is_derived_from_responses_endpoint(string baseUrl, string expected)
    {
        Assert.Equal(expected, ResponsesModelClient.BuildChatCompletionsUri(baseUrl).ToString());
    }

    [Theory]
    [InlineData("https://api.example.com", "https://api.example.com/v1/images/generations")]
    [InlineData("https://api.example.com/v1/images/generations", "https://api.example.com/v1/images/generations")]
    [InlineData("https://api.example.com/images/generations", "https://api.example.com/images/generations")]
    public void Image_generations_uri_preserves_explicit_endpoint(string baseUrl, string expected)
    {
        Assert.Equal(expected, ResponsesModelClient.BuildImageGenerationsUri(baseUrl).ToString());
    }

    [Fact]
    public void Chat_completions_payload_uses_messages_shape()
    {
        var body = JsonSerializer.Serialize(ResponsesModelClient.BuildChatCompletionsRequestBody(
            Provider("https://example.com", "key", "test"),
            useVision: false,
            "prompt",
            []));

        Assert.Contains("\"messages\"", body);
        Assert.Contains("\"role\":\"user\"", body);
        Assert.Contains("\"type\":\"text\"", body);
    }

    [Fact]
    public void Flatten_exception_includes_inner_exception_message()
    {
        var exception = new InvalidOperationException(
            "outer",
            new HttpRequestException("inner network failure"));

        var message = ResponsesModelClient.FlattenException(exception);

        Assert.Contains("outer", message);
        Assert.Contains("inner network failure", message);
    }

    [Fact]
    public async Task Settings_store_round_trips_analysis_and_image_generation_models()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new AppSettingsStore(new AppDataPaths(root));

            await store.SaveModelSettingsAsync(new ModelProviderSettings
            {
                Analysis = new SingleModelProviderSettings
                {
                    BaseUrl = "https://analysis.example.com",
                    ApiKey = "analysis-secret",
                    Model = "analysis-model"
                },
                ImageGeneration = new SingleModelProviderSettings
                {
                    BaseUrl = "https://image.example.com",
                    ApiKey = "image-secret",
                    Model = "image-generation-model"
                },
                UseVision = true,
                QualityMode = ModelQualityMode.HighQuality
            });

            var settings = await store.LoadModelSettingsAsync();

            Assert.Equal("https://analysis.example.com", settings.Analysis.BaseUrl);
            Assert.Equal("analysis-secret", settings.Analysis.ApiKey);
            Assert.Equal("analysis-model", settings.Analysis.Model);
            Assert.Equal("https://image.example.com", settings.ImageGeneration.BaseUrl);
            Assert.Equal("image-secret", settings.ImageGeneration.ApiKey);
            Assert.Equal("image-generation-model", settings.ImageGeneration.Model);
            Assert.True(settings.UseVision);
            Assert.Equal(ModelQualityMode.HighQuality, settings.QualityMode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Settings_store_defaults_quality_mode_to_balanced()
    {
        var root = CreateTempDirectory();
        try
        {
            var settings = await new AppSettingsStore(new AppDataPaths(root)).LoadModelSettingsAsync();

            Assert.Equal(ModelQualityMode.Balanced, settings.QualityMode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Low_cost_mode_does_not_send_vision_images()
    {
        var root = CreateTempDirectory();
        try
        {
            var imagePath = Path.Combine(root, "shot.png");
            CreatePng(imagePath);
            var sentBodies = new List<string>();
            var client = new ResponsesModelClient(new HttpClient(new FakeHttpHandler(async request =>
            {
                sentBodies.Add(await request.Content!.ReadAsStringAsync());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"output_text\":\"低成本报告\"}")
                };
            })));

            var content = await client.GenerateAsync(
                new ModelProviderSettings
                {
                    Analysis = Provider("https://analysis.example.com", "key", "model"),
                    UseVision = true,
                    QualityMode = ModelQualityMode.LowCost
                },
                "prompt",
                [
                    new EvidenceItem
                    {
                        Kind = EvidenceKinds.Screenshot,
                        CapturedAt = DateTime.Now,
                        ImagePath = imagePath,
                        Included = true
                    }
                ]);

            Assert.Equal("低成本报告", content);
            Assert.Single(sentBodies);
            Assert.DoesNotContain("input_image", sentBodies[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Connection_test_uses_chat_completions_by_default()
    {
        string? uri = null;
        var client = new ResponsesModelClient(new HttpClient(new FakeHttpHandler(request =>
        {
            uri = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}")
            });
        })));

        var result = await client.TestConnectionAsync(
            Provider("https://analysis.example.com", "key", "model"),
            ModelConnectionTestKind.Analysis);

        Assert.True(result.Succeeded);
        Assert.False(result.FallbackUsed);
        Assert.Equal("https://analysis.example.com/v1/chat/completions", result.EndpointUsed);
        Assert.Equal("https://analysis.example.com/v1/chat/completions", uri);
    }

    [Fact]
    public async Task Connection_test_uses_responses_fallback_when_chat_is_not_supported()
    {
        var uris = new List<string>();
        var client = new ResponsesModelClient(new HttpClient(new FakeHttpHandler(request =>
        {
            uris.Add(request.RequestUri!.ToString());
            if (uris.Count == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{\"error\":\"chat completions not found\"}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"output_text\":\"ok\"}")
            });
        })));

        var result = await client.TestConnectionAsync(
            Provider("https://analysis.example.com", "key", "model"),
            ModelConnectionTestKind.Analysis);

        Assert.True(result.Succeeded);
        Assert.True(result.FallbackUsed);
        Assert.Equal("https://analysis.example.com/v1/responses", result.EndpointUsed);
        Assert.Equal(
            ["https://analysis.example.com/v1/chat/completions", "https://analysis.example.com/v1/responses"],
            uris);
    }

    [Fact]
    public async Task Text_generation_uses_chat_fallback_when_responses_proxy_returns_server_error()
    {
        var uris = new List<string>();
        var client = new ResponsesModelClient(new HttpClient(new FakeHttpHandler(request =>
        {
            uris.Add(request.RequestUri!.ToString());
            if (uris.Count == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("{\"error\":\"responses upstream failed\"}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}")
            });
        })));

        var result = await client.GenerateAsync(
            new ModelProviderSettings
            {
                Analysis = Provider("https://analysis.example.com", "key", "model"),
                QualityMode = ModelQualityMode.LowCost
            },
            "prompt",
            []);

        Assert.Equal("ok", result);
        Assert.Equal(
            ["https://analysis.example.com/v1/responses", "https://analysis.example.com/v1/chat/completions"],
            uris);
    }

    [Fact]
    public async Task Image_generation_connection_test_uses_images_endpoint()
    {
        string? requestBody = null;
        var client = new ResponsesModelClient(new HttpClient(new FakeHttpHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            Assert.Equal("https://image.example.com/v1/images/generations", request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}")
            };
        })));

        var result = await client.TestConnectionAsync(
            Provider("https://image.example.com", "key", "gpt-image-2"),
            ModelConnectionTestKind.ImageGeneration);

        Assert.True(result.Succeeded);
        Assert.False(result.FallbackUsed);
        Assert.Equal("https://image.example.com/v1/images/generations", result.EndpointUsed);
        Assert.Contains("\"model\":\"gpt-image-2\"", requestBody);
        Assert.Contains("\"prompt\":\"connection test\"", requestBody);
    }

    [Fact]
    public async Task Connection_test_returns_friendly_auth_error()
    {
        var client = new ResponsesModelClient(new HttpClient(new FakeHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"bad key\"}")
            }))));

        var result = await client.TestConnectionAsync(
            Provider("https://analysis.example.com", "bad", "model"),
            ModelConnectionTestKind.Analysis);

        Assert.False(result.Succeeded);
        Assert.Contains("API Key", result.Message);
    }

    [Fact]
    public async Task Report_generation_polishes_high_quality_draft()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new EvidenceStore(new AppDataPaths(root));
            var client = new FakeModelClient
            {
                GenerateResult = "初稿",
                PolishResult = "润色稿"
            };
            var service = new ReportGenerationService(
                new CompositeEvidenceCollector([new FakeEvidenceCollector()], store),
                store,
                new FakeSettingsStore(new ModelProviderSettings
                {
                    Analysis = Provider("https://analysis.example.com", "key", "model"),
                    QualityMode = ModelQualityMode.HighQuality
                }),
                client,
                new ReportPromptBuilder());

            var result = await service.GenerateFromEvidenceAsync(
                ReportTypes.Daily,
                new DateOnly(2026, 6, 26),
                new DateOnly(2026, 6, 26),
                [
                    new EvidenceItem
                    {
                        Id = "e1",
                        Kind = EvidenceKinds.CodexSession,
                        CapturedAt = new DateTime(2026, 6, 26, 9, 0, 0),
                        Title = "任务",
                        Summary = "完成任务",
                        Included = true
                    }
                ],
                null);

            Assert.Contains("今日工作内容简述", result.Draft.Content);
            Assert.Contains("润色稿", result.Draft.Content);
            Assert.Contains("润色稿", result.Draft.DailySummary);
            Assert.True(client.PolishCalled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Settings_store_migrates_legacy_single_provider_to_analysis_provider_only()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new AppDataPaths(root);
            paths.EnsureCreated();
            await File.WriteAllTextAsync(
                paths.SettingsPath,
                JsonSerializer.Serialize(new
                {
                    ModelProvider = new
                    {
                        BaseUrl = "https://legacy.example.com",
                        ProtectedApiKey = ProtectForTest("legacy-secret"),
                        Model = "legacy-model",
                        UseVision = true
                    }
                }));

            var settings = await new AppSettingsStore(paths).LoadModelSettingsAsync();

            Assert.Equal("https://legacy.example.com", settings.Analysis.BaseUrl);
            Assert.Equal("legacy-secret", settings.Analysis.ApiKey);
            Assert.Equal("legacy-model", settings.Analysis.Model);
            Assert.Equal("", settings.ImageGeneration.BaseUrl);
            Assert.Equal("", settings.ImageGeneration.ApiKey);
            Assert.Equal("", settings.ImageGeneration.Model);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Weekly_report_section_parser_maps_template_sections()
    {
        var sections = WeeklyReportSectionParser.Parse("""
            ## 本周学习/工作总结
            1、完成提醒模块优化
            2、验证自动截图逻辑

            ## 下周学习/工作计划
            1、完善报告导出体验

            ## 工作中遇到的问题、所需要的支持、解决问题措施及建议
            1、接口配置缺少示例，建议补充连通性提示
            """);

        Assert.Contains("1、完成提醒模块优化", sections.WorkSummary);
        Assert.Contains("2、验证自动截图逻辑", sections.WorkSummary);
        Assert.Contains("1、完善报告导出体验", sections.NextWeekPlan);
        Assert.Contains("1、接口配置缺少示例", sections.Problems);
        Assert.DoesNotContain("接口配置缺少示例", sections.SupportAndSuggestions);
        Assert.Contains("1、接口配置缺少示例", sections.ProblemsAndSupport);
    }

    [Fact]
    public void Weekly_report_section_parser_maps_structured_problem_and_support_sections()
    {
        var sections = WeeklyReportSectionParser.Parse("""
            ## 本周学习/工作总结
            1、完成结构化编辑

            ## 存在问题
            1、证据需要人工筛选

            ## 下周学习/工作计划
            1、完善补全模板

            ## 所需支持/建议
            1、需要确认模板字段
            """);

        Assert.Contains("完成结构化编辑", sections.WorkSummary);
        Assert.Contains("证据需要人工筛选", sections.Problems);
        Assert.Contains("完善补全模板", sections.NextWeekPlan);
        Assert.Contains("需要确认模板字段", sections.SupportAndSuggestions);
        Assert.Contains("需要确认模板字段", sections.ProblemsAndSupport);
    }

    [Fact]
    public void Daily_report_formatter_removes_low_value_tail_and_process_only_items()
    {
        var markdown = WeeklyReportSectionParser.BuildDailyMarkdown("""
            1、完成 playwright-cli 技能安装与本机 Codex 技能区配置，产出可用于浏览器自动化、网页测试和 Playwright 测试辅助的工具能力。
            2、推进 LangChain 学习导图生成与配置调整，完成高清可读版本输出，包含 4K PNG 与 SVG 矢量版，便于放大查看和打印。
            3、实现 Embedding 与文档切分调试台练习入口 python -m src.embeddinglab，用于观察 RAG 中 chunk 切分、embedding 检索结果及 /sources 逻辑。
            4、完成相关验证工作，确认调试台验证通过且退出命令正常。
            5、代码评审、技术风险和下一步研发计划暂无可确认内容。
            6、打包 exe 并生成 zip 测试包。
            """);

        Assert.Contains("## 今日工作内容简述", markdown);
        Assert.Contains("LangChain 学习导图", markdown);
        Assert.Contains("Embedding 与文档切分调试台", markdown);
        Assert.Contains("playwright-cli 技能安装", markdown);
        Assert.DoesNotContain("代码评审、技术风险", markdown);
        Assert.DoesNotContain("下一步研发计划暂无可确认内容", markdown);
        Assert.DoesNotContain("退出命令正常", markdown);
        Assert.DoesNotContain("打包 exe", markdown);
        Assert.DoesNotContain("生成 zip", markdown);
        Assert.True(markdown.Split('\n').Count(line => line.TrimStart().StartsWith("1、", StringComparison.Ordinal)
            || line.TrimStart().StartsWith("2、", StringComparison.Ordinal)
            || line.TrimStart().StartsWith("3、", StringComparison.Ordinal)
            || line.TrimStart().StartsWith("4、", StringComparison.Ordinal)
            || line.TrimStart().StartsWith("5、", StringComparison.Ordinal)) <= 5);
    }

    [Fact]
    public void Internship_week_calculator_uses_2026_06_22_as_first_work_week()
    {
        var firstWeek = InternshipWeekCalculator.GetWeek(new DateOnly(2026, 6, 26));
        var secondWeek = InternshipWeekCalculator.GetWeek(new DateOnly(2026, 6, 29));
        var thirdWeek = InternshipWeekCalculator.GetWeek(new DateOnly(2026, 7, 6));
        var tenthWeek = InternshipWeekCalculator.GetWeek(new DateOnly(2026, 8, 24));
        var sundayStillFirstWeek = InternshipWeekCalculator.GetWeek(new DateOnly(2026, 6, 28));

        Assert.Equal(1, firstWeek.WeekNumber);
        Assert.Equal(new DateOnly(2026, 6, 22), firstWeek.StartDate);
        Assert.Equal(new DateOnly(2026, 6, 26), firstWeek.EndDate);
        Assert.Equal(2, secondWeek.WeekNumber);
        Assert.Equal(new DateOnly(2026, 6, 29), secondWeek.StartDate);
        Assert.Equal(new DateOnly(2026, 7, 3), secondWeek.EndDate);
        Assert.Equal("第三周", thirdWeek.ChineseWeekLabel);
        Assert.Equal("第十周", tenthWeek.ChineseWeekLabel);
        Assert.Equal(1, sundayStillFirstWeek.WeekNumber);
    }

    [Fact]
    public void Enterprise_weekly_report_excel_exporter_fills_template_cells()
    {
        var root = CreateTempDirectory();
        try
        {
            var draft = new ReportDraft
            {
                Type = ReportTypes.Weekly,
                StartDate = new DateOnly(2026, 8, 3),
                EndDate = new DateOnly(2026, 8, 7),
                WorkSummary = "1、完成第七周报告模板适配",
                Problems = "1、暂无第七周可确认问题",
                NextWeekPlan = "1、继续完善第七周周报导出",
                SupportAndSuggestions = "1、暂无第七周需额外支持"
            };

            var path = new WeeklyReportExcelExporter(workbookStateRoot: Path.Combine(root, "state"))
                .ExportEnterprise(draft, root);

            Assert.True(File.Exists(path));
            Assert.Equal("enterprise-weekly-report.xlsx", Path.GetFileName(path));
            Assert.Equal("第七周", ReadSharedStringCell(path, "A8"));
            Assert.Equal("8月3日-8月7日", ReadSharedStringCell(path, "B8"));
            Assert.Equal(new DateTime(2026, 6, 22).ToOADate(), ReadNumericCell(path, "D2"));
            Assert.Equal("示例工作内容（请替换）", ReadSharedStringCell(path, "I2"));
            Assert.Contains("完成第七周报告模板适配", ReadSharedStringCell(path, "I8"));
            Assert.Contains("继续完善第七周周报导出", ReadSharedStringCell(path, "J8"));
            Assert.Contains("暂无第七周可确认问题", ReadSharedStringCell(path, "K8"));
            Assert.Contains("暂无第七周需额外支持", ReadSharedStringCell(path, "K8"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Enterprise_weekly_report_excel_exporter_extends_uploaded_template_after_reserved_rows()
    {
        var root = CreateTempDirectory();
        try
        {
            var draft = new ReportDraft
            {
                Type = ReportTypes.Weekly,
                StartDate = new DateOnly(2026, 9, 14),
                EndDate = new DateOnly(2026, 9, 18),
                WorkSummary = "1、第十三周新增周报内容",
                Problems = "1、第十三周暂无可确认问题",
                NextWeekPlan = "1、继续推进后续工作",
                SupportAndSuggestions = "1、暂无额外支持"
            };

            var path = new WeeklyReportExcelExporter(workbookStateRoot: Path.Combine(root, "state"))
                .ExportEnterprise(draft, root);

            Assert.Equal("第十三周", ReadSharedStringCell(path, "A14"));
            Assert.Equal("9月14日-9月18日", ReadSharedStringCell(path, "B14"));
            Assert.Contains("第十三周新增周报内容", ReadSharedStringCell(path, "I14"));
            Assert.Equal("示例工作内容（请替换）", ReadSharedStringCell(path, "I2"));
            Assert.Equal(string.Empty, ReadSharedStringCell(path, "I7"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Enterprise_weekly_report_excel_exporter_accumulates_into_template_with_fixed_name()
    {
        var root = CreateTempDirectory();
        try
        {
            var templateRoot = Path.Combine(root, "templates");
            var templateDirectory = Path.Combine(templateRoot, "Templates");
            Directory.CreateDirectory(templateDirectory);
            File.Copy(
                Path.Combine(Directory.GetCurrentDirectory(), "Templates", "enterprise-weekly-report-template.xlsx"),
                Path.Combine(templateDirectory, "enterprise-weekly-report-template.xlsx"));

            var exporter = new WeeklyReportExcelExporter(templateRoot, Path.Combine(root, "state"));
            var first = new ReportDraft
            {
                Type = ReportTypes.Weekly,
                StartDate = new DateOnly(2026, 6, 22),
                EndDate = new DateOnly(2026, 6, 26),
                WorkSummary = "企业第一周完成内容",
                Problems = "企业第一周问题",
                NextWeekPlan = "企业第一周计划",
                SupportAndSuggestions = "企业第一周支持"
            };
            var second = new ReportDraft
            {
                Type = ReportTypes.Weekly,
                StartDate = new DateOnly(2026, 6, 29),
                EndDate = new DateOnly(2026, 7, 3),
                WorkSummary = "企业第二周完成内容",
                Problems = "企业第二周问题",
                NextWeekPlan = "企业第二周计划",
                SupportAndSuggestions = "企业第二周支持"
            };

            var firstPath = exporter.ExportEnterprise(first, root);
            var secondPath = exporter.ExportEnterprise(second, root);
            var updatedTemplatePath = Path.Combine(templateDirectory, "enterprise-weekly-report-template.xlsx");

            Assert.Equal("enterprise-weekly-report.xlsx", Path.GetFileName(firstPath));
            Assert.Equal(firstPath, secondPath);
            Assert.Contains("企业第一周完成内容", ReadSharedStringCell(secondPath, "I2"));
            Assert.Contains("企业第二周完成内容", ReadSharedStringCell(secondPath, "I3"));
            Assert.DoesNotContain("企业第二周计划", ReadSharedStringCell(updatedTemplatePath, "J3"));
            Assert.DoesNotContain("企业第二周问题", ReadSharedStringCell(updatedTemplatePath, "K3"));
            Assert.DoesNotContain("企业第二周支持", ReadSharedStringCell(updatedTemplatePath, "K3"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Enterprise_weekly_report_excel_exporter_rebuilds_missing_week_from_report_history()
    {
        var root = CreateTempDirectory();
        try
        {
            var exporter = new WeeklyReportExcelExporter(workbookStateRoot: Path.Combine(root, "state"));
            var seventh = new ReportDraft
            {
                Type = ReportTypes.Weekly,
                StartDate = new DateOnly(2026, 8, 3),
                EndDate = new DateOnly(2026, 8, 7),
                WorkSummary = "企业第七周历史内容",
                Problems = "企业第七周历史问题",
                NextWeekPlan = "企业第七周历史计划"
            };
            var eighth = new ReportDraft
            {
                Type = ReportTypes.Weekly,
                StartDate = new DateOnly(2026, 8, 10),
                EndDate = new DateOnly(2026, 8, 14),
                WorkSummary = "企业第八周当前内容",
                Problems = "企业第八周当前问题",
                NextWeekPlan = "企业第八周当前计划"
            };
            exporter.ExportEnterprise(eighth, root);

            var path = exporter.ExportEnterprise(eighth, root, [seventh, eighth]);

            Assert.Equal("第七周", ReadSharedStringCell(path, "A8"));
            Assert.Equal("第八周", ReadSharedStringCell(path, "A9"));
            Assert.Contains("企业第七周历史内容", ReadSharedStringCell(path, "I8"));
            Assert.Contains("企业第八周当前内容", ReadSharedStringCell(path, "I9"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void School_weekly_report_excel_exporter_fills_matching_week_row()
    {
        var root = CreateTempDirectory();
        try
        {
            var draft = new ReportDraft
            {
                Type = ReportTypes.Weekly,
                StartDate = new DateOnly(2026, 6, 29),
                EndDate = new DateOnly(2026, 7, 3),
                WorkSummary = "1、完成学校模板适配",
                Problems = "1、暂无可确认问题",
                NextWeekPlan = "1、继续验证导出内容",
                SupportAndSuggestions = "1、暂无需额外支持"
            };

            var path = new WeeklyReportExcelExporter(workbookStateRoot: Path.Combine(root, "state"))
                .ExportSchool(draft, root);

            Assert.True(File.Exists(path));
            Assert.Equal("school-weekly-report-第2周.xlsx", Path.GetFileName(path));
            Assert.Equal("第2周", ReadSharedStringCell(path, "A10"));
            Assert.Equal("6月29日-7月3日", ReadSharedStringCell(path, "B10"));
            Assert.Contains("完成学校模板适配", ReadSharedStringCell(path, "C10"));
            Assert.Contains("暂无可确认问题", ReadSharedStringCell(path, "E10"));
            Assert.Contains("继续验证导出内容", ReadSharedStringCell(path, "F10"));
            Assert.Equal("示例姓名", ReadSharedStringCell(path, "B3"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void School_weekly_report_excel_exporter_accumulates_into_template()
    {
        var root = CreateTempDirectory();
        try
        {
            var templateRoot = Path.Combine(root, "templates");
            var templateDirectory = Path.Combine(templateRoot, "Templates");
            Directory.CreateDirectory(templateDirectory);
            File.Copy(
                Path.Combine(Directory.GetCurrentDirectory(), "Templates", "school-weekly-report-template.xlsx"),
                Path.Combine(templateDirectory, "school-weekly-report-template.xlsx"));

            var exporter = new WeeklyReportExcelExporter(templateRoot, Path.Combine(root, "state"));
            var first = new ReportDraft
            {
                Type = ReportTypes.Weekly,
                StartDate = new DateOnly(2026, 6, 22),
                EndDate = new DateOnly(2026, 6, 26),
                WorkSummary = "第一周完成内容",
                Problems = "第一周问题",
                NextWeekPlan = "第一周计划"
            };
            var second = new ReportDraft
            {
                Type = ReportTypes.Weekly,
                StartDate = new DateOnly(2026, 6, 29),
                EndDate = new DateOnly(2026, 7, 3),
                WorkSummary = "第二周完成内容",
                Problems = "第二周问题",
                NextWeekPlan = "第二周计划"
            };

            exporter.ExportSchool(first, root);
            var secondPath = exporter.ExportSchool(second, root);
            var updatedTemplatePath = Path.Combine(templateDirectory, "school-weekly-report-template.xlsx");

            Assert.Equal("school-weekly-report-第2周.xlsx", Path.GetFileName(secondPath));
            Assert.Contains("第一周完成内容", ReadSharedStringCell(secondPath, "C9"));
            Assert.Contains("第二周完成内容", ReadSharedStringCell(secondPath, "C10"));
            Assert.DoesNotContain("第二周完成内容", ReadSharedStringCell(updatedTemplatePath, "C10"));
            AssertWorkbookUsesFont(secondPath, "微软雅黑");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void School_weekly_report_excel_exporter_rebuilds_missing_week_from_report_history()
    {
        var root = CreateTempDirectory();
        try
        {
            var exporter = new WeeklyReportExcelExporter(workbookStateRoot: Path.Combine(root, "state"));
            var second = new ReportDraft
            {
                Type = ReportTypes.Weekly,
                StartDate = new DateOnly(2026, 6, 29),
                EndDate = new DateOnly(2026, 7, 3),
                WorkSummary = "学校第二周历史内容",
                Problems = "学校第二周历史问题",
                NextWeekPlan = "学校第二周历史计划"
            };
            var third = new ReportDraft
            {
                Type = ReportTypes.Weekly,
                StartDate = new DateOnly(2026, 7, 6),
                EndDate = new DateOnly(2026, 7, 10),
                WorkSummary = "学校第三周当前内容",
                Problems = "学校第三周当前问题",
                NextWeekPlan = "学校第三周当前计划"
            };
            exporter.ExportSchool(third, root);

            var path = exporter.ExportSchool(third, root, [second, third]);

            Assert.Contains("学校第二周历史内容", ReadSharedStringCell(path, "C10"));
            Assert.Contains("学校第三周当前内容", ReadSharedStringCell(path, "C11"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private const string TestTraeCnDbKey = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
    private static int TestSqlCipherProviderInitialized;

    private static void CreateTraeCnAiAgentDatabase(
        string root,
        DateOnly date,
        string sessionId,
        string title,
        string userMessage,
        string assistantMessage)
    {
        Directory.CreateDirectory(root);
        EnsureTestSqlCipherProviderInitialized();

        var databasePath = Path.Combine(root, "database.db");
        sqlite3? database = null;
        try
        {
            CheckSqlite(raw.sqlite3_open_v2(
                databasePath,
                out database,
                raw.SQLITE_OPEN_READWRITE | raw.SQLITE_OPEN_CREATE | raw.SQLITE_OPEN_URI,
                null), database);
            ExecSqlite(database, $"PRAGMA key = \"x'{TestTraeCnDbKey}'\";");
            ExecSqlite(database, """
                CREATE TABLE chat_session (
                    id INTEGER PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    project_id TEXT NOT NULL,
                    created_at bigint NOT NULL,
                    updated_at bigint NOT NULL,
                    deleted_at bigint NOT NULL DEFAULT 0,
                    session_title TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE project (
                    id INTEGER PRIMARY KEY,
                    project_id TEXT NOT NULL,
                    absolute_path TEXT NULL,
                    deleted_at bigint NULL
                );
                CREATE TABLE chat_message (
                    id INTEGER PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    message_id TEXT NOT NULL,
                    message_type TEXT NOT NULL,
                    message_role TEXT NOT NULL,
                    message_index INTEGER NOT NULL,
                    created_at bigint NOT NULL,
                    updated_at bigint NOT NULL,
                    deleted_at bigint NOT NULL DEFAULT 0
                );
                CREATE TABLE chat_message_general (
                    id INTEGER PRIMARY KEY,
                    message_id TEXT NOT NULL,
                    content TEXT NOT NULL,
                    deleted_at bigint NOT NULL DEFAULT 0
                );
                CREATE TABLE chat_message_task (
                    id INTEGER PRIMARY KEY,
                    message_id TEXT NOT NULL,
                    content TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    deleted_at bigint NOT NULL DEFAULT 0
                );
                """);

            var createdAt = ToUnixSeconds(date.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Local));
            var updatedAt = ToUnixSeconds(date.ToDateTime(new TimeOnly(10, 20), DateTimeKind.Local));
            var projectId = "project-1";
            var userMessageId = $"{sessionId}-user";
            var assistantMessageId = $"{sessionId}-assistant";
            var userContent = JsonSerializer.Serialize(new[]
            {
                new { type = "text", text_content = userMessage }
            });
            var taskContent = JsonSerializer.Serialize(new
            {
                messages = new[]
                {
                    new
                    {
                        type = "plan_item",
                        plan_item = new
                        {
                            id = "plan-item-1",
                            thought = assistantMessage,
                            reasoning_content = "新增 Trae CN 原生采集器并验证日报证据。"
                        }
                    }
                }
            });

            ExecSqlite(database, $"""
                INSERT INTO project (project_id, absolute_path, deleted_at)
                VALUES ({Sql(projectId)}, {Sql("D:\\Code\\项目\\sdd测试")}, 0);
                INSERT INTO chat_session (session_id, project_id, created_at, updated_at, deleted_at, session_title)
                VALUES ({Sql(sessionId)}, {Sql(projectId)}, {createdAt}, {updatedAt}, 0, {Sql(title)});
                INSERT INTO chat_message (session_id, message_id, message_type, message_role, message_index, created_at, updated_at, deleted_at)
                VALUES ({Sql(sessionId)}, {Sql(userMessageId)}, 'general', 'user', 1, {createdAt}, {createdAt}, 0);
                INSERT INTO chat_message_general (message_id, content, deleted_at)
                VALUES ({Sql(userMessageId)}, {Sql(userContent)}, 0);
                INSERT INTO chat_message (session_id, message_id, message_type, message_role, message_index, created_at, updated_at, deleted_at)
                VALUES ({Sql(sessionId)}, {Sql(assistantMessageId)}, 'task', 'assistant', 2, {updatedAt}, {updatedAt}, 0);
                INSERT INTO chat_message_task (message_id, content, summary, deleted_at)
                VALUES ({Sql(assistantMessageId)}, {Sql(taskContent)}, '', 0);
                """);
        }
        finally
        {
            if (database is not null)
            {
                raw.sqlite3_close(database);
            }
        }
    }

    private static void EnsureTestSqlCipherProviderInitialized()
    {
        if (Interlocked.Exchange(ref TestSqlCipherProviderInitialized, 1) == 0)
        {
            try
            {
                raw.SetProvider(new SQLite3Provider_e_sqlcipher());
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static void ExecSqlite(sqlite3 database, string sql)
    {
        CheckSqlite(raw.sqlite3_exec(database, sql), database);
    }

    private static void CheckSqlite(int rc, sqlite3? database)
    {
        if (rc == raw.SQLITE_OK || rc == raw.SQLITE_ROW || rc == raw.SQLITE_DONE)
        {
            return;
        }

        var message = database is null
            ? raw.sqlite3_errstr(rc).utf8_to_string()
            : raw.sqlite3_errmsg(database).utf8_to_string();
        throw new InvalidOperationException($"SQLite rc={rc}: {message}");
    }

    private static string Sql(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static long ToUnixSeconds(DateTime value)
    {
        return new DateTimeOffset(value).ToUnixTimeSeconds();
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Timenotifier.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<string> WriteRawReportDraftAsync(AppDataPaths paths, ReportDraft draft)
    {
        var typeDirectory = draft.Type == ReportTypes.Weekly ? "weekly" : "daily";
        var directory = Path.Combine(paths.ReportsDirectory, typeDirectory, $"{draft.StartDate:yyyy-MM-dd}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{draft.Id}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(draft));
        return path;
    }

    private static async Task WriteSessionAsync(
        string root,
        DateOnly date,
        string sessionId,
        string title,
        string userMessage,
        string assistantMessage)
    {
        var directory = Path.Combine(
            root,
            "sessions",
            date.Year.ToString("0000"),
            date.Month.ToString("00"),
            date.Day.ToString("00"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(root, "session_index.jsonl"),
            JsonSerializer.Serialize(new { id = sessionId, thread_name = title }) + Environment.NewLine);
        await File.WriteAllLinesAsync(
            Path.Combine(directory, $"{sessionId}.jsonl"),
            [
                JsonSerializer.Serialize(new
                {
                    timestamp = $"{date:yyyy-MM-dd}T01:00:00Z",
                    type = "session_meta",
                    payload = new { session_id = sessionId, cwd = "D:\\Code\\项目\\Timenotifier" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = $"{date:yyyy-MM-dd}T01:01:00Z",
                    type = "event_msg",
                    payload = new { type = "user_message", message = userMessage }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = $"{date:yyyy-MM-dd}T01:02:00Z",
                    type = "event_msg",
                    payload = new { type = "agent_message", message = assistantMessage }
                })
            ]);
    }

    private static string ReadSharedStringCell(string workbookPath, string cellReference)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using var archive = ZipFile.OpenRead(workbookPath);
        using var sheetStream = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        using var stringsStream = archive.GetEntry("xl/sharedStrings.xml")!.Open();
        var sheet = XDocument.Load(sheetStream);
        var strings = XDocument.Load(stringsStream);
        var cell = sheet
            .Descendants(ns + "c")
            .First(element => string.Equals((string?)element.Attribute("r"), cellReference, StringComparison.OrdinalIgnoreCase));
        var value = cell.Element(ns + "v")?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var index = int.Parse(value);
        return strings.Root!.Elements(ns + "si").ElementAt(index).Element(ns + "t")!.Value;
    }

    private static double ReadNumericCell(string workbookPath, string cellReference)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using var archive = ZipFile.OpenRead(workbookPath);
        using var sheetStream = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var sheet = XDocument.Load(sheetStream);
        var cell = sheet
            .Descendants(ns + "c")
            .First(element => string.Equals((string?)element.Attribute("r"), cellReference, StringComparison.OrdinalIgnoreCase));
        return double.Parse(cell.Element(ns + "v")!.Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AssertWorkbookUsesFont(string workbookPath, string expectedFont)
    {
        using var archive = ZipFile.OpenRead(workbookPath);
        foreach (var entryName in new[] { "xl/styles.xml", "xl/sharedStrings.xml" })
        {
            var entry = archive.GetEntry(entryName);
            if (entry is null)
            {
                continue;
            }

            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var fontNames = Regex.Matches(text, "<(?:name|rFont) val=\"([^\"]+)\"")
                .Select(match => match.Groups[1].Value)
                .Distinct()
                .ToList();
            Assert.All(fontNames, font => Assert.Equal(expectedFont, font));
        }
    }

    private static SingleModelProviderSettings Provider(string baseUrl, string apiKey, string model)
    {
        return new SingleModelProviderSettings
        {
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            Model = model
        };
    }

    private static string ProtectForTest(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = System.Security.Cryptography.ProtectedData.Protect(
            bytes,
            Encoding.UTF8.GetBytes("Timenotifier.ModelProviderSettings.v1"),
            System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static void CreatePng(string path)
    {
        using var bitmap = new Bitmap(32, 24);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(31, 112, 193));
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    private static int CountOccurrences(string value, string marker)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        return count;
    }

    private static string ExtractResponsesPrompt(string body)
    {
        using var document = JsonDocument.Parse(body);
        var content = document.RootElement
            .GetProperty("input")[0]
            .GetProperty("content");

        foreach (var item in content.EnumerateArray())
        {
            if (item.GetProperty("type").GetString() == "input_text")
            {
                return item.GetProperty("text").GetString() ?? "";
            }
        }

        return "";
    }

    private sealed class FakeEvidenceCollector(params EvidenceItem[] evidence) : IEvidenceCollector
    {
        public string Name => "Fake";

        public Task<IReadOnlyList<EvidenceItem>> CollectAsync(
            EvidenceRange range,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<EvidenceItem>>(evidence);
        }
    }

    private static EvidenceItem AutoScreenshot(string id, DateTime capturedAt, string imagePath)
    {
        return new EvidenceItem
        {
            Id = id,
            Kind = EvidenceKinds.Screenshot,
            CapturedAt = capturedAt,
            ImagePath = imagePath,
            IsAutoCaptured = true
        };
    }

    private sealed class FakeTimeProvider(DateTime now) : ITimeProvider
    {
        public DateTime Now { get; set; } = now;
    }

    private sealed class FakeActiveWindowService(ActiveWindowInfo info) : IActiveWindowService
    {
        public ActiveWindowInfo GetActiveWindow()
        {
            return info;
        }
    }

    private sealed class FakeCaptureSettingsStore(EvidenceCaptureSettings captureSettings) : IAppSettingsStore
    {
        public Task<ModelProviderSettings> LoadModelSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelProviderSettings());
        }

        public Task SaveModelSettingsAsync(ModelProviderSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<ReminderSettings> LoadReminderSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(DefaultReminders.CreateSettings());
        }

        public Task SaveReminderSettingsAsync(ReminderSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ReportTemplate>> LoadReportTemplatesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ReportTemplate>>(AppSettingsStore.CreateDefaultReportTemplates());
        }

        public Task SaveReportTemplatesAsync(IReadOnlyList<ReportTemplate> templates, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<EvidenceCaptureSettings> LoadEvidenceCaptureSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(captureSettings);
        }

        public Task SaveEvidenceCaptureSettingsAsync(EvidenceCaptureSettings settings, CancellationToken cancellationToken = default)
        {
            captureSettings = settings;
            return Task.CompletedTask;
        }

        public Task<DailyReportAutomationSettings> LoadDailyReportAutomationSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DailyReportAutomationSettings());
        }

        public Task SaveDailyReportAutomationSettingsAsync(DailyReportAutomationSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeScreenshotCaptureService : IScreenshotCaptureService
    {
        public int CaptureCount { get; private set; }

        public Task<EvidenceItem> CaptureFullScreenAsync(CancellationToken cancellationToken = default)
        {
            CaptureCount++;
            return Task.FromResult(new EvidenceItem());
        }

        public Task<EvidenceItem?> CaptureFullScreenAsync(
            EvidenceCaptureSettings settings,
            bool isAutoCaptured,
            CancellationToken cancellationToken = default)
        {
            CaptureCount++;
            return Task.FromResult<EvidenceItem?>(new EvidenceItem());
        }
    }

    private sealed class FakeHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return sendAsync(request);
        }
    }

    private sealed class FakeSettingsStore(ModelProviderSettings modelSettings) : IAppSettingsStore
    {
        public Task<ModelProviderSettings> LoadModelSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(modelSettings);
        }

        public Task SaveModelSettingsAsync(ModelProviderSettings settings, CancellationToken cancellationToken = default)
        {
            modelSettings = settings;
            return Task.CompletedTask;
        }

        public Task<ReminderSettings> LoadReminderSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(DefaultReminders.CreateSettings());
        }

        public Task SaveReminderSettingsAsync(ReminderSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ReportTemplate>> LoadReportTemplatesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ReportTemplate>>(AppSettingsStore.CreateDefaultReportTemplates());
        }

        public Task SaveReportTemplatesAsync(IReadOnlyList<ReportTemplate> templates, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<EvidenceCaptureSettings> LoadEvidenceCaptureSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new EvidenceCaptureSettings());
        }

        public Task SaveEvidenceCaptureSettingsAsync(EvidenceCaptureSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<DailyReportAutomationSettings> LoadDailyReportAutomationSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DailyReportAutomationSettings());
        }

        public Task SaveDailyReportAutomationSettingsAsync(DailyReportAutomationSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeModelClient : IModelClient
    {
        public string GenerateResult { get; set; } = "";
        public string PolishResult { get; set; } = "";
        public bool PolishCalled { get; private set; }

        public Task<string> GenerateAsync(
            ModelProviderSettings settings,
            string prompt,
            IReadOnlyList<EvidenceItem> evidence,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(GenerateResult);
        }

        public Task<ModelConnectionTestResult> TestConnectionAsync(
            SingleModelProviderSettings provider,
            ModelConnectionTestKind kind,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelConnectionTestResult(true, "ok", "", false));
        }

        public Task<string> PolishReportAsync(
            ModelProviderSettings settings,
            string draft,
            IReadOnlyList<EvidenceItem> evidence,
            CancellationToken cancellationToken = default)
        {
            PolishCalled = true;
            return Task.FromResult(PolishResult);
        }
    }
}
