using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using WcScraper.Wpf.Models;

namespace WcScraper.Wpf.Services;

public sealed class RunPlanner : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ObservableCollection<RunPlan> _plans = new();
    private readonly ReadOnlyObservableCollection<RunPlan> _readonlyPlans;
    private readonly Func<RunPlan, Task<RunPlanExecutionOutcome>> _executor;
    private readonly DispatcherTimer _timer;
    private readonly Queue<RunPlan> _executionQueue = new();
    private readonly object _sync = new();
    private bool _isExecuting;

    public RunPlanner(Func<RunPlan, Task<RunPlanExecutionOutcome>> executor, Dispatcher? dispatcher = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _readonlyPlans = new ReadOnlyObservableCollection<RunPlan>(_plans);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, OnTimerTick, _dispatcher);
        _timer.Start();
    }

    public ReadOnlyObservableCollection<RunPlan> Plans => _readonlyPlans;

    public event EventHandler<RunPlan>? PlanQueued;
    public event EventHandler<RunPlanExecutionEventArgs>? PlanExecutionCompleted;

    public void Enqueue(RunPlan plan)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        InvokeOnDispatcher(() =>
        {
            if (plan.ExecutionMode == RunPlanExecutionMode.Scheduled
                && plan.ScheduledForUtc is { } scheduled
                && scheduled > DateTimeOffset.UtcNow)
            {
                plan.Status = RunPlanStatus.Scheduled;
            }
            else
            {
                plan.Status = RunPlanStatus.Pending;
            }

            plan.ExecutedAtUtc = null;
            plan.ResultNote = null;
            _plans.Add(plan);
        });

        RaisePlanQueued(plan);
        EvaluateSchedule();
    }

    public bool TryApprove(RunPlan? plan)
    {
        if (plan is null)
        {
            return false;
        }

        if (plan.Status is RunPlanStatus.Running or RunPlanStatus.Completed or RunPlanStatus.Failed)
        {
            return false;
        }

        QueueExecution(plan);
        return true;
    }

    public bool TryCancel(RunPlan? plan, string? reason = null)
    {
        if (plan is null)
        {
            return false;
        }

        if (plan.Status is RunPlanStatus.Running or RunPlanStatus.Completed or RunPlanStatus.Failed)
        {
            return false;
        }

        InvokeOnDispatcher(() =>
        {
            plan.Status = RunPlanStatus.Skipped;
            plan.ResultNote = string.IsNullOrWhiteSpace(reason) ? "Plan dismissed." : reason.Trim();
            plan.ExecutedAtUtc = DateTimeOffset.UtcNow;
        });

        RaisePlanCompleted(plan, new RunPlanExecutionOutcome(false, plan.ResultNote));
        return true;
    }

    public IReadOnlyList<RunPlanSnapshot> CreateSnapshot(
        RunPlan? overridePlan = null,
        RunPlanExecutionOutcome? overrideOutcome = null)
    {
        return InvokeOnDispatcher(() =>
        {
            var snapshots = new List<RunPlanSnapshot>(_plans.Count);
            foreach (var plan in _plans)
            {
                var snapshot = plan.CreateSnapshot();
                if (overridePlan is not null && snapshot.Id == overridePlan.Id)
                {
                    if (overrideOutcome is null)
                    {
                        snapshot = snapshot with { Status = RunPlanStatus.Running };
                    }
                    else
                    {
                        snapshot = snapshot with
                        {
                            Status = overrideOutcome.Success ? RunPlanStatus.Completed : RunPlanStatus.Failed,
                            ExecutedAtUtc = DateTimeOffset.UtcNow,
                            ResultNote = overrideOutcome.Message,
                        };
                    }
                }

                snapshots.Add(snapshot);
            }

            return (IReadOnlyList<RunPlanSnapshot>)snapshots;
        });
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }

    private void OnTimerTick(object? sender, EventArgs e)
        => EvaluateSchedule();

    private void EvaluateSchedule()
    {
        RunPlan? duePlan = null;
        InvokeOnDispatcher(() =>
        {
            var now = DateTimeOffset.UtcNow;
            duePlan = _plans.FirstOrDefault(plan =>
                plan.ExecutionMode == RunPlanExecutionMode.Scheduled
                && (plan.Status == RunPlanStatus.Scheduled || plan.Status == RunPlanStatus.Pending)
                && plan.ScheduledForUtc is { } scheduled
                && scheduled <= now);
        });

        if (duePlan is not null)
        {
            QueueExecution(duePlan);
        }
    }

    private void QueueExecution(RunPlan plan)
    {
        lock (_sync)
        {
            if (_isExecuting)
            {
                if (!_executionQueue.Contains(plan))
                {
                    _executionQueue.Enqueue(plan);
                }

                return;
            }

            _isExecuting = true;
        }

        _ = ExecutePlanAsync(plan);
    }

    private async Task ExecutePlanAsync(RunPlan plan)
    {
        RunPlanExecutionOutcome outcome;
        try
        {
            await InvokeOnDispatcherAsync(() =>
            {
                plan.Status = RunPlanStatus.Running;
                plan.ExecutedAtUtc ??= DateTimeOffset.UtcNow;
                plan.ResultNote = null;
            }).ConfigureAwait(false);

            try
            {
                outcome = await _executor(plan).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                outcome = new RunPlanExecutionOutcome(false, ex.Message);
            }

            await InvokeOnDispatcherAsync(() =>
            {
                plan.Status = outcome.Success ? RunPlanStatus.Completed : RunPlanStatus.Failed;
                plan.ExecutedAtUtc ??= DateTimeOffset.UtcNow;
                plan.ResultNote = outcome.Message;
            }).ConfigureAwait(false);

            RaisePlanCompleted(plan, outcome);
        }
        finally
        {
            RunPlan? next = null;
            lock (_sync)
            {
                if (_executionQueue.Count > 0)
                {
                    next = _executionQueue.Dequeue();
                }
                else
                {
                    _isExecuting = false;
                }
            }

            if (next is not null)
            {
                await ExecutePlanAsync(next).ConfigureAwait(false);
            }
        }
    }

    private void RaisePlanQueued(RunPlan plan)
        => InvokeOnDispatcher(() => PlanQueued?.Invoke(this, plan));

    private void RaisePlanCompleted(RunPlan plan, RunPlanExecutionOutcome outcome)
        => InvokeOnDispatcher(() => PlanExecutionCompleted?.Invoke(this, new RunPlanExecutionEventArgs(plan, outcome)));

    private void InvokeOnDispatcher(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }

    private T InvokeOnDispatcher<T>(Func<T> action)
    {
        if (_dispatcher.CheckAccess())
        {
            return action();
        }

        return _dispatcher.Invoke(action);
    }

    private Task InvokeOnDispatcherAsync(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action).Task;
    }
}

public sealed class RunPlanExecutionEventArgs : EventArgs
{
    public RunPlanExecutionEventArgs(RunPlan plan, RunPlanExecutionOutcome outcome)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Outcome = outcome;
    }

    public RunPlan Plan { get; }

    public RunPlanExecutionOutcome Outcome { get; }
}
