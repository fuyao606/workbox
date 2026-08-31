using System.Text.Json;

namespace Timenotifier.Services.Assistant;

public sealed record AssistantParsedAction(string Name, JsonElement Arguments);

public sealed record AssistantParsedResponse(string Reply, AssistantParsedAction? Action);

public static class AssistantResponseParser
{
    public static AssistantParsedResponse Parse(string response)
    {
        var original = response?.Trim() ?? "";
        if (!TryExtractJsonCandidate(original, out var candidate))
        {
            return new AssistantParsedResponse(original, null);
        }

        try
        {
            using var document = JsonDocument.Parse(candidate);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new AssistantParsedResponse(original, null);
            }

            var root = document.RootElement;
            if (!root.TryGetProperty("reply", out var replyElement)
                || replyElement.ValueKind != JsonValueKind.String)
            {
                return new AssistantParsedResponse(original, null);
            }

            var reply = replyElement.GetString()?.Trim() ?? "";
            if (!root.TryGetProperty("action", out var actionElement)
                || actionElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return new AssistantParsedResponse(reply, null);
            }

            if (CountActionLikeObjects(root) != 1)
            {
                return new AssistantParsedResponse(reply, null);
            }

            if (actionElement.ValueKind != JsonValueKind.Object
                || !actionElement.TryGetProperty("name", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nameElement.GetString())
                || !actionElement.TryGetProperty("arguments", out var argumentsElement)
                || argumentsElement.ValueKind != JsonValueKind.Object)
            {
                return new AssistantParsedResponse(reply, null);
            }

            return new AssistantParsedResponse(
                reply,
                new AssistantParsedAction(nameElement.GetString()!.Trim(), argumentsElement.Clone()));
        }
        catch (JsonException)
        {
            return new AssistantParsedResponse(original, null);
        }
    }

    private static bool TryExtractJsonCandidate(string response, out string candidate)
    {
        candidate = response;
        if (!response.StartsWith("```", StringComparison.Ordinal))
        {
            return response.IndexOf("```", StringComparison.Ordinal) < 0;
        }

        var firstLineEnd = response.IndexOf('\n');
        var lastFence = response.LastIndexOf("```", StringComparison.Ordinal);
        if (firstLineEnd < 0
            || lastFence <= firstLineEnd
            || !string.IsNullOrWhiteSpace(response[(lastFence + 3)..])
            || response[(firstLineEnd + 1)..lastFence].Contains("```", StringComparison.Ordinal))
        {
            return false;
        }

        var fenceHeader = response[3..firstLineEnd].Trim();
        if (fenceHeader.Length > 0
            && !string.Equals(fenceHeader, "json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        candidate = response[(firstLineEnd + 1)..lastFence].Trim();
        return true;
    }

    private static int CountActionLikeObjects(JsonElement element)
    {
        var count = 0;
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("name", out _)
                && element.TryGetProperty("arguments", out _))
            {
                count++;
            }

            foreach (var property in element.EnumerateObject())
            {
                count += CountActionLikeObjects(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                count += CountActionLikeObjects(item);
            }
        }

        return count;
    }
}
