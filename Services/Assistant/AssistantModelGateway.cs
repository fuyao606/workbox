using Timenotifier.Models;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Settings;

namespace Timenotifier.Services.Assistant;

public interface IAssistantModelGateway
{
    Task<string> CompleteAsync(
        string prompt,
        IReadOnlyList<ModelImageInput> images,
        CancellationToken cancellationToken = default);
}

public sealed class DeterministicAssistantModelGateway : IAssistantModelGateway
{
    public async Task<string> CompleteAsync(
        string prompt,
        IReadOnlyList<ModelImageInput> images,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(250, cancellationToken);
        if (prompt.Contains("打开报告", StringComparison.OrdinalIgnoreCase))
        {
            return "{\"reply\":\"已为你打开报告页。\",\"action\":{\"name\":\"navigate_module\",\"arguments\":{\"module\":\"reports\"}}}";
        }

        if (prompt.Contains("新增提醒", StringComparison.OrdinalIgnoreCase))
        {
            return "{\"reply\":\"我已准备新增一条工作日提醒。\",\"action\":{\"name\":\"upsert_reminder\",\"arguments\":{\"message\":\"UI Loop 测试提醒\",\"time\":\"18:30\",\"scheduleKind\":\"Workdays\"}}}";
        }

        return "{\"reply\":\"这是确定性的 AI 助手测试回复。\",\"action\":null}";
    }

}

public sealed class AssistantModelGateway(
    IAppSettingsStore settingsStore,
    IAssistantModelClient modelClient) : IAssistantModelGateway
{
    public async Task<string> CompleteAsync(
        string prompt,
        IReadOnlyList<ModelImageInput> images,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadModelSettingsAsync(cancellationToken);
        ValidateSettings(settings, images);
        return await modelClient.GenerateAssistantAsync(settings, prompt, images, cancellationToken);
    }

    private static void ValidateSettings(
        ModelProviderSettings settings,
        IReadOnlyList<ModelImageInput> images)
    {
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException("请先在模型页配置 API 请求地址、API Key 和模型名称。");
        }

        if (images.Count > 0 && !settings.UseVision)
        {
            throw new InvalidOperationException("当前未启用视觉分析，请先在模型页启用后再发送图片。");
        }
    }
}
