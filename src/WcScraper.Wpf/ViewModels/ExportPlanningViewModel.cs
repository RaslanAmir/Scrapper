using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using WcScraper.Wpf.Models;
using WcScraper.Wpf.Services;

namespace WcScraper.Wpf.ViewModels;

public sealed class ExportPlanningViewModel : INotifyPropertyChanged
{
    private readonly RunPlanner _runPlanner;
    private readonly ReadOnlyObservableCollection<RunPlan> _runPlans;
    private readonly IDictionary<string, (Func<bool> Getter, Action<bool> Setter)> _assistantToggleBindings;
    private readonly Func<int> _getHttpRetryAttempts;
    private readonly Action<int> _setHttpRetryAttempts;
    private readonly Func<double> _getHttpRetryBaseDelaySeconds;
    private readonly Action<double> _setHttpRetryBaseDelaySeconds;
    private readonly Func<double> _getHttpRetryMaxDelaySeconds;
    private readonly Action<double> _setHttpRetryMaxDelaySeconds;
    private readonly Action<string> _setManualRunGoals;
    private readonly Func<CancellationToken> _prepareRunCancellationToken;
    private readonly Func<CancellationToken, Task> _runAsync;
    private readonly Action<string> _append;
    private RunPlan? _activeRunPlan;
    private RunPlanExecutionOutcome? _activeRunPlanOutcome;

    public ExportPlanningViewModel(
        Dispatcher dispatcher,
        IDictionary<string, (Func<bool> Getter, Action<bool> Setter)> assistantToggleBindings,
        Func<int> getHttpRetryAttempts,
        Action<int> setHttpRetryAttempts,
        Func<double> getHttpRetryBaseDelaySeconds,
        Action<double> setHttpRetryBaseDelaySeconds,
        Func<double> getHttpRetryMaxDelaySeconds,
        Action<double> setHttpRetryMaxDelaySeconds,
        Action<string> setManualRunGoals,
        Func<CancellationToken> prepareRunCancellationToken,
        Func<CancellationToken, Task> runAsync,
        Action<string> append)
    {
        if (dispatcher is null)
        {
            throw new ArgumentNullException(nameof(dispatcher));
        }

        _assistantToggleBindings = assistantToggleBindings ?? throw new ArgumentNullException(nameof(assistantToggleBindings));
        _getHttpRetryAttempts = getHttpRetryAttempts ?? throw new ArgumentNullException(nameof(getHttpRetryAttempts));
        _setHttpRetryAttempts = setHttpRetryAttempts ?? throw new ArgumentNullException(nameof(setHttpRetryAttempts));
        _getHttpRetryBaseDelaySeconds = getHttpRetryBaseDelaySeconds ?? throw new ArgumentNullException(nameof(getHttpRetryBaseDelaySeconds));
        _setHttpRetryBaseDelaySeconds = setHttpRetryBaseDelaySeconds ?? throw new ArgumentNullException(nameof(setHttpRetryBaseDelaySeconds));
        _getHttpRetryMaxDelaySeconds = getHttpRetryMaxDelaySeconds ?? throw new ArgumentNullException(nameof(getHttpRetryMaxDelaySeconds));
        _setHttpRetryMaxDelaySeconds = setHttpRetryMaxDelaySeconds ?? throw new ArgumentNullException(nameof(setHttpRetryMaxDelaySeconds));
        _setManualRunGoals = setManualRunGoals ?? throw new ArgumentNullException(nameof(setManualRunGoals));
        _prepareRunCancellationToken = prepareRunCancellationToken ?? throw new ArgumentNullException(nameof(prepareRunCancellationToken));
        _runAsync = runAsync ?? throw new ArgumentNullException(nameof(runAsync));
        _append = append ?? throw new ArgumentNullException(nameof(append));

        _runPlanner = new RunPlanner(ExecuteRunPlanAsync, dispatcher);
        _runPlans = _runPlanner.Plans;
        ((INotifyCollectionChanged)_runPlans).CollectionChanged += OnRunPlansChanged;
        _runPlanner.PlanQueued += OnRunPlanQueued;
        _runPlanner.PlanExecutionCompleted += OnRunPlanExecutionCompleted;

        ApproveRunPlanCommand = new RelayCommand<RunPlan>(OnApproveRunPlan, CanApproveRunPlan);
        DismissRunPlanCommand = new RelayCommand<RunPlan>(OnDismissRunPlan, CanDismissRunPlan);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ReadOnlyObservableCollection<RunPlan> RunPlans => _runPlans;

    public RelayCommand<RunPlan> ApproveRunPlanCommand { get; }

    public RelayCommand<RunPlan> DismissRunPlanCommand { get; }

    public bool HasRunPlans => RunPlans.Count > 0;

    public bool HasActiveRunPlan => _activeRunPlan is not null;

    public RunPlan? ActiveRunPlan => _activeRunPlan;

    public void EnqueuePlan(RunPlan plan)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        _runPlanner.Enqueue(plan);
    }

    public IReadOnlyList<RunPlanSnapshot> CreateSnapshot(RunPlanExecutionOutcome? overrideOutcome)
        => _runPlanner.CreateSnapshot(_activeRunPlan, overrideOutcome);

    public void SetActiveRunPlanOutcome(RunPlanExecutionOutcome outcome)
    {
        if (outcome is null)
        {
            throw new ArgumentNullException(nameof(outcome));
        }

        if (_activeRunPlan is null)
        {
            return;
        }

        _activeRunPlanOutcome ??= outcome;
    }

    public string BuildRunPlanConfirmationPrompt(RunPlan plan)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        if (plan.ExecutionMode == RunPlanExecutionMode.Scheduled && plan.ScheduledForUtc is { } scheduled)
        {
            return $"Queue remediation plan \"{plan.Name}\" for {scheduled:u}?";
        }

        return $"Queue remediation plan \"{plan.Name}\" for approval?";
    }

    private async Task<RunPlanExecutionOutcome> ExecuteRunPlanAsync(RunPlan plan)
    {
        if (plan is null)
        {
            return new RunPlanExecutionOutcome(false, "Plan details were missing.");
        }

        _activeRunPlan = plan;
        _activeRunPlanOutcome = null;

        _append($"Run plan \"{plan.Name}\" initiated.");
        if (plan.PrerequisiteNotes.Count > 0)
        {
            foreach (var note in plan.PrerequisiteNotes)
            {
                _append($"  Prerequisite: {note}");
            }
        }

        ApplyRunPlanOverrides(plan);

        var cancellationToken = _prepareRunCancellationToken();

        RunPlanExecutionOutcome outcome;

        try
        {
            await _runAsync(cancellationToken).ConfigureAwait(false);
            outcome = _activeRunPlanOutcome ?? new RunPlanExecutionOutcome(true, "Run invoked.");
        }
        catch (OperationCanceledException)
        {
            _activeRunPlanOutcome ??= RunPlanExecutionOutcome.CreateCancelled("Run cancelled.");
            outcome = _activeRunPlanOutcome;
        }
        finally
        {
            _activeRunPlan = null;
            _activeRunPlanOutcome = null;
        }

        return outcome;
    }

    private void ApplyRunPlanOverrides(RunPlan plan)
    {
        foreach (var setting in plan.Settings)
        {
            if (!string.IsNullOrWhiteSpace(setting.Description))
            {
                _append($"Run plan note for {setting.Name}: {setting.Description}");
            }

            switch (setting.ValueKind)
            {
                case RunPlanSettingValueKind.Boolean when setting.BooleanValue is bool booleanValue:
                    if (_assistantToggleBindings.TryGetValue(setting.Name, out var binding))
                    {
                        var current = binding.Getter();
                        if (current == booleanValue)
                        {
                            _append($"Run plan left {setting.Name} unchanged (already {(booleanValue ? "enabled" : "disabled")}).");
                        }
                        else
                        {
                            binding.Setter(booleanValue);
                            _append($"Run plan set {setting.Name} to {(booleanValue ? "enabled" : "disabled") }.");
                        }
                    }
                    else
                    {
                        _append($"Run plan override skipped: toggle {setting.Name} is not recognized.");
                    }

                    break;
                case RunPlanSettingValueKind.Number when setting.NumberValue is double numericValue:
                    ApplyRunPlanNumericOverride(setting.Name, numericValue);
                    break;
                case RunPlanSettingValueKind.Text when !string.IsNullOrWhiteSpace(setting.TextValue):
                    ApplyRunPlanTextOverride(setting.Name, setting.TextValue!);
                    break;
                default:
                    _append($"Run plan override skipped: {setting.Name} value could not be interpreted.");
                    break;
            }
        }
    }

    private void ApplyRunPlanNumericOverride(string name, double value)
    {
        switch (name)
        {
            case nameof(MainViewModel.HttpRetryAttempts):
                var attempts = (int)Math.Round(value);
                if (attempts < 0 || attempts > 10)
                {
                    _append($"Run plan override skipped: {name} value {attempts} is outside the expected range (0-10).");
                    return;
                }

                if (_getHttpRetryAttempts() == attempts)
                {
                    _append($"Run plan left {name} at {attempts} (already set).");
                }
                else
                {
                    _setHttpRetryAttempts(attempts);
                    _append($"Run plan set {name} to {attempts}.");
                }

                break;
            case nameof(MainViewModel.HttpRetryBaseDelaySeconds):
                if (!IsDelayWithinRange(value))
                {
                    _append($"Run plan override skipped: {name} value {FormatSeconds(value)} is outside the expected range (0-600 seconds).");
                    return;
                }

                if (Math.Abs(_getHttpRetryBaseDelaySeconds() - value) < 0.0001)
                {
                    _append($"Run plan left {name} at {FormatSeconds(value)} (already set).");
                }
                else
                {
                    _setHttpRetryBaseDelaySeconds(value);
                    _append($"Run plan set {name} to {FormatSeconds(value)}.");
                }

                break;
            case nameof(MainViewModel.HttpRetryMaxDelaySeconds):
                if (!IsDelayWithinRange(value))
                {
                    _append($"Run plan override skipped: {name} value {FormatSeconds(value)} is outside the expected range (0-600 seconds).");
                    return;
                }

                if (Math.Abs(_getHttpRetryMaxDelaySeconds() - value) < 0.0001)
                {
                    _append($"Run plan left {name} at {FormatSeconds(value)} (already set).");
                }
                else
                {
                    _setHttpRetryMaxDelaySeconds(value);
                    _append($"Run plan set {name} to {FormatSeconds(value)}.");
                }

                break;
            default:
                _append($"Run plan override skipped: numeric setting {name} is not supported.");
                break;
        }
    }

    private void ApplyRunPlanTextOverride(string name, string value)
    {
        switch (name)
        {
            case nameof(MainViewModel.ManualRunGoals):
                _setManualRunGoals(value);
                _append("Run plan updated manual run goals.");
                break;
            default:
                _append($"Run plan override skipped: text setting {name} is not supported.");
                break;
        }
    }

    private void OnRunPlansChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(HasRunPlans));

    private void OnRunPlanQueued(object? sender, RunPlan plan)
    {
        if (plan is null)
        {
            return;
        }

        plan.PropertyChanged += OnRunPlanPropertyChanged;
        var scheduleText = plan.ExecutionMode == RunPlanExecutionMode.Scheduled && plan.ScheduledForUtc is { } scheduled
            ? $"scheduled for {scheduled:u}"
            : "awaiting approval";
        _append($"Assistant queued remediation plan \"{plan.Name}\" ({scheduleText}).");

        if (!string.IsNullOrWhiteSpace(plan.DirectiveSummary))
        {
            _append($"Summary: {plan.DirectiveSummary}");
        }

        if (plan.Settings.Count > 0)
        {
            var overrideSummary = string.Join(", ", plan.Settings.Select(setting => $"{setting.Name}={setting.DisplayValue}"));
            _append("Overrides: " + overrideSummary);
        }

        foreach (var note in plan.PrerequisiteNotes)
        {
            _append($"Prerequisite: {note}");
        }

        ApproveRunPlanCommand.RaiseCanExecuteChanged();
        DismissRunPlanCommand.RaiseCanExecuteChanged();
    }

    private void OnRunPlanExecutionCompleted(object? sender, RunPlanExecutionEventArgs e)
    {
        if (e.Plan is null)
        {
            return;
        }

        var status = e.Outcome.Cancelled
            ? "was cancelled"
            : e.Outcome.Success
                ? "completed"
                : "finished with issues";
        _append($"Run plan \"{e.Plan.Name}\" {status}.");
        if (!string.IsNullOrWhiteSpace(e.Outcome.Message))
        {
            _append($"Plan result: {e.Outcome.Message}");
        }

        ApproveRunPlanCommand.RaiseCanExecuteChanged();
        DismissRunPlanCommand.RaiseCanExecuteChanged();
    }

    private void OnRunPlanPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(RunPlan.Status), StringComparison.Ordinal))
        {
            return;
        }

        ApproveRunPlanCommand.RaiseCanExecuteChanged();
        DismissRunPlanCommand.RaiseCanExecuteChanged();
    }

    private bool CanApproveRunPlan(RunPlan? plan)
        => plan is not null && (plan.Status == RunPlanStatus.Pending || plan.Status == RunPlanStatus.Scheduled);

    private void OnApproveRunPlan(RunPlan? plan)
    {
        if (plan is null)
        {
            return;
        }

        if (_runPlanner.TryApprove(plan))
        {
            _append($"Operator approved run plan \"{plan.Name}\".");
            return;
        }

        _append($"Run plan \"{plan.Name}\" could not be approved (already in progress or completed).");
    }

    private bool CanDismissRunPlan(RunPlan? plan)
        => plan is not null && (plan.Status == RunPlanStatus.Pending || plan.Status == RunPlanStatus.Scheduled);

    private void OnDismissRunPlan(RunPlan? plan)
    {
        if (plan is null)
        {
            return;
        }

        if (_runPlanner.TryCancel(plan, "Dismissed by operator."))
        {
            _append($"Run plan \"{plan.Name}\" dismissed by operator.");
        }
        else
        {
            _append($"Run plan \"{plan.Name}\" could not be dismissed (already executing or finalized).");
        }
    }

    private static bool IsDelayWithinRange(double value)
        => value >= 0 && value <= 600;

    private static string FormatSeconds(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture) + "s";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
