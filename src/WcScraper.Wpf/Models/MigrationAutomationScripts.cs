using System;
using System.Collections.Generic;

namespace WcScraper.Wpf.Models;

public sealed record MigrationAutomationScript(
    string Name,
    string? Description,
    string Language,
    string Content,
    string? FileName,
    IReadOnlyList<string> Notes);

public sealed record MigrationScriptGenerationResult(
    string? Summary,
    IReadOnlyList<MigrationAutomationScript> Scripts,
    IReadOnlyList<string> Warnings,
    string? Error)
{
    public static readonly MigrationScriptGenerationResult Empty = new(
        null,
        Array.Empty<MigrationAutomationScript>(),
        Array.Empty<string>(),
        null);
}
