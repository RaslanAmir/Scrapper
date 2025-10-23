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
}
