using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Timenotifier.Models;

namespace Timenotifier.Services.Infrastructure;

public interface IModelClient
{
    Task<string> GenerateAsync(
        ModelProviderSettings settings,
        string prompt,
        IReadOnlyList<EvidenceItem> evidence,
        CancellationToken cancellationToken = default);

    Task<ModelConnectionTestResult> TestConnectionAsync(
        SingleModelProviderSettings provider,
        ModelConnectionTestKind kind,
        CancellationToken cancellationToken = default);

    Task<string> PolishReportAsync(
        ModelProviderSettings settings,
        string draft,
        IReadOnlyList<EvidenceItem> evidence,
        CancellationToken cancellationToken = default);
}

public interface IAssistantModelClient
{
    Task<string> GenerateAssistantAsync(
        ModelProviderSettings settings,
        string prompt,
        IReadOnlyList<ModelImageInput> images,
        CancellationToken cancellationToken);
}

public sealed class ResponsesModelClient(HttpClient? httpClient = null) : IModelClient, IAssistantModelClient
{
    private const int TransientRetryCount = 3;
    private const int MaxOutputTokens = 900;
    private const int MaxVisionImages = 2;
    private const int MaxVisionImageEdge = 1280;
    private const long VisionJpegQuality = 72L;
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(2);

    private readonly HttpClient _httpClient = httpClient ?? CreateDefaultHttpClient();

    public async Task<string> GenerateAsync(
        ModelProviderSettings settings,
        string prompt,
        IReadOnlyList<EvidenceItem> evidence,
        CancellationToken cancellationToken = default)
    {
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException("请先配置 API 请求地址、API Key 和模型名称。");
        }

        var cleanSettings = settings.Clean();
        if (cleanSettings.QualityMode == ModelQualityMode.LowCost)
        {
            return await GenerateWithCleanSettingsAsync(
                cleanSettings.Analysis,
                useVision: false,
                prompt,
                evidence,
                cancellationToken);
        }

        var visionEvidence = GetVisionEvidence(evidence);
        if (cleanSettings.UseVision && visionEvidence.Count > 0)
        {
            string? visionNotes = null;
            try
            {
                visionNotes = await GenerateWithCleanSettingsAsync(
                    cleanSettings.Analysis,
                    useVision: true,
                    BuildVisionAnalysisPrompt(visionEvidence),
                    visionEvidence,
                    cancellationToken);
            }
            catch (Exception ex) when (IsVisionFallbackCandidate(ex))
            {
                var content = await GenerateWithCleanSettingsAsync(
                    cleanSettings.Analysis,
                    useVision: false,
                    prompt,
                    evidence,
                    cancellationToken);
                return $"{BuildVisionFallbackNotice(ex)}{Environment.NewLine}{Environment.NewLine}{content}";
            }

            return await GenerateWithCleanSettingsAsync(
                cleanSettings.Analysis,
                useVision: false,
                AppendVisionNotes(prompt, visionNotes),
                evidence,
                cancellationToken);
        }

        return await GenerateWithCleanSettingsAsync(
            cleanSettings.Analysis,
            useVision: false,
            prompt,
            evidence,
            cancellationToken);
    }

    public async Task<string> GenerateAssistantAsync(
        ModelProviderSettings settings,
        string prompt,
        IReadOnlyList<ModelImageInput> images,
        CancellationToken cancellationToken)
    {
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException("请先在模型页配置 API 请求地址、API Key 和模型名称。");
        }

        ValidateAssistantImages(settings, images);
        return await GenerateAssistantWithCleanSettingsAsync(
            settings.Clean().Analysis,
            prompt,
            images,
            cancellationToken);
    }

    public async Task<ModelConnectionTestResult> TestConnectionAsync(
        SingleModelProviderSettings provider,
        ModelConnectionTestKind kind,
        CancellationToken cancellationToken = default)
    {
        var cleanProvider = provider.Clean();
        if (!cleanProvider.IsConfigured)
        {
            return new ModelConnectionTestResult(false, "请先填写 API 请求地址、API Key 和模型名称。", "", false);
        }

        if (kind == ModelConnectionTestKind.ImageGeneration)
        {
            try
            {
                var endpoint = BuildImageGenerationsUri(cleanProvider.BaseUrl).ToString();
                await SendImageGenerationTestAsync(cleanProvider, cancellationToken);
                return new ModelConnectionTestResult(true, $"{DisplayKind(kind)}连接成功。", endpoint, false);
            }
            catch (Exception ex)
            {
                return new ModelConnectionTestResult(false, FriendlyError(ex), BuildImageGenerationsUri(cleanProvider.BaseUrl).ToString(), false);
            }
        }

        const string prompt = "请回复：连接成功";
        try
        {
            var endpoint = BuildChatCompletionsUri(cleanProvider.BaseUrl).ToString();
            _ = await SendChatCompletionsAsync(cleanProvider, useVision: false, prompt, [], cancellationToken);
            return new ModelConnectionTestResult(true, $"{DisplayKind(kind)}连接成功。", endpoint, false);
        }
        catch (ModelRequestException chatException) when (ShouldTryResponsesFallback(chatException, cleanProvider))
        {
            try
            {
                var endpoint = BuildResponsesUri(cleanProvider.BaseUrl).ToString();
                _ = await SendResponsesAsync(cleanProvider, useVision: false, prompt, [], cancellationToken);
                return new ModelConnectionTestResult(true, $"{DisplayKind(kind)}连接成功，已使用 Responses 兼容兜底。", endpoint, true);
            }
            catch (Exception responsesException)
            {
                return new ModelConnectionTestResult(false, FriendlyError(responsesException), BuildResponsesUri(cleanProvider.BaseUrl).ToString(), true);
            }
        }
        catch (Exception ex)
        {
            return new ModelConnectionTestResult(false, FriendlyError(ex), BuildChatCompletionsUri(cleanProvider.BaseUrl).ToString(), false);
        }
    }

    public async Task<string> PolishReportAsync(
        ModelProviderSettings settings,
        string draft,
        IReadOnlyList<EvidenceItem> evidence,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(draft))
        {
            return draft;
        }

        var prompt = $"""
            你是严谨的工作报告编辑。请只在不新增事实的前提下润色下面的报告草稿，让表达更清晰、结构更紧凑、语气更职场。

            要求：
            1. 不要新增草稿中没有的事实。
            2. 保留证据编号，例如 [E1]。
            3. 保留 Markdown 二级标题结构。
            4. 如果内容不足，不要补编，只做表达整理。

            草稿：
            {draft}
            """;

        return await GenerateWithCleanSettingsAsync(
            settings.Clean().Analysis,
            useVision: false,
            prompt,
            evidence,
            cancellationToken);
    }

    private async Task<string> GenerateWithCleanSettingsAsync(
        SingleModelProviderSettings provider,
        bool useVision,
        string prompt,
        IReadOnlyList<EvidenceItem> evidence,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= TransientRetryCount; attempt++)
        {
            try
            {
                return await SendResponsesAsync(provider, useVision, prompt, evidence, cancellationToken);
            }
            catch (ModelRequestException responsesException) when (ShouldTryChatFallback(
                responsesException,
                provider,
                includeServerErrors: !useVision))
            {
                try
                {
                    return await SendChatCompletionsAsync(provider, useVision, prompt, evidence, cancellationToken);
                }
                catch (Exception chatException) when (IsTransient(chatException) && attempt < TransientRetryCount)
                {
                    await Task.Delay(TransientRetryDelay * attempt, cancellationToken);
                }
                catch (Exception chatException)
                {
                    throw new InvalidOperationException(
                        $"Responses 请求失败：{responsesException.Message}{Environment.NewLine}Chat Completions 兜底也失败：{FlattenException(chatException)}",
                        chatException);
                }
            }
            catch (HttpRequestException ex) when (IsTransient(ex) && attempt < TransientRetryCount)
            {
                await Task.Delay(TransientRetryDelay * attempt, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException($"模型请求发送失败：{FlattenException(ex)}", ex);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < TransientRetryCount)
            {
                await Task.Delay(TransientRetryDelay * attempt, cancellationToken);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("模型请求超时，请检查 API 地址、网络或服务端响应速度。", ex);
            }
        }

        throw new InvalidOperationException("模型请求失败：已重试但没有获得有效响应。");
    }

    private async Task<string> GenerateAssistantWithCleanSettingsAsync(
        SingleModelProviderSettings provider,
        string prompt,
        IReadOnlyList<ModelImageInput> images,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= TransientRetryCount; attempt++)
        {
            try
            {
                return await SendAssistantResponsesAsync(provider, prompt, images, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ModelRequestException responsesException) when (ShouldTryChatFallback(
                responsesException,
                provider,
                includeServerErrors: true))
            {
                try
                {
                    return await SendAssistantChatCompletionsAsync(provider, prompt, images, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception chatException) when (IsTransient(chatException) && attempt < TransientRetryCount)
                {
                    await Task.Delay(TransientRetryDelay * attempt, cancellationToken);
                }
                catch (Exception chatException)
                {
                    throw CreateSafeAssistantException(chatException);
                }
            }
            catch (HttpRequestException exception) when (IsTransient(exception) && attempt < TransientRetryCount)
            {
                await Task.Delay(TransientRetryDelay * attempt, cancellationToken);
            }
            catch (TaskCanceledException) when (attempt < TransientRetryCount)
            {
                await Task.Delay(TransientRetryDelay * attempt, cancellationToken);
            }
            catch (Exception exception)
            {
                throw CreateSafeAssistantException(exception);
            }
        }

        throw new InvalidOperationException("模型请求失败，请稍后重试。");
    }

    private async Task<string> SendAssistantResponsesAsync(
        SingleModelProviderSettings provider,
        string prompt,
        IReadOnlyList<ModelImageInput> images,
        CancellationToken cancellationToken)
    {
        var responseText = await SendJsonAsync(
            BuildResponsesUri(provider.BaseUrl),
            provider.ApiKey,
            BuildAssistantResponsesRequestBody(provider, prompt, images),
            cancellationToken);
        return ExtractResponsesOutputText(responseText);
    }

    private async Task<string> SendAssistantChatCompletionsAsync(
        SingleModelProviderSettings provider,
        string prompt,
        IReadOnlyList<ModelImageInput> images,
        CancellationToken cancellationToken)
    {
        var responseText = await SendJsonAsync(
            BuildChatCompletionsUri(provider.BaseUrl),
            provider.ApiKey,
            BuildAssistantChatCompletionsRequestBody(provider, prompt, images),
            cancellationToken);
        return ExtractChatCompletionsOutputText(responseText);
    }

    private async Task<string> SendResponsesAsync(
        SingleModelProviderSettings provider,
        bool useVision,
        string prompt,
        IReadOnlyList<EvidenceItem> evidence,
        CancellationToken cancellationToken)
    {
        var uri = BuildResponsesUri(provider.BaseUrl);
        var responseText = await SendJsonAsync(
            uri,
            provider.ApiKey,
            BuildResponsesRequestBody(provider, useVision, prompt, evidence),
            cancellationToken);
        return ExtractResponsesOutputText(responseText);
    }

    private async Task<string> SendChatCompletionsAsync(
        SingleModelProviderSettings provider,
        bool useVision,
        string prompt,
        IReadOnlyList<EvidenceItem> evidence,
        CancellationToken cancellationToken)
    {
        var uri = BuildChatCompletionsUri(provider.BaseUrl);
        var responseText = await SendJsonAsync(
            uri,
            provider.ApiKey,
            BuildChatCompletionsRequestBody(provider, useVision, prompt, evidence),
            cancellationToken);
        return ExtractChatCompletionsOutputText(responseText);
    }

    private async Task<string> SendJsonAsync(
        Uri uri,
        string apiKey,
        object body,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ModelRequestException(uri, response.StatusCode, response.ReasonPhrase, responseText);
        }

        return responseText;
    }

    public static Uri BuildResponsesUri(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        if (trimmed.EndsWith("/responses", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("/v1/responses", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(trimmed);
        }

        return new Uri(trimmed.TrimEnd('/') + "/v1/responses");
    }

    public static Uri BuildChatCompletionsUri(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(trimmed);
        }

        if (trimmed.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
        {
            var withoutResponses = trimmed[..^"/responses".Length].TrimEnd('/');
            return new Uri(withoutResponses + "/chat/completions");
        }

        if (trimmed.EndsWith("/v1/responses", StringComparison.OrdinalIgnoreCase))
        {
            var withoutResponses = trimmed[..^"/responses".Length].TrimEnd('/');
            return new Uri(withoutResponses + "/chat/completions");
        }

        return new Uri(trimmed.TrimEnd('/') + "/v1/chat/completions");
    }

    public static Uri BuildImageGenerationsUri(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        if (trimmed.EndsWith("/images/generations", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("/v1/images/generations", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(trimmed);
        }

        return new Uri(trimmed.TrimEnd('/') + "/v1/images/generations");
    }

    private async Task SendImageGenerationTestAsync(
        SingleModelProviderSettings provider,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            model = provider.Model,
            prompt = "connection test",
            n = 1,
            size = "1024x1024"
        };

        _ = await SendJsonAsync(BuildImageGenerationsUri(provider.BaseUrl), provider.ApiKey, body, cancellationToken);
    }

    public static object BuildResponsesRequestBody(
        SingleModelProviderSettings provider,
        bool useVision,
        string prompt,
        IReadOnlyList<EvidenceItem> evidence)
    {
        List<object> content = [new { type = "input_text", text = prompt }];
        if (useVision)
        {
            foreach (var image in GetVisionEvidence(evidence))
            {
                content.Add(new
                {
                    type = "input_image",
                    image_url = ToDataUrl(image.ImagePath!)
                });
            }
        }

        return new
        {
            model = provider.Model,
            max_output_tokens = MaxOutputTokens,
            input = new object[]
            {
                new
                {
                    role = "user",
                    content
                }
            }
        };
    }

    public static object BuildChatCompletionsRequestBody(
        SingleModelProviderSettings provider,
        bool useVision,
        string prompt,
        IReadOnlyList<EvidenceItem> evidence)
    {
        List<object> content = [new { type = "text", text = prompt }];
        if (useVision)
        {
            foreach (var image in GetVisionEvidence(evidence))
            {
                content.Add(new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = ToDataUrl(image.ImagePath!)
                    }
                });
            }
        }

        return new
        {
            model = provider.Model,
            max_tokens = MaxOutputTokens,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content
                }
            }
        };
    }

    private static object BuildAssistantResponsesRequestBody(
        SingleModelProviderSettings provider,
        string prompt,
        IReadOnlyList<ModelImageInput> images)
    {
        List<object> content = [new { type = "input_text", text = prompt }];
        foreach (var image in images)
        {
            content.Add(new
            {
                type = "input_image",
                image_url = ToDataUrl(image)
            });
        }

        return new
        {
            model = provider.Model,
            max_output_tokens = MaxOutputTokens,
            input = new object[]
            {
                new
                {
                    role = "user",
                    content
                }
            }
        };
    }

    private static object BuildAssistantChatCompletionsRequestBody(
        SingleModelProviderSettings provider,
        string prompt,
        IReadOnlyList<ModelImageInput> images)
    {
        List<object> content = [new { type = "text", text = prompt }];
        foreach (var image in images)
        {
            content.Add(new
            {
                type = "image_url",
                image_url = new
                {
                    url = ToDataUrl(image)
                }
            });
        }

        return new
        {
            model = provider.Model,
            max_tokens = MaxOutputTokens,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content
                }
            }
        };
    }

    private static void ValidateAssistantImages(
        ModelProviderSettings settings,
        IReadOnlyList<ModelImageInput> images)
    {
        if (images.Count > MaxVisionImages)
        {
            throw new InvalidOperationException("一次最多发送 2 张图片。");
        }

        if (images.Count > 0 && !settings.UseVision)
        {
            throw new InvalidOperationException("当前未启用视觉分析，请先在模型页启用后再发送图片。");
        }

        foreach (var image in images)
        {
            if (!string.Equals(image.MediaType, "image/jpeg", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("图片输入必须是 JPEG 格式。");
            }

            if (!IsValidJpeg(image.JpegBytes))
            {
                throw new InvalidOperationException("图片数据不是有效的 JPEG 文件。");
            }
        }
    }

    private static bool IsValidJpeg(byte[]? bytes)
    {
        if (bytes is null
            || bytes.Length < 4
            || bytes[0] != 0xFF
            || bytes[1] != 0xD8
            || bytes[2] != 0xFF
            || bytes[^2] != 0xFF
            || bytes[^1] != 0xD9)
        {
            return false;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var decoded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            return decoded.RawFormat.Guid == ImageFormat.Jpeg.Guid && decoded.Width > 0 && decoded.Height > 0;
        }
        catch (Exception exception) when (exception is ArgumentException or OutOfMemoryException)
        {
            return false;
        }
    }

    private static string ToDataUrl(ModelImageInput image)
    {
        return $"data:image/jpeg;base64,{Convert.ToBase64String(image.JpegBytes)}";
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20)
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
    }

    private static string BuildVisionAnalysisPrompt(IReadOnlyList<EvidenceItem> evidence)
    {
        var evidenceLines = string.Join(
            Environment.NewLine,
            evidence.Select((item, index) => $"[S{index + 1}] {item.CapturedAt:yyyy-MM-dd HH:mm:ss} {item.Title}"));

        return $"""
            你是工作截图分析助手。请只根据随请求提供的截图图片，提取与工作日报/周报有关的工作事实。

            要求：
            1. 不要编造截图中看不到的信息。
            2. 对不确定内容标注“待确认”。
            3. 优先提取可用于汇报的事实：正在处理的任务、可见产出、问题提示、工具或页面状态。
            4. 输出中文，按截图编号列出，每张截图控制在 120 字以内。
            5. 不要生成日报或周报，只输出截图观察摘要。

            截图：
            {evidenceLines}
            """;
    }

    private static string AppendVisionNotes(string prompt, string visionNotes)
    {
        if (string.IsNullOrWhiteSpace(visionNotes))
        {
            return prompt;
        }

        return $"""
            {prompt}

            截图多模态分析结果：
            {visionNotes}
            """;
    }

    private static string BuildVisionFallbackNotice(Exception exception)
    {
        var reason = IsTransient(exception)
            ? "理解模型服务暂时不可用或不支持当前图片输入。"
            : "理解模型配置或接口暂不兼容当前截图分析请求。";
        return $"【提示】截图多模态分析暂时不可用，已跳过截图分析并改用文本证据生成。{reason}";
    }

    private static bool ShouldTryChatFallback(
        ModelRequestException exception,
        SingleModelProviderSettings provider,
        bool includeServerErrors = false)
    {
        if (BuildResponsesUri(provider.BaseUrl) == BuildChatCompletionsUri(provider.BaseUrl))
        {
            return false;
        }

        var body = exception.ResponseText.ToLowerInvariant();
        return exception.StatusCode is HttpStatusCode.NotFound
                or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.BadRequest
            || (includeServerErrors && (int)exception.StatusCode >= 500)
            || body.Contains("responses")
            || body.Contains("response_format")
            || body.Contains("input");
    }

    private static bool ShouldTryResponsesFallback(ModelRequestException exception, SingleModelProviderSettings provider)
    {
        if (BuildChatCompletionsUri(provider.BaseUrl) == BuildResponsesUri(provider.BaseUrl))
        {
            return false;
        }

        var body = exception.ResponseText.ToLowerInvariant();
        return exception.StatusCode is HttpStatusCode.NotFound
                or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.BadRequest
            || body.Contains("chat")
            || body.Contains("messages")
            || body.Contains("completions");
    }

    public static string FlattenException(Exception exception)
    {
        List<string> messages = [];
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(" -> ", messages.Distinct());
    }

    public static string FriendlyError(Exception exception)
    {
        var message = FlattenException(exception);
        if (message.Contains("401", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "认证失败：请检查 API Key 是否正确。";
        }

        if (message.Contains("403", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return "权限不足：请检查 API Key 权限、余额或模型访问权限。";
        }

        if (message.Contains("404", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return "接口或模型不存在：请检查 API 地址和模型名称。";
        }

        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("超时", StringComparison.OrdinalIgnoreCase))
        {
            return "请求超时：请检查网络、API 地址或服务响应速度。";
        }

        if (message.Contains("5", StringComparison.OrdinalIgnoreCase)
            && (message.Contains("server", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Bad Gateway", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Service", StringComparison.OrdinalIgnoreCase)))
        {
            return "服务端暂时不可用：请稍后重试，或切换为低成本模式。";
        }

        if (message.Contains("image", StringComparison.OrdinalIgnoreCase)
            || message.Contains("vision", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unsupported", StringComparison.OrdinalIgnoreCase))
        {
            return "当前接口不支持图片/视觉输入：可关闭视觉分析或使用低成本模式。";
        }

        return $"模型调用失败：{message}";
    }

    private static InvalidOperationException CreateSafeAssistantException(Exception exception)
    {
        var message = exception switch
        {
            ModelRequestException request when request.StatusCode == HttpStatusCode.Unauthorized =>
                "模型认证失败，请检查 API Key。",
            ModelRequestException request when request.StatusCode == HttpStatusCode.Forbidden =>
                "模型访问被拒绝，请检查 API Key 权限或模型访问权限。",
            ModelRequestException request when request.StatusCode == HttpStatusCode.NotFound =>
                "模型接口不可用，请检查模型配置。",
            ModelRequestException request when (int)request.StatusCode >= 500 =>
                "模型服务暂时不可用，请稍后重试。",
            HttpRequestException =>
                "模型请求发送失败，请检查网络连接后重试。",
            TaskCanceledException =>
                "模型请求超时，请稍后重试。",
            JsonException =>
                "模型返回格式无效，请稍后重试。",
            _ =>
                "模型调用失败，请检查模型配置或稍后重试。"
        };
        return new InvalidOperationException(message);
    }

    private static string DisplayKind(ModelConnectionTestKind kind)
    {
        return kind == ModelConnectionTestKind.ImageGeneration ? "ImageGeneration " : "Analysis ";
    }

    private static bool IsTransient(Exception exception)
    {
        var flattened = FlattenException(exception);
        return flattened.Contains("ResponseEnded", StringComparison.OrdinalIgnoreCase)
            || flattened.Contains("response ended prematurely", StringComparison.OrdinalIgnoreCase)
            || flattened.Contains("connection", StringComparison.OrdinalIgnoreCase)
            || flattened.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || flattened.Contains("temporarily", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVisionFallbackCandidate(Exception exception)
    {
        var flattened = FlattenException(exception);
        return flattened.Contains("502", StringComparison.OrdinalIgnoreCase)
            || flattened.Contains("Bad Gateway", StringComparison.OrdinalIgnoreCase)
            || flattened.Contains("upstream", StringComparison.OrdinalIgnoreCase)
            || flattened.Contains("image", StringComparison.OrdinalIgnoreCase)
            || flattened.Contains("vision", StringComparison.OrdinalIgnoreCase)
            || flattened.Contains("multi", StringComparison.OrdinalIgnoreCase)
            || flattened.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || IsTransient(exception);
    }

    private static IReadOnlyList<EvidenceItem> GetVisionEvidence(IReadOnlyList<EvidenceItem> evidence)
    {
        return evidence
            .Where(item => item.Included && !string.IsNullOrWhiteSpace(item.ImagePath) && File.Exists(item.ImagePath))
            .OrderByDescending(item => item.CapturedAt)
            .Take(MaxVisionImages)
            .OrderBy(item => item.CapturedAt)
            .ToList();
    }

    private static string ToDataUrl(string imagePath)
    {
        using var source = Image.FromFile(imagePath);
        var scale = Math.Min(
            1.0,
            Math.Min((double)MaxVisionImageEdge / source.Width, (double)MaxVisionImageEdge / source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(source, 0, 0, width, height);
        }

        using var stream = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.MimeType == "image/jpeg");
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, VisionJpegQuality);
        bitmap.Save(stream, encoder, parameters);

        return $"data:image/jpeg;base64,{Convert.ToBase64String(stream.ToArray())}";
    }

    private static string ExtractResponsesOutputText(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;
        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString() ?? "";
        }

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            List<string> texts = [];
            foreach (var outputItem in output.EnumerateArray())
            {
                if (!outputItem.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var contentItem in content.EnumerateArray())
                {
                    if (contentItem.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        texts.Add(text.GetString() ?? "");
                    }
                }
            }

            if (texts.Count > 0)
            {
                return string.Join(Environment.NewLine, texts);
            }
        }

        return responseText;
    }

    private static string ExtractChatCompletionsOutputText(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return responseText;
        }

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("message", out var message))
            {
                continue;
            }

            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? "";
            }
        }

        return responseText;
    }

    private sealed class ModelRequestException(
        Uri uri,
        HttpStatusCode statusCode,
        string? reasonPhrase,
        string responseText)
        : Exception($"模型请求失败：{(int)statusCode} {reasonPhrase}，地址：{uri}{Environment.NewLine}{responseText}")
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
        public string ResponseText { get; } = responseText;
    }
}
