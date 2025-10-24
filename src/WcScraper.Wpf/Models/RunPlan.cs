using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace WcScraper.Wpf.Models;

public enum RunPlanExecutionMode
{
    Immediate,
    Scheduled,
}

public enum RunPlanStatus
{
    Pending,
    Scheduled,
    Running,
    Completed,
    Failed,
    Skipped,
}

public enum RunPlanSettingValueKind
{
    Boolean,
    Number,
    Text,
}

public sealed record RunPlanSettingOverride(
    string Name,
    RunPlanSettingValueKind ValueKind,
    bool? BooleanValue,
    double? NumberValue,
    string? TextValue,
    string? Description)
{
    public string DisplayValue => ValueKind switch
    {
        RunPlanSettingValueKind.Boolean when BooleanValue is bool value => value ? "true" : "false",
        RunPlanSettingValueKind.Number when NumberValue is double number => number.ToString("0.###", CultureInfo.InvariantCulture),
        RunPlanSettingValueKind.Text when !string.IsNullOrWhiteSpace(TextValue) => TextValue!,
        _ => "(unspecified)",
    };
}

public sealed record RunPlanExecutionOutcome(bool Success, string? Message);

public sealed record RunPlanSnapshot(
    Guid Id,
    string Name,
    RunPlanExecutionMode ExecutionMode,
    RunPlanStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ScheduledForUtc,
    DateTimeOffset? ExecutedAtUtc,
    int? ExecutionOrder,
    string? DirectiveSummary,
    string? ResultNote,
    IReadOnlyList<RunPlanSettingOverride> Settings,
    IReadOnlyList<string> PrerequisiteNotes);

public sealed class RunPlan : INotifyPropertyChanged
{
    private RunPlanStatus _status;
    private DateTimeOffset? _executedAtUtc;
    private string? _resultNote;

    public RunPlan(
        Guid id,
        string name,
        RunPlanExecutionMode executionMode,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? scheduledForUtc,
        IReadOnlyList<RunPlanSettingOverride> settings,
        IReadOnlyList<string> prerequisiteNotes,
        string? directiveSummary,
        int? executionOrder)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Plan name is required.", nameof(name));
        }

        Id = id;
        Name = name.Trim();
        ExecutionMode = executionMode;
        CreatedAtUtc = createdAtUtc;
        ScheduledForUtc = scheduledForUtc;
        DirectiveSummary = string.IsNullOrWhiteSpace(directiveSummary) ? null : directiveSummary.Trim();
        ExecutionOrder = executionOrder;

        Settings = new ReadOnlyCollection<RunPlanSettingOverride>(settings?.ToArray() ?? Array.Empty<RunPlanSettingOverride>());
        PrerequisiteNotes = new ReadOnlyCollection<string>(prerequisiteNotes?.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray() ?? Array.Empty<string>());

        _status = RunPlanStatus.Pending;
    }

    public Guid Id { get; }

    public string Name { get; }

    public RunPlanExecutionMode ExecutionMode { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? ScheduledForUtc { get; }

    public IReadOnlyList<RunPlanSettingOverride> Settings { get; }

    public IReadOnlyList<string> PrerequisiteNotes { get; }

    public string? DirectiveSummary { get; }

    public int? ExecutionOrder { get; }

    public RunPlanStatus Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
        }
    }

    public DateTimeOffset? ExecutedAtUtc
    {
        get => _executedAtUtc;
        set
        {
            if (_executedAtUtc == value)
            {
                return;
            }

            _executedAtUtc = value;
            OnPropertyChanged();
        }
    }

    public string? ResultNote
    {
        get => _resultNote;
        set
        {
            if (string.Equals(_resultNote, value, StringComparison.Ordinal))
            {
                return;
            }

            _resultNote = value;
            OnPropertyChanged();
        }
    }

    public RunPlanSnapshot CreateSnapshot()
    {
        return new RunPlanSnapshot(
            Id,
            Name,
            ExecutionMode,
            Status,
            CreatedAtUtc,
            ScheduledForUtc,
            ExecutedAtUtc,
            ExecutionOrder,
            DirectiveSummary,
            ResultNote,
            Settings.ToArray(),
            PrerequisiteNotes.ToArray());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
