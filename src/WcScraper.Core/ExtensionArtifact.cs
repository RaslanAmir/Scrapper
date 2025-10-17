using System;
using System.IO;

namespace WcScraper.Core;

public sealed class ExtensionArtifact
{
    public ExtensionArtifact(string slug, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            throw new ArgumentException("Slug is required.", nameof(slug));
        }

        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Directory path is required.", nameof(directoryPath));
        }

        Slug = slug;
        DirectoryPath = directoryPath;
    }

    public string Slug { get; }
    public string DirectoryPath { get; }

    public string OptionsPath => Path.Combine(DirectoryPath, "options.json");
    public string ManifestPath => Path.Combine(DirectoryPath, "manifest.json");
    public string ArchivePath => Path.Combine(DirectoryPath, "archive.zip");
}
