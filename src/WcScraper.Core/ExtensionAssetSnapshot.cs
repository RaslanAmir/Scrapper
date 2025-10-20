using System;
using System.Collections.Generic;
using System.Linq;

namespace WcScraper.Core;

public sealed class ExtensionAssetSnapshot
{
    public ExtensionAssetSnapshot(string? manifestJson, IEnumerable<string>? paths)
    {
        ManifestJson = manifestJson;
        Paths = paths?.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                ?? new List<string>();
    }

    public string? ManifestJson { get; }

    public List<string> Paths { get; }
}
