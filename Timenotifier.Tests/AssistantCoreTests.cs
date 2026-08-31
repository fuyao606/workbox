using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Timenotifier.Models;
using Timenotifier.Services.Assistant;
using Timenotifier.Services.Infrastructure;
using Xunit;

namespace Timenotifier.Tests;

public sealed class AssistantCoreTests
{
    [Fact]
    public void Assistant_models_expose_only_safe_persistence_and_image_contracts()
    {
        var attachmentProperties = PropertyNames<AssistantAttachmentMetadata>();
        var actionProperties = PropertyNames<AssistantActionRecord>();
        var imageProperties = PropertyNames<ModelImageInput>();

        Assert.Equal(
            new[] { "MediaType", "Name", "SizeBytes" },
            attachmentProperties.OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[] { "Name", "ProposalId", "Risk", "Status", "Summary" },
            actionProperties.OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[] { "DisplayName", "JpegBytes", "MediaType" },
            imageProperties.OrderBy(name => name, StringComparer.Ordinal).ToArray());

        var input = new ModelImageInput([0xFF, 0xD8, 0xFF, 0xD9], "image/jpeg", "diagram.jpg");
        Assert.Equal("image/jpeg", input.MediaType);
        Assert.Equal("diagram.jpg", input.DisplayName);
        Assert.DoesNotContain("Path", imageProperties);
    }

    [Fact]
    public void Parser_accepts_plain_json_with_one_action()
    {
        var parsed = AssistantResponseParser.Parse(
            """
            {"reply":"我可以帮你打开证据页。","action":{"name":"navigate_module","arguments":{"module":"evidence"}}}
            """);

        Assert.Equal("我可以帮你打开证据页。", parsed.Reply);
        Assert.NotNull(parsed.Action);
        Assert.Equal("navigate_module", parsed.Action.Name);
        Assert.Equal("evidence", parsed.Action.Arguments.GetProperty("module").GetString());
    }

    [Fact]
    public void Parser_accepts_fenced_json()
    {
        var parsed = AssistantResponseParser.Parse(
            """
            ```json
            {"reply":"已整理当前状态。","action":null}
            ```
            """);

        Assert.Equal("已整理当前状态。", parsed.Reply);
        Assert.Null(parsed.Action);
    }

    [Fact]
    public void Parser_treats_malformed_or_multiple_actions_as_plain_reply_without_action()
    {
        var malformed = AssistantResponseParser.Parse("这不是 JSON，但仍应显示给用户。");
        var multiple = AssistantResponseParser.Parse(
            """
            {"reply":"不能执行多个动作。","action":[{"name":"navigate_module","arguments":{}},{"name":"delete_evidence","arguments":{}}]}
            """);

        Assert.Equal("这不是 JSON，但仍应显示给用户。", malformed.Reply);
        Assert.Null(malformed.Action);
        Assert.Equal("不能执行多个动作。", multiple.Reply);
        Assert.Null(multiple.Action);
    }

    [Fact]
    public void Parser_rejects_non_object_arguments_and_multiple_action_like_objects()
    {
        var nonObjectArguments = AssistantResponseParser.Parse(
            """
            {"reply":"参数无效。","action":{"name":"navigate_module","arguments":"evidence"}}
            """);
        var multipleActionObjects = AssistantResponseParser.Parse(
            """
            {"reply":"只能执行一个动作。","action":{"name":"navigate_module","arguments":{"module":"evidence"}},"alternative":{"name":"delete_evidence","arguments":{"id":"e1"}}}
            """);

        Assert.Equal("参数无效。", nonObjectArguments.Reply);
        Assert.Null(nonObjectArguments.Action);
        Assert.Equal("只能执行一个动作。", multipleActionObjects.Reply);
        Assert.Null(multipleActionObjects.Action);
    }

    [Fact]
    public async Task Session_store_encrypts_visible_text_and_atomically_replaces_the_file()
    {
        using var temp = new TemporaryDirectory();
        var paths = new AppDataPaths(temp.Path);
        var store = new AssistantSessionStore(paths);
        var marker = $"secret-message-{Guid.NewGuid():N}";
        var session = Session(marker);

        await store.SaveAsync(session);
        session.Messages.Add(new AssistantMessage
        {
            Role = AssistantMessageRole.Assistant,
            Text = "第二条回复",
            CreatedAt = DateTime.Now
        });
        await store.SaveAsync(session);

        var files = Directory.GetFiles(paths.AssistantSessionsDirectory, "*.bin");
        Assert.Single(files);
        Assert.Empty(Directory.GetFiles(paths.AssistantSessionsDirectory, "*.tmp"));
        var bytes = await File.ReadAllBytesAsync(files[0]);
        Assert.DoesNotContain(marker, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);

        var loaded = await store.LoadAsync(session.Id);
        Assert.NotNull(loaded);
        Assert.Equal(marker, loaded.Messages[0].Text);
        Assert.Equal(2, loaded.Messages.Count);
    }

    [Fact]
    public async Task Session_store_roundtrips_proposed_action_status_without_state_decisions()
    {
        using var temp = new TemporaryDirectory();
        var store = new AssistantSessionStore(new AppDataPaths(temp.Path));
        var session = Session("请删除证据");
        session.Messages.Add(new AssistantMessage
        {
            Role = AssistantMessageRole.Assistant,
            Text = "等待确认",
            CreatedAt = DateTime.Now,
            Action = new AssistantActionRecord
            {
                Name = "delete_evidence",
                Summary = "删除 1 条证据",
                Risk = AssistantActionRisk.Confirmation,
                Status = AssistantActionStatus.Proposed
            }
        });

        await store.SaveAsync(session);
        var loaded = await store.LoadAsync(session.Id);

        Assert.NotNull(loaded);
        Assert.Equal(AssistantActionStatus.Proposed, loaded.Messages[1].Action!.Status);
    }

    [Fact]
    public async Task Session_store_skips_one_corrupt_file_and_loads_other_sessions()
    {
        using var temp = new TemporaryDirectory();
        var paths = new AppDataPaths(temp.Path);
        var store = new AssistantSessionStore(paths);
        var good = Session("可读取会话");
        await store.SaveAsync(good);
        Directory.CreateDirectory(paths.AssistantSessionsDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(paths.AssistantSessionsDirectory, $"{Guid.NewGuid():N}.bin"),
            [1, 2, 3, 4]);

        var result = await store.LoadAllAsync();

        Assert.Single(result.Sessions);
        Assert.Equal(good.Id, result.Sessions[0].Id);
        Assert.Single(result.Warnings);
        Assert.DoesNotContain(temp.Path, result.Warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Session_store_skips_decryptable_but_invalid_session_file()
    {
        using var temp = new TemporaryDirectory();
        var paths = new AppDataPaths(temp.Path);
        var store = new AssistantSessionStore(paths);
        var good = Session("有效会话");
        await store.SaveAsync(good);
        var invalid = Session("无效会话");
        invalid.Id = "not-a-session-id";
        var invalidJson = JsonSerializer.SerializeToUtf8Bytes(invalid);
        var encrypted = ProtectedData.Protect(
            invalidJson,
            Encoding.UTF8.GetBytes("Timenotifier.AssistantSessions.v1"),
            DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(
            Path.Combine(paths.AssistantSessionsDirectory, $"{Guid.NewGuid():N}.bin"),
            encrypted);

        var result = await store.LoadAllAsync();

        Assert.Single(result.Sessions);
        Assert.Equal(good.Id, result.Sessions[0].Id);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task Session_store_roundtrips_only_safe_attachment_and_action_metadata()
    {
        using var temp = new TemporaryDirectory();
        var paths = new AppDataPaths(temp.Path);
        var store = new AssistantSessionStore(paths);
        var session = Session("普通工作内容");
        session.Messages[0].Attachments[0].Name = @"C:\private-folder\notes.txt";
        session.Messages[0].Action = new AssistantActionRecord
        {
            ProposalId = "proposal-1",
            Name = "navigate_module",
            Summary = "打开证据页",
            Risk = AssistantActionRisk.Immediate,
            Status = AssistantActionStatus.Executed
        };

        await store.SaveAsync(session);

        var file = Assert.Single(Directory.GetFiles(paths.AssistantSessionsDirectory, "*.bin"));
        var encrypted = await File.ReadAllBytesAsync(file);
        var json = ProtectedData.Unprotect(
            encrypted,
            Encoding.UTF8.GetBytes("Timenotifier.AssistantSessions.v1"),
            DataProtectionScope.CurrentUser);
        using var document = JsonDocument.Parse(json);
        var persistedMessage = document.RootElement.GetProperty("Messages")[0];
        var persistedAttachment = persistedMessage.GetProperty("Attachments")[0];
        var persistedAction = persistedMessage.GetProperty("Action");
        var loaded = await store.LoadAsync(session.Id);

        Assert.NotNull(loaded);
        Assert.Equal("notes.txt", loaded.Messages[0].Attachments[0].Name);
        Assert.Equal("text/plain", loaded.Messages[0].Attachments[0].MediaType);
        Assert.Equal(12, loaded.Messages[0].Attachments[0].SizeBytes);
        Assert.Equal("proposal-1", loaded.Messages[0].Action!.ProposalId);
        Assert.Equal("navigate_module", loaded.Messages[0].Action!.Name);
        Assert.Equal("打开证据页", loaded.Messages[0].Action!.Summary);
        Assert.Equal(AssistantActionRisk.Immediate, loaded.Messages[0].Action!.Risk);
        Assert.Equal(AssistantActionStatus.Executed, loaded.Messages[0].Action!.Status);
        Assert.Equal(
            new[] { "MediaType", "Name", "SizeBytes" },
            JsonPropertyNames(persistedAttachment));
        Assert.Equal("notes.txt", persistedAttachment.GetProperty("Name").GetString());
        Assert.Equal(
            new[] { "Name", "ProposalId", "Risk", "Status", "Summary" },
            JsonPropertyNames(persistedAction));
    }

    [Theory]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("ASIAIOSFODNN7EXAMPLE")]
    [InlineData("sk-test-Ab12Cd34Ef56")]
    [InlineData("rk-test-Ab12Cd34Ef56")]
    [InlineData("pk-test-Ab12Cd34Ef56")]
    [InlineData("api-Ab12Cd34Ef56")]
    [InlineData("token-Ab12Cd34Ef56")]
    [InlineData("secret-Ab12Cd34Ef56")]
    public void Sensitive_guard_rejects_short_prefixed_credentials_without_echoing_them(string credential)
    {
        var result = new AssistantSensitiveInputGuard().Check(
            $"use {credential} now",
            [],
            new AssistantProtectedValueSnapshot([]));

        Assert.False(result.IsAllowed);
        Assert.Equal(AssistantSensitiveInputCategory.Credential, result.Category);
        Assert.Equal(AssistantSensitiveInputReasonCode.HighEntropyToken, result.ReasonCode);
        Assert.DoesNotContain(credential, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("prefix Ab3dE5fGh7Jk9Lm2Np4Qr6St8Uv0WxYz suffix", AssistantSensitiveInputReasonCode.HighEntropyToken)]
    [InlineData("-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\n-----END PRIVATE KEY-----", AssistantSensitiveInputReasonCode.PemPrivateKey)]
    [InlineData("请打开 C:\\Users\\someone\\report.txt", AssistantSensitiveInputReasonCode.AbsoluteDrivePath)]
    [InlineData("请读取 \\\\server\\share\\report.txt", AssistantSensitiveInputReasonCode.UncPath)]
    [InlineData("请读取 file:///C:/Users/someone/report.txt", AssistantSensitiveInputReasonCode.FileUri)]
    public void Sensitive_guard_rejects_generic_secret_and_path_patterns(
        string text,
        AssistantSensitiveInputReasonCode expectedReason)
    {
        var result = new AssistantSensitiveInputGuard().Check(
            text,
            [],
            new AssistantProtectedValueSnapshot([]));

        Assert.False(result.IsAllowed);
        Assert.NotEqual(AssistantSensitiveInputCategory.None, result.Category);
        Assert.Equal(expectedReason, result.ReasonCode);
    }

    [Fact]
    public void Sensitive_guard_rejects_unlabeled_exact_protected_value_without_echoing_it()
    {
        var protectedValue = $"sk-current-{Guid.NewGuid():N}";

        var result = new AssistantSensitiveInputGuard().Check(
            $"请使用 {protectedValue} 继续",
            [],
            new AssistantProtectedValueSnapshot(["", "   ", protectedValue]));

        Assert.False(result.IsAllowed);
        Assert.Equal(AssistantSensitiveInputCategory.ProtectedValue, result.Category);
        Assert.Equal(AssistantSensitiveInputReasonCode.ExactProtectedValue, result.ReasonCode);
        Assert.DoesNotContain(protectedValue, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Sensitive_guard_rejects_protected_value_found_only_in_attachment_text()
    {
        var protectedValue = $"private-setting-{Guid.NewGuid():N}";

        var result = new AssistantSensitiveInputGuard().Check(
            "请总结附件中的普通工作内容",
            ["项目进展正常", $"配置内容为 {protectedValue}"],
            new AssistantProtectedValueSnapshot([protectedValue]));

        Assert.False(result.IsAllowed);
        Assert.Equal(AssistantSensitiveInputReasonCode.ExactProtectedValue, result.ReasonCode);
    }

    [Fact]
    public void Sensitive_guard_allows_ordinary_chinese_and_english_work_text()
    {
        var result = new AssistantSensitiveInputGuard().Check(
            "请总结今天完成的登录页修复，并列出 tomorrow's review items.",
            ["测试覆盖了空状态、取消操作和日报草稿。"],
            new AssistantProtectedValueSnapshot(["a-current-secret-that-is-not-present"]));

        Assert.True(result.IsAllowed);
        Assert.Equal(AssistantSensitiveInputCategory.None, result.Category);
        Assert.Equal(AssistantSensitiveInputReasonCode.None, result.ReasonCode);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Session_store_contract_does_not_accept_protected_snapshots()
    {
        var parameterTypes = typeof(IAssistantSessionStore)
            .GetMethods()
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToList();

        Assert.DoesNotContain(typeof(AssistantProtectedValueSnapshot), parameterTypes);
    }

    private static AssistantSession Session(string text)
    {
        var now = DateTime.Now;
        return new AssistantSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = text,
            CreatedAt = now,
            UpdatedAt = now,
            Messages =
            [
                new AssistantMessage
                {
                    Role = AssistantMessageRole.User,
                    Text = text,
                    CreatedAt = now,
                    Attachments =
                    [
                        new AssistantAttachmentMetadata
                        {
                            Name = "notes.txt",
                            MediaType = "text/plain",
                            SizeBytes = 12
                        }
                    ]
                }
            ]
        };
    }

    private static HashSet<string> PropertyNames<T>()
    {
        return typeof(T)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string[] JsonPropertyNames(JsonElement element)
    {
        return element
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Timenotifier.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
