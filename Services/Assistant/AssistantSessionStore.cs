using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Timenotifier.Models;
using Timenotifier.Services.Infrastructure;

namespace Timenotifier.Services.Assistant;

public sealed record AssistantSessionLoadResult(
    IReadOnlyList<AssistantSession> Sessions,
    IReadOnlyList<string> Warnings);

public interface IAssistantSessionStore
{
    Task SaveAsync(AssistantSession session, CancellationToken cancellationToken = default);
    Task<AssistantSession?> LoadAsync(string id, CancellationToken cancellationToken = default);
    Task<AssistantSessionLoadResult> LoadAllAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}

public sealed class AssistantSessionStore(AppDataPaths paths) : IAssistantSessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Timenotifier.AssistantSessions.v1");
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly SemaphoreSlim _storeLock = new(1, 1);

    public async Task SaveAsync(AssistantSession session, CancellationToken cancellationToken = default)
    {
        ValidateId(session.Id);
        await _storeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(paths.AssistantSessionsDirectory);
            session.UpdatedAt = DateTime.Now;
            var persistedSession = CreatePersistedSession(session);
            var json = JsonSerializer.SerializeToUtf8Bytes(persistedSession, JsonOptions);
            var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
            var destination = SessionPath(session.Id);
            var temporary = Path.Combine(
                paths.AssistantSessionsDirectory,
                $"{session.Id}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken);
                if (File.Exists(destination))
                {
                    File.Replace(temporary, destination, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporary, destination);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public async Task<AssistantSession?> LoadAsync(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        await _storeLock.WaitAsync(cancellationToken);
        try
        {
            return await LoadFileAsync(SessionPath(id), cancellationToken);
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public async Task<AssistantSessionLoadResult> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        await _storeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(paths.AssistantSessionsDirectory);
            List<AssistantSession> sessions = [];
            List<string> warnings = [];
            foreach (var file in Directory.EnumerateFiles(paths.AssistantSessionsDirectory, "*.bin"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = await LoadFileAsync(file, cancellationToken);
                if (session is null)
                {
                    warnings.Add("无法读取一个助手会话，已安全跳过。");
                    continue;
                }

                sessions.Add(session);
            }

            return new AssistantSessionLoadResult(
                sessions.OrderByDescending(session => session.UpdatedAt).ToList(),
                warnings);
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        await _storeLock.WaitAsync(cancellationToken);
        try
        {
            var path = SessionPath(id);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            _storeLock.Release();
        }
    }

    private async Task<AssistantSession?> LoadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
            var json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var session = JsonSerializer.Deserialize<AssistantSession>(json, JsonOptions);
            if (!IsValidSession(session, path))
            {
                return null;
            }

            return session;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string SessionPath(string id)
    {
        return Path.Combine(paths.AssistantSessionsDirectory, $"{id}.bin");
    }

    private static AssistantSession CreatePersistedSession(AssistantSession session)
    {
        return new AssistantSession
        {
            Id = session.Id,
            Title = session.Title,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
            Messages = session.Messages.Select(message => new AssistantMessage
            {
                Id = message.Id,
                Role = message.Role,
                Text = message.Text,
                CreatedAt = message.CreatedAt,
                Attachments = message.Attachments.Select(attachment => new AssistantAttachmentMetadata
                {
                    Name = Path.GetFileName(attachment.Name),
                    MediaType = attachment.MediaType,
                    SizeBytes = attachment.SizeBytes
                }).ToList(),
                Action = message.Action is null
                    ? null
                    : new AssistantActionRecord
                    {
                        ProposalId = message.Action.ProposalId,
                        Name = message.Action.Name,
                        Summary = message.Action.Summary,
                        Risk = message.Action.Risk,
                        Status = message.Action.Status
                    }
            }).ToList()
        };
    }

    private static bool IsValidSession(AssistantSession? session, string path)
    {
        return session is not null
            && Guid.TryParseExact(session.Id, "N", out _)
            && string.Equals(
                Path.GetFileNameWithoutExtension(path),
                session.Id,
                StringComparison.OrdinalIgnoreCase)
            && session.Messages is not null
            && session.Messages.All(message => message is not null
                && message.Attachments is not null);
    }

    private static void ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw new ArgumentException("助手会话 ID 无效。", nameof(id));
        }
    }
}
