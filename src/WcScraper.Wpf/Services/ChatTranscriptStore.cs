using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WcScraper.Wpf.Models;

namespace WcScraper.Wpf.Services;

internal enum ChatTranscriptFormat
{
    Jsonl,
    Markdown,
}

internal sealed record ChatTranscriptSession(
    string SessionId,
    DateTimeOffset CreatedAtUtc,
    string JsonlPath,
    string MarkdownPath,
    IReadOnlyList<ChatMessage> Messages);

internal sealed class ChatTranscriptStore
{
    private readonly string _transcriptDirectory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _appendLock = new(1, 1);
    private readonly object _sessionSync = new();
    private string? _currentSessionId;

    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public ChatTranscriptStore(string settingsDirectory)
        : this(settingsDirectory, () => DateTimeOffset.UtcNow)
    {
    }

    internal ChatTranscriptStore(string settingsDirectory, Func<DateTimeOffset> clock)
    {
        if (string.IsNullOrWhiteSpace(settingsDirectory))
        {
            throw new ArgumentException("A settings directory is required.", nameof(settingsDirectory));
        }

        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _transcriptDirectory = Path.Combine(settingsDirectory, "chat-transcripts");
    }

    public string TranscriptDirectory => _transcriptDirectory;

    public string CurrentJsonlPath
    {
        get
        {
            Directory.CreateDirectory(_transcriptDirectory);
            return Path.Combine(_transcriptDirectory, GetCurrentSessionId() + ".jsonl");
        }
    }

    public string CurrentMarkdownPath
    {
        get
        {
            Directory.CreateDirectory(_transcriptDirectory);
            return Path.Combine(_transcriptDirectory, GetCurrentSessionId() + ".md");
        }
    }

    public void StartNewSession()
    {
        lock (_sessionSync)
        {
            _currentSessionId = CreateSessionIdentifier(_clock());
        }
    }

    public async Task AppendAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var entry = new ChatTranscriptEntry(_clock(), message.Role.ToString(), message.Content ?? string.Empty);
        var jsonLine = JsonSerializer.Serialize(entry, s_jsonOptions);
        var markdown = BuildMarkdown(entry);

        var jsonPath = CurrentJsonlPath;
        var markdownPath = CurrentMarkdownPath;

        await _appendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(jsonPath, jsonLine + Environment.NewLine, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            await File.AppendAllTextAsync(markdownPath, markdown, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _appendLock.Release();
        }
    }

    public async Task<ChatTranscriptSession?> LoadMostRecentTranscriptAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_transcriptDirectory))
        {
            Directory.CreateDirectory(_transcriptDirectory);
            StartNewSession();
            return null;
        }

        string? latestJson = Directory.EnumerateFiles(_transcriptDirectory, "*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(latestJson))
        {
            StartNewSession();
            return null;
        }

        var sessionId = Path.GetFileNameWithoutExtension(latestJson)!;
        var createdAtUtc = File.GetCreationTimeUtc(latestJson);
        var createdAt = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);
        var markdownPath = Path.ChangeExtension(latestJson, ".md");

        var messages = new List<ChatMessage>();

        try
        {
            using var stream = new FileStream(latestJson, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ChatTranscriptEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<ChatTranscriptEntry>(line, s_jsonOptions);
                }
                catch
                {
                    continue;
                }

                if (entry is null)
                {
                    continue;
                }

                var role = ParseRole(entry.Role);
                messages.Add(new ChatMessage(role, entry.Content));
            }
        }
        catch
        {
            // If the transcript cannot be read, start a fresh session.
            StartNewSession();
            return null;
        }

        lock (_sessionSync)
        {
            _currentSessionId = sessionId;
        }

        return new ChatTranscriptSession(
            sessionId,
            new DateTimeOffset(createdAt),
            latestJson,
            markdownPath,
            messages);
    }

    public async Task SaveTranscriptAsync(string targetPath, ChatTranscriptFormat format, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("A target path is required.", nameof(targetPath));
        }

        var source = format == ChatTranscriptFormat.Jsonl ? CurrentJsonlPath : CurrentMarkdownPath;
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        await _appendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(source))
            {
                // Nothing to copy yet.
                await File.WriteAllTextAsync(source, string.Empty, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }

            using var sourceStream = new FileStream(source, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
            using var destinationStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await sourceStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _appendLock.Release();
        }
    }

    private string GetCurrentSessionId()
    {
        lock (_sessionSync)
        {
            _currentSessionId ??= CreateSessionIdentifier(_clock());
            return _currentSessionId;
        }
    }

    private static string CreateSessionIdentifier(DateTimeOffset timestamp)
        => $"chat-{timestamp.UtcDateTime:yyyyMMdd-HHmmss}Z";

    private static ChatMessageRole ParseRole(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse<ChatMessageRole>(value, ignoreCase: true, out var role))
        {
            return role;
        }

        return ChatMessageRole.Assistant;
    }

    private static string BuildMarkdown(ChatTranscriptEntry entry)
    {
        var builder = new StringBuilder();
        builder.Append("### ");
        builder.Append(entry.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
        builder.Append(" UTC — ");
        builder.Append(entry.Role);
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine(entry.Content?.TrimEnd() ?? string.Empty);
        builder.AppendLine();
        return builder.ToString();
    }

    private sealed record ChatTranscriptEntry(DateTimeOffset TimestampUtc, string Role, string Content);
}
