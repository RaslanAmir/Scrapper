using System.Collections.Generic;
using WcScraper.Wpf.Models;
using WcScraper.Wpf.Services;
using Xunit;

namespace WcScraper.Wpf.Tests;

public static class ChatAssistantServiceTests
{
    [Fact]
    public static void ParseAssistantDirectivePayload_WithValidResponse_ReturnsBatch()
    {
        const string payload = """```
{
  \"summary\": \"Enable design snapshot\",
  \"changes\": [
    {
      \"type\": \"toggle\",
      \"name\": \"ExportPublicDesignSnapshot\",
      \"value\": true,
      \"justification\": \"Operator requested a theme snapshot.\",
      \"risk_level\": \"medium\",
      \"confidence\": 0.92,
      \"requires_confirmation\": true
    }
  ],
  \"retry\": {
    \"enable\": \"true\",
    \"attempts\": \"5\",
    \"base_delay_seconds\": \"2.5\",
    \"max_delay_seconds\": 45,
    \"justification\": \"Increase resilience during scraping.\"
  },
  \"credential_reminders\": [
    {
      \"credential\": \"WordPress\",
      \"message\": \"Ask for admin credentials before enabling this export.\"
    }
  ],
  \"requires_confirmation\": true,
  \"risk_note\": \"Capturing public design snapshots increases runtime.\"
}
```""";

        var result = ChatAssistantService.ParseAssistantDirectivePayload(payload);
        Assert.NotNull(result);
        Assert.Equal("Enable design snapshot", result!.Summary);
        Assert.True(result.RequiresConfirmation);
        Assert.Equal("Capturing public design snapshots increases runtime.", result.RiskNote);

        var toggle = Assert.Single(result.Toggles);
        Assert.Equal("ExportPublicDesignSnapshot", toggle.Name);
        Assert.True(toggle.Value);
        Assert.True(toggle.RequiresConfirmation);
        Assert.Equal("medium", toggle.RiskLevel);
        Assert.NotNull(toggle.Confidence);
        Assert.Equal(0.92, toggle.Confidence!.Value, precision: 2);
        Assert.Equal("Operator requested a theme snapshot.", toggle.Justification);

        Assert.NotNull(result.Retry);
        Assert.True(result.Retry!.Enable);
        Assert.Equal(5, result.Retry!.Attempts);
        Assert.NotNull(result.Retry!.BaseDelaySeconds);
        Assert.Equal(2.5, result.Retry!.BaseDelaySeconds!.Value, precision: 2);
        Assert.Equal(45, result.Retry!.MaxDelaySeconds);
        Assert.Equal("Increase resilience during scraping.", result.Retry!.Justification);

        var reminder = Assert.Single(result.CredentialReminders);
        Assert.Equal("WordPress", reminder.Credential);
        Assert.Equal("Ask for admin credentials before enabling this export.", reminder.Message);
    }

    [Fact]
    public static void ParseAssistantDirectivePayload_WithInvalidJson_ReturnsNull()
    {
        const string payload = "not-json";
        var result = ChatAssistantService.ParseAssistantDirectivePayload(payload);
        Assert.Null(result);
    }

    [Fact]
    public static void ParseAssistantDirectivePayload_IgnoresInvalidToggleButKeepsReminders()
    {
        const string payload = """
{
  \"summary\": \"Reminder only\",
  \"changes\": [
    { \"type\": \"toggle\", \"name\": \"ExportCsv\", \"value\": \"maybe\" }
  ],
  \"credential_reminders\": [
    { \"message\": \"Provide storefront token.\" }
  ]
}
""";

        var result = ChatAssistantService.ParseAssistantDirectivePayload(payload);
        Assert.NotNull(result);
        Assert.Empty(result!.Toggles);
        var reminder = Assert.Single(result.CredentialReminders);
        Assert.Equal("General", reminder.Credential);
        Assert.Equal("Provide storefront token.", reminder.Message);
    }

    [Fact]
    public static void ReportUsage_WithSnapshot_NotifiesListener()
    {
        var snapshots = new List<ChatUsageSnapshot>();
        var settings = new ChatSessionSettings(
            "https://example.invalid",
            "key",
            "model",
            systemPrompt: null,
            UsageReported: snapshots.Add);

        var usage = new ChatUsageSnapshot(10, 5, 15);
        ChatAssistantService.ReportUsage(settings, usage);

        var captured = Assert.Single(snapshots);
        Assert.Equal(usage, captured);
        Assert.Equal(10, settings.ConsumedPromptTokens);
        Assert.Equal(5, settings.ConsumedCompletionTokens);
        Assert.Equal(15, settings.ConsumedTotalTokens);
        Assert.Equal(0m, settings.ConsumedCostUsd);
    }

    [Fact]
    public static void ReportUsage_WithMultipleSnapshots_NotifiesEachListenerInvocation()
    {
        var snapshots = new List<ChatUsageSnapshot>();
        var settings = new ChatSessionSettings(
            "https://example.invalid",
            "key",
            "model",
            systemPrompt: null,
            UsageReported: snapshots.Add);

        ChatAssistantService.ReportUsage(settings, new ChatUsageSnapshot(5, 3, 8));
        ChatAssistantService.ReportUsage(settings, new ChatUsageSnapshot(7, 2, 9));

        Assert.Equal(2, snapshots.Count);
        Assert.Contains(new ChatUsageSnapshot(5, 3, 8), snapshots);
        Assert.Contains(new ChatUsageSnapshot(7, 2, 9), snapshots);
        Assert.Equal(12, settings.ConsumedPromptTokens);
        Assert.Equal(5, settings.ConsumedCompletionTokens);
        Assert.Equal(17, settings.ConsumedTotalTokens);
        Assert.Equal(0m, settings.ConsumedCostUsd);
    }

    [Fact]
    public static void ReportUsage_WithPricing_TracksCost()
    {
        var settings = new ChatSessionSettings(
            "https://example.invalid",
            "key",
            "model",
            systemPrompt: null,
            PromptTokenCostPerThousandUsd: 0.5m,
            CompletionTokenCostPerThousandUsd: 1.5m);

        ChatAssistantService.ReportUsage(settings, new ChatUsageSnapshot(2_000, 1_000, 3_000));

        Assert.Equal(2_000, settings.ConsumedPromptTokens);
        Assert.Equal(1_000, settings.ConsumedCompletionTokens);
        Assert.Equal(3_000, settings.ConsumedTotalTokens);
        Assert.Equal(2.5m, settings.ConsumedCostUsd);
    }
}
