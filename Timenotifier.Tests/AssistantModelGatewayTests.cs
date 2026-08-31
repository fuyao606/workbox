using System.Net;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using Timenotifier.Models;
using Timenotifier.Services.Assistant;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Scheduling;
using Timenotifier.Services.Settings;
using Xunit;

namespace Timenotifier.Tests;

public sealed class AssistantModelGatewayTests
{
    [Fact]
    public void Gateway_boundary_has_only_internal_settings_image_input_api()
    {
        var method = Assert.Single(typeof(IAssistantModelGateway).GetMethods());
        Assert.Equal("CompleteAsync", method.Name);
        Assert.Equal(
            [typeof(string), typeof(IReadOnlyList<ModelImageInput>), typeof(CancellationToken)],
            method.GetParameters().Select(parameter => parameter.ParameterType));

        var constructor = Assert.Single(typeof(AssistantModelGateway).GetConstructors());
        Assert.Equal(
            [typeof(IAppSettingsStore), typeof(IAssistantModelClient)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(
            typeof(AssistantModelGateway).GetMethods(),
            candidate => candidate.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(ModelProviderSettings)
                || parameter.ParameterType == typeof(PreparedAssistantAttachment)
                || parameter.ParameterType == typeof(IReadOnlyList<PreparedAssistantAttachment>)));
    }

    [Fact]
    public async Task Gateway_loads_current_settings_and_returns_raw_model_text_unchanged()
    {
        var settings = ConfiguredSettings();
        var store = new GatewaySettingsStore(settings);
        var client = new RecordingAssistantModelClient { Result = "plain non-json model text" };
        var gateway = new AssistantModelGateway(store, client);

        var result = await gateway.CompleteAsync("prompt", []);

        Assert.Equal("plain non-json model text", result);
        Assert.Equal(1, store.ModelLoadCount);
        Assert.Same(settings, client.Settings);
        Assert.Equal("prompt", client.Prompt);
    }

    [Fact]
    public async Task Gateway_rejects_unconfigured_model_without_calling_client()
    {
        var client = new RecordingAssistantModelClient();
        var gateway = new AssistantModelGateway(new GatewaySettingsStore(new ModelProviderSettings()), client);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.CompleteAsync("prompt", []));

        Assert.Contains("模型页", exception.Message);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Gateway_rejects_images_when_vision_is_disabled_without_calling_client()
    {
        var settings = ConfiguredSettings();
        settings.UseVision = false;
        var client = new RecordingAssistantModelClient();
        var gateway = new AssistantModelGateway(new GatewaySettingsStore(settings), client);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gateway.CompleteAsync("prompt", [Jpeg("one.jpg", 1)]));

        Assert.Contains("视觉", exception.Message);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Responses_payload_supports_zero_one_and_two_ordered_jpegs_without_names()
    {
        foreach (var count in new[] { 0, 1, 2 })
        {
            string? body = null;
            var handler = new RecordingHttpHandler(async (request, _) =>
            {
                body = await request.Content!.ReadAsStringAsync();
                return JsonResponse(HttpStatusCode.OK, "{\"output_text\":\"complete assistant response\"}");
            });
            var client = new ResponsesModelClient(new HttpClient(handler));
            var images = Enumerable.Range(1, count).Select(index => Jpeg($"secret-name-{index}.jpg", (byte)index)).ToList();

            var result = await client.GenerateAssistantAsync(ConfiguredSettings(), "assistant prompt", images, CancellationToken.None);

            Assert.Equal("complete assistant response", result);
            Assert.Equal(1, handler.CallCount);
            using var document = JsonDocument.Parse(body!);
            var content = document.RootElement.GetProperty("input")[0].GetProperty("content");
            Assert.Equal(count + 1, content.GetArrayLength());
            Assert.Equal("input_text", content[0].GetProperty("type").GetString());
            Assert.Equal("assistant prompt", content[0].GetProperty("text").GetString());
            for (var index = 0; index < count; index++)
            {
                Assert.Equal("input_image", content[index + 1].GetProperty("type").GetString());
                Assert.Equal(
                    $"data:image/jpeg;base64,{Convert.ToBase64String(images[index].JpegBytes)}",
                    content[index + 1].GetProperty("image_url").GetString());
            }
            Assert.DoesNotContain("secret-name", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Chat_fallback_preserves_identical_image_order_and_stops_after_success()
    {
        var uris = new List<string>();
        var bodies = new List<string>();
        var handler = new RecordingHttpHandler(async (request, _) =>
        {
            uris.Add(request.RequestUri!.ToString());
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return uris.Count == 1
                ? JsonResponse(HttpStatusCode.BadGateway, "{\"error\":\"responses unavailable\"}")
                : JsonResponse(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"fallback complete\"}}]}");
        });
        var client = new ResponsesModelClient(new HttpClient(handler));
        var images = new[] { Jpeg("one.jpg", 1), Jpeg("two.jpg", 2) };

        var result = await client.GenerateAssistantAsync(ConfiguredSettings(), "prompt", images, CancellationToken.None);

        Assert.Equal("fallback complete", result);
        Assert.Equal(
            ["https://assistant.example.test/v1/responses", "https://assistant.example.test/v1/chat/completions"],
            uris);
        Assert.Equal(2, handler.CallCount);
        var responseUrls = ReadResponsesImageUrls(bodies[0]);
        var chatUrls = ReadChatImageUrls(bodies[1]);
        Assert.Equal(responseUrls, chatUrls);
        Assert.Equal(
            images.Select(image => $"data:image/jpeg;base64,{Convert.ToBase64String(image.JpegBytes)}"),
            responseUrls);
    }

    [Fact]
    public async Task Successful_responses_call_does_not_also_call_chat_fallback()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(
            JsonResponse(HttpStatusCode.OK, "{\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"part one\"},{\"type\":\"output_text\",\"text\":\"part two\"}]}]}")));
        var client = new ResponsesModelClient(new HttpClient(handler));

        var result = await client.GenerateAssistantAsync(ConfiguredSettings(), "prompt", [], CancellationToken.None);

        Assert.Equal($"part one{Environment.NewLine}part two", result);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Invalid_image_inputs_are_rejected_before_http()
    {
        var cases = new IReadOnlyList<ModelImageInput>[]
        {
            [Jpeg("one.jpg", 1), Jpeg("two.jpg", 2), Jpeg("three.jpg", 3)],
            [new ModelImageInput(JpegBytes(1), "image/png", "wrong.png")],
            [new ModelImageInput([0x01, 0x02, 0x03, 0x04], "image/jpeg", "invalid.jpg")],
            [new ModelImageInput([0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x00, 0xFF, 0xD9], "image/jpeg", "fake-markers.jpg")]
        };

        foreach (var images in cases)
        {
            var handler = new RecordingHttpHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}")));
            var client = new ResponsesModelClient(new HttpClient(handler));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.GenerateAssistantAsync(ConfiguredSettings(), "prompt", images, CancellationToken.None));
            Assert.Equal(0, handler.CallCount);
        }
    }

    [Fact]
    public async Task Cancellation_reaches_http_and_is_not_converted_to_model_failure()
    {
        var cancellationObserved = false;
        var handler = new RecordingHttpHandler(async (_, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
                throw;
            }
        });
        var client = new ResponsesModelClient(new HttpClient(handler));
        using var cancellation = new CancellationTokenSource();

        var pending = client.GenerateAssistantAsync(ConfiguredSettings(), "prompt", [], cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(cancellationObserved);
    }

    [Fact]
    public async Task Server_and_network_failures_are_sanitized()
    {
        const string apiKey = "sk-SENTINEL-FAILURE-KEY";
        const string prompt = "prompt C:\\sentinel\\payload.txt";
        var settings = ConfiguredSettings();
        settings.Analysis.ApiKey = apiKey;
        var responses = new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[]
        {
            (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.Unauthorized, $"{{\"error\":\"{apiKey} {prompt}\"}}")),
            (_, _) => throw new HttpRequestException($"socket-boom {apiKey} {prompt}")
        };

        foreach (var response in responses)
        {
            var client = new ResponsesModelClient(new HttpClient(new RecordingHttpHandler(response)));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.GenerateAssistantAsync(settings, prompt, [], CancellationToken.None));

            Assert.Contains("模型", exception.Message);
            Assert.DoesNotContain(apiKey, exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(prompt, exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(settings.Analysis.BaseUrl, exception.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Gateway_propagates_cancellation_to_model_client()
    {
        var client = new RecordingAssistantModelClient { WaitForCancellation = true };
        var gateway = new AssistantModelGateway(new GatewaySettingsStore(ConfiguredSettings()), client);
        using var cancellation = new CancellationTokenSource();

        var pending = gateway.CompleteAsync("prompt", [], cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(client.CancellationObserved);
    }

    private static ModelProviderSettings ConfiguredSettings()
    {
        return new ModelProviderSettings
        {
            Analysis = new SingleModelProviderSettings
            {
                BaseUrl = "https://assistant.example.test",
                ApiKey = "test-key",
                Model = "test-model"
            },
            UseVision = true
        };
    }

    private static ModelImageInput Jpeg(string name, byte marker) => new(JpegBytes(marker), "image/jpeg", name);

    private static byte[] JpegBytes(byte marker)
    {
        using var bitmap = new Bitmap(1, 1);
        bitmap.SetPixel(0, 0, Color.FromArgb(marker, 255 - marker, marker / 2));
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Jpeg);
        return stream.ToArray();
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode) { Content = new StringContent(body) };
    }

    private static IReadOnlyList<string> ReadResponsesImageUrls(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("input")[0].GetProperty("content")
            .EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "input_image")
            .Select(item => item.GetProperty("image_url").GetString()!)
            .ToList();
    }

    private static IReadOnlyList<string> ReadChatImageUrls(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("messages")[0].GetProperty("content")
            .EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "image_url")
            .Select(item => item.GetProperty("image_url").GetProperty("url").GetString()!)
            .ToList();
    }

    private sealed class RecordingHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return sendAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingAssistantModelClient : IAssistantModelClient
    {
        public string Result { get; init; } = "{}";
        public bool WaitForCancellation { get; init; }
        public int CallCount { get; private set; }
        public ModelProviderSettings? Settings { get; private set; }
        public string? Prompt { get; private set; }
        public bool CancellationObserved { get; private set; }

        public async Task<string> GenerateAssistantAsync(
            ModelProviderSettings settings,
            string prompt,
            IReadOnlyList<ModelImageInput> images,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Settings = settings;
            Prompt = prompt;
            if (WaitForCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }
            }
            return Result;
        }
    }

    private sealed class GatewaySettingsStore(ModelProviderSettings modelSettings) : IAppSettingsStore
    {
        public int ModelLoadCount { get; private set; }

        public Task<ModelProviderSettings> LoadModelSettingsAsync(CancellationToken cancellationToken = default)
        {
            ModelLoadCount++;
            return Task.FromResult(modelSettings);
        }

        public Task SaveModelSettingsAsync(ModelProviderSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ReminderSettings> LoadReminderSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ReminderSettings());
        public Task SaveReminderSettingsAsync(ReminderSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ReportTemplate>> LoadReportTemplatesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ReportTemplate>>([]);
        public Task SaveReportTemplatesAsync(IReadOnlyList<ReportTemplate> templates, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<EvidenceCaptureSettings> LoadEvidenceCaptureSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new EvidenceCaptureSettings());
        public Task SaveEvidenceCaptureSettingsAsync(EvidenceCaptureSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DailyReportAutomationSettings> LoadDailyReportAutomationSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DailyReportAutomationSettings());
        public Task SaveDailyReportAutomationSettingsAsync(DailyReportAutomationSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
