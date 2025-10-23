using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WcScraper.Wpf.Models;

namespace WcScraper.Wpf.Services;

public interface IArtifactIndexingService
{
    event EventHandler? IndexChanged;

    bool HasAnyIndexedArtifacts { get; }

    int IndexedDatasetCount { get; }

    void ResetForRun(string storeIdentifier, string runIdentifier);

    Task IndexArtifactAsync(string filePath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArtifactSearchResult>> SearchAsync(string query, int take, CancellationToken cancellationToken = default);

    IReadOnlyList<AiIndexedDatasetReference> GetIndexedDatasets();
}

public sealed record ArtifactSearchResult(
    string DatasetName,
    string FilePath,
    string VectorIndexId,
    int RowNumber,
    string Snippet,
    double Score);

public sealed class ArtifactIndexingService : IArtifactIndexingService
{
    private const int MaxRowsPerDataset = 2048;
    private const int MaxSnippetLength = 700;

    private readonly ReaderWriterLockSlim _lock = new();
    private readonly Dictionary<string, ArtifactDataset> _datasets = new(StringComparer.OrdinalIgnoreCase);

    private string _storeIdentifier = string.Empty;
    private string _runIdentifier = string.Empty;

    public event EventHandler? IndexChanged;

    public bool HasAnyIndexedArtifacts
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _datasets.Count > 0;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public int IndexedDatasetCount
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _datasets.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public void ResetForRun(string storeIdentifier, string runIdentifier)
    {
        _lock.EnterWriteLock();
        try
        {
            _datasets.Clear();
            _storeIdentifier = storeIdentifier ?? string.Empty;
            _runIdentifier = runIdentifier ?? string.Empty;
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        OnIndexChanged();
    }

    public async Task IndexArtifactAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var extension = Path.GetExtension(filePath);
        if (!string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!File.Exists(filePath))
        {
            return;
        }

        ArtifactDataset? dataset = null;

        if (string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
        {
            dataset = await BuildCsvDatasetAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(extension, ".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            dataset = await BuildJsonlDatasetAsync(filePath, cancellationToken).ConfigureAwait(false);
        }

        if (dataset is null || dataset.Chunks.Count == 0)
        {
            return;
        }

        _lock.EnterWriteLock();
        try
        {
            _datasets[filePath] = dataset;
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        OnIndexChanged();
    }

    public async Task<IReadOnlyList<ArtifactSearchResult>> SearchAsync(string query, int take, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || take <= 0)
        {
            return Array.Empty<ArtifactSearchResult>();
        }

        var vector = BuildVector(query);
        if (vector.Magnitude <= 0)
        {
            return Array.Empty<ArtifactSearchResult>();
        }

        List<ArtifactDataset> snapshot;

        _lock.EnterReadLock();
        try
        {
            if (_datasets.Count == 0)
            {
                return Array.Empty<ArtifactSearchResult>();
            }

            snapshot = _datasets.Values.ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }

        var scores = new List<ArtifactSearchResult>();

        foreach (var dataset in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var chunk in dataset.Chunks)
            {
                var score = ComputeCosineSimilarity(vector, chunk.Vector);
                if (score <= 0)
                {
                    continue;
                }

                scores.Add(new ArtifactSearchResult(
                    dataset.DatasetName,
                    dataset.FilePath,
                    dataset.VectorIndexId,
                    chunk.RowNumber,
                    chunk.Snippet,
                    score));
            }
        }

        if (scores.Count == 0)
        {
            return Array.Empty<ArtifactSearchResult>();
        }

        return scores
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.DatasetName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.RowNumber)
            .Take(take)
            .ToList();
    }

    public IReadOnlyList<AiIndexedDatasetReference> GetIndexedDatasets()
    {
        _lock.EnterReadLock();
        try
        {
            if (_datasets.Count == 0)
            {
                return Array.Empty<AiIndexedDatasetReference>();
            }

            return _datasets.Values
                .OrderBy(dataset => dataset.DatasetName, StringComparer.OrdinalIgnoreCase)
                .Select(dataset => new AiIndexedDatasetReference(dataset.DatasetName, dataset.SchemaHighlights, dataset.VectorIndexId))
                .ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private void OnIndexChanged()
        => IndexChanged?.Invoke(this, EventArgs.Empty);

    private async Task<ArtifactDataset?> BuildCsvDatasetAsync(string filePath, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var headerLine = await reader.ReadLineAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            return null;
        }

        var headers = ParseCsvLine(headerLine);
        if (headers.Count == 0)
        {
            return null;
        }

        var schemaHighlights = headers
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Take(8)
            .Select(h => h.Trim())
            .ToList();

        var chunks = new List<ArtifactChunk>();
        var rowNumber = 0;
        string? line;

        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            rowNumber++;
            if (rowNumber > MaxRowsPerDataset)
            {
                break;
            }

            var values = ParseCsvLine(line);
            if (values.Count == 0)
            {
                continue;
            }

            var snippet = BuildCsvSnippet(rowNumber, headers, values);
            var vector = BuildVector(snippet);
            if (vector.Magnitude <= 0)
            {
                continue;
            }

            chunks.Add(new ArtifactChunk(rowNumber, snippet, vector));
        }

        if (chunks.Count == 0)
        {
            return null;
        }

        return new ArtifactDataset(
            filePath,
            Path.GetFileName(filePath),
            BuildVectorIndexId(filePath),
            schemaHighlights,
            chunks);
    }

    private async Task<ArtifactDataset?> BuildJsonlDatasetAsync(string filePath, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (lines.Length == 0)
        {
            return null;
        }

        var schema = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chunks = new List<ArtifactChunk>();
        var rowNumber = 0;

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            rowNumber++;
            if (rowNumber > MaxRowsPerDataset)
            {
                break;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var snippet = BuildJsonlSnippet(rowNumber, document.RootElement, schema);
                var vector = BuildVector(snippet);
                if (vector.Magnitude <= 0)
                {
                    continue;
                }

                chunks.Add(new ArtifactChunk(rowNumber, snippet, vector));
            }
            catch (JsonException)
            {
                // Skip malformed JSON rows.
            }
        }

        if (chunks.Count == 0)
        {
            return null;
        }

        var schemaHighlights = schema.Take(8).ToList();

        return new ArtifactDataset(
            filePath,
            Path.GetFileName(filePath),
            BuildVectorIndexId(filePath),
            schemaHighlights,
            chunks);
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        if (line is null)
        {
            return values;
        }

        var builder = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        builder.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    builder.Append(ch);
                }
            }
            else
            {
                if (ch == '"')
                {
                    inQuotes = true;
                }
                else if (ch == ',')
                {
                    values.Add(builder.ToString());
                    builder.Clear();
                }
                else
                {
                    builder.Append(ch);
                }
            }
        }

        values.Add(builder.ToString());
        return values;
    }

    private static string BuildCsvSnippet(int rowNumber, IReadOnlyList<string> headers, IReadOnlyList<string> values)
    {
        var builder = new StringBuilder();
        builder.Append("Row ");
        builder.Append(rowNumber);
        builder.Append(' ');

        for (var i = 0; i < headers.Count && i < values.Count; i++)
        {
            var header = headers[i];
            var value = values[i];
            if (string.IsNullOrWhiteSpace(header) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (builder.Length > 10)
            {
                builder.Append("; ");
            }

            builder.Append(header.Trim());
            builder.Append(':');
            builder.Append(' ');
            builder.Append(value.Trim());
        }

        return Truncate(builder.ToString());
    }

    private static string BuildJsonlSnippet(int rowNumber, JsonElement element, HashSet<string> schema)
    {
        var builder = new StringBuilder();
        builder.Append("Entry ");
        builder.Append(rowNumber);

        var pairs = new List<string>();
        BuildJsonPairs(element, pairs, schema, prefix: string.Empty);

        foreach (var pair in pairs)
        {
            builder.Append("; ");
            builder.Append(pair);
        }

        return Truncate(builder.ToString());
    }

    private static void BuildJsonPairs(JsonElement element, List<string> pairs, HashSet<string> schema, string prefix)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var key = string.IsNullOrEmpty(prefix)
                        ? property.Name
                        : string.Concat(prefix, ".", property.Name);
                    BuildJsonPairs(property.Value, pairs, schema, key);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var key = string.Concat(prefix, "[", index++, "]");
                    BuildJsonPairs(item, pairs, schema, key);
                }

                break;
            default:
                if (string.IsNullOrWhiteSpace(prefix))
                {
                    return;
                }

                schema.Add(prefix);
                var value = element.ToString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                pairs.Add(string.Concat(prefix, ": ", value));
                break;
        }
    }

    private static string Truncate(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= MaxSnippetLength)
        {
            return value;
        }

        return value[..MaxSnippetLength] + "…";
    }

    private string BuildVectorIndexId(string filePath)
    {
        var store = string.IsNullOrWhiteSpace(_storeIdentifier) ? "store" : _storeIdentifier;
        var run = string.IsNullOrWhiteSpace(_runIdentifier) ? "run" : _runIdentifier;
        var normalized = Path.GetFullPath(filePath ?? string.Empty);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hash = Convert.ToHexString(hashBytes.AsSpan(0, 8)).ToLowerInvariant();
        return string.Concat(store, ":", run, ":", hash);
    }

    private static VectorRepresentation BuildVector(string text)
    {
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new VectorRepresentation(weights, 0);
        }

        var tokens = Tokenize(text);
        foreach (var token in tokens)
        {
            if (weights.TryGetValue(token, out var existing))
            {
                weights[token] = existing + 1;
            }
            else
            {
                weights[token] = 1;
            }
        }

        double magnitude = 0;
        foreach (var value in weights.Values)
        {
            magnitude += value * value;
        }

        magnitude = Math.Sqrt(magnitude);

        return new VectorRepresentation(weights, magnitude);
    }

    private static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        var builder = new StringBuilder();

        void Flush()
        {
            if (builder.Length >= 2)
            {
                tokens.Add(builder.ToString());
            }

            builder.Clear();
        }

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return tokens;
    }

    private static double ComputeCosineSimilarity(VectorRepresentation query, VectorRepresentation chunk)
    {
        if (query.Magnitude <= 0 || chunk.Magnitude <= 0)
        {
            return 0;
        }

        double dot = 0;
        foreach (var kvp in query.Weights)
        {
            if (chunk.Weights.TryGetValue(kvp.Key, out var weight))
            {
                dot += kvp.Value * weight;
            }
        }

        if (dot <= 0)
        {
            return 0;
        }

        return dot / (query.Magnitude * chunk.Magnitude);
    }

    private sealed record ArtifactDataset(
        string FilePath,
        string DatasetName,
        string VectorIndexId,
        IReadOnlyList<string> SchemaHighlights,
        IReadOnlyList<ArtifactChunk> Chunks);

    private sealed record ArtifactChunk(int RowNumber, string Snippet, VectorRepresentation Vector);

    private sealed record VectorRepresentation(Dictionary<string, double> Weights, double Magnitude);
}
