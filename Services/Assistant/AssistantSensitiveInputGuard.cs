using System.Text.RegularExpressions;
using Timenotifier.Models;

namespace Timenotifier.Services.Assistant;

public enum AssistantSensitiveInputCategory
{
    None,
    ProtectedValue,
    Credential,
    PrivateKey,
    Path
}

public enum AssistantSensitiveInputReasonCode
{
    None,
    ExactProtectedValue,
    HighEntropyToken,
    PemPrivateKey,
    AbsoluteDrivePath,
    UncPath,
    FileUri
}

public sealed record AssistantSensitiveInputResult(
    bool IsAllowed,
    AssistantSensitiveInputCategory Category,
    AssistantSensitiveInputReasonCode ReasonCode);

public sealed partial class AssistantSensitiveInputGuard
{
    private static readonly AssistantSensitiveInputResult Allowed = new(
        true,
        AssistantSensitiveInputCategory.None,
        AssistantSensitiveInputReasonCode.None);

    public AssistantSensitiveInputResult Check(
        string userText,
        IReadOnlyList<string> extractedAttachmentTexts,
        AssistantProtectedValueSnapshot protectedValues)
    {
        ArgumentNullException.ThrowIfNull(extractedAttachmentTexts);
        ArgumentNullException.ThrowIfNull(protectedValues);

        foreach (var text in EnumerateInputs(userText, extractedAttachmentTexts))
        {
            var result = CheckText(text, protectedValues.Values);
            if (!result.IsAllowed)
            {
                return result;
            }
        }

        return Allowed;
    }

    private static AssistantSensitiveInputResult CheckText(
        string text,
        IReadOnlyList<string> protectedValues)
    {
        foreach (var value in protectedValues)
        {
            if (!string.IsNullOrWhiteSpace(value)
                && text.Contains(value, StringComparison.Ordinal))
            {
                return Blocked(
                    AssistantSensitiveInputCategory.ProtectedValue,
                    AssistantSensitiveInputReasonCode.ExactProtectedValue);
            }
        }

        if (PemPrivateKeyRegex().IsMatch(text))
        {
            return Blocked(
                AssistantSensitiveInputCategory.PrivateKey,
                AssistantSensitiveInputReasonCode.PemPrivateKey);
        }

        if (FileUriRegex().IsMatch(text))
        {
            return Blocked(
                AssistantSensitiveInputCategory.Path,
                AssistantSensitiveInputReasonCode.FileUri);
        }

        if (UncPathRegex().IsMatch(text))
        {
            return Blocked(
                AssistantSensitiveInputCategory.Path,
                AssistantSensitiveInputReasonCode.UncPath);
        }

        if (AbsoluteDrivePathRegex().IsMatch(text))
        {
            return Blocked(
                AssistantSensitiveInputCategory.Path,
                AssistantSensitiveInputReasonCode.AbsoluteDrivePath);
        }

        if (AwsAccessKeyIdRegex().IsMatch(text)
            || CredentialTokenRegex().IsMatch(text)
            || HighEntropyCandidateRegex().Matches(text)
                .Select(match => match.Value)
                .Any(IsHighEntropyToken))
        {
            return Blocked(
                AssistantSensitiveInputCategory.Credential,
                AssistantSensitiveInputReasonCode.HighEntropyToken);
        }

        return Allowed;
    }

    private static IEnumerable<string> EnumerateInputs(
        string userText,
        IReadOnlyList<string> attachmentTexts)
    {
        yield return userText ?? "";
        foreach (var text in attachmentTexts)
        {
            yield return text ?? "";
        }
    }

    private static bool IsHighEntropyToken(string candidate)
    {
        if (!candidate.Any(char.IsLetter) || !candidate.Any(char.IsDigit))
        {
            return false;
        }

        var entropy = 0d;
        foreach (var count in candidate.GroupBy(character => character).Select(group => group.Count()))
        {
            var probability = (double)count / candidate.Length;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy >= 3.5d;
    }

    private static AssistantSensitiveInputResult Blocked(
        AssistantSensitiveInputCategory category,
        AssistantSensitiveInputReasonCode reasonCode)
    {
        return new AssistantSensitiveInputResult(false, category, reasonCode);
    }

    [GeneratedRegex(
        @"-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PemPrivateKeyRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])file:(?://)?[^\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileUriRegex();

    [GeneratedRegex(@"(?<![\\])\\\\[^\\/\s]+[\\/][^\\/\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex UncPathRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]:[\\/][^\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteDrivePathRegex();

    [GeneratedRegex(@"(?<![A-Z0-9])(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex AwsAccessKeyIdRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:sk|rk|pk|api|token|secret)[-_][A-Za-z0-9_+=/-]{12,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialTokenRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_+=/-])[A-Za-z0-9_+=/-]{24,}(?![A-Za-z0-9_+=/-])", RegexOptions.CultureInvariant)]
    private static partial Regex HighEntropyCandidateRegex();
}
