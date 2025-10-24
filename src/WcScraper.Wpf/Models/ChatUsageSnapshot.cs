namespace WcScraper.Wpf.Models;

public sealed record ChatUsageSnapshot(int PromptTokens, int CompletionTokens, int TotalTokens);
