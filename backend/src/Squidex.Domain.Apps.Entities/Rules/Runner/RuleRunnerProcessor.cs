﻿// ==========================================================================
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex UG (haftungsbeschraenkt)
//  All rights reserved. Licensed under the MIT license.
// ==========================================================================

using Microsoft.Extensions.Logging;
using Squidex.Caching;
using Squidex.Domain.Apps.Core.HandleRules;
using Squidex.Domain.Apps.Entities.Rules.Repositories;
using Squidex.Infrastructure;
using Squidex.Infrastructure.EventSourcing;
using Squidex.Infrastructure.States;
using Squidex.Infrastructure.Tasks;
using Squidex.Infrastructure.Translations;
using TaskHelper = Squidex.Infrastructure.Tasks.TaskExtensions;

namespace Squidex.Domain.Apps.Entities.Rules.Runner;

public sealed class RuleRunnerProcessor
{
    private const int MaxErrors = 10;
    private readonly IAppProvider appProvider;
    private readonly IEventFormatter eventFormatter;
    private readonly IEventStore eventStore;
    private readonly ILocalCache localCache;
    private readonly IRuleEventRepository ruleEventRepository;
    private readonly IRuleService ruleService;
    private readonly IRuleUsageTracker ruleUsageTracker;
    private readonly ILogger<RuleRunnerProcessor> log;
    private readonly SimpleState<RuleRunnerState> state;
    private readonly ReentrantScheduler scheduler = new ReentrantScheduler(1);
    private readonly DomainId appId;
    private Run? currentRun;

    // Use a run to store all state that is necessary for a single run.
    private sealed class Run : IDisposable
    {
        private readonly CancellationTokenSource cancellationSource = new CancellationTokenSource();
        private readonly CancellationTokenSource cancellationLinked;

        public RuleRunnerState Job { get; init; }

        public RuleContext Context { get; set; }

        public CancellationToken CancellationToken => cancellationLinked.Token;

        public Run(CancellationToken ct)
        {
            cancellationLinked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationSource.Token);
        }

        public void Dispose()
        {
            cancellationSource.Dispose();
            cancellationLinked.Dispose();
        }

        public void Cancel()
        {
            try
            {
                cancellationSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Cancellation token might have been disposed, if the run is completed.
            }
        }
    }

    public RuleRunnerProcessor(
        DomainId appId,
        IAppProvider appProvider,
        IEventFormatter eventFormatter,
        IEventStore eventStore,
        ILocalCache localCache,
        IPersistenceFactory<RuleRunnerState> persistenceFactory,
        IRuleEventRepository ruleEventRepository,
        IRuleService ruleService,
        IRuleUsageTracker ruleUsageTracker,
        ILogger<RuleRunnerProcessor> log)
    {
        this.appId = appId;
        this.appProvider = appProvider;
        this.localCache = localCache;
        this.eventStore = eventStore;
        this.eventFormatter = eventFormatter;
        this.ruleEventRepository = ruleEventRepository;
        this.ruleService = ruleService;
        this.ruleUsageTracker = ruleUsageTracker;
        this.log = log;

        state = new SimpleState<RuleRunnerState>(persistenceFactory, GetType(), appId);
    }

    public async Task LoadAsync(
        CancellationToken ct = default)
    {
        await state.LoadAsync(ct);

        if (!state.Value.RunFromSnapshots && state.Value.RuleId != default)
        {
            TaskHelper.Forget(RunAsync(state.Value.RuleId, false, default));
        }
        else
        {
            await state.ClearAsync(ct);
        }
    }

    public Task CancelAsync()
    {
        // Ensure that only one thread is accessing the current state at a time.
        return scheduler.Schedule(() =>
        {
            currentRun?.Cancel();
        });
    }

    public Task RunAsync(DomainId ruleId, bool fromSnapshots,
        CancellationToken ct)
    {
        return scheduler.ScheduleAsync(async ct =>
        {
            // There is no continuation token for snapshots, therefore we cannot continue with the run.
            if (currentRun?.Job.RunFromSnapshots == true)
            {
                throw new DomainException(T.Get("rules.ruleAlreadyRunning"));
            }

            var previousJob = state.Value;

            // If we have not removed the state, we have not completed the previous run and can therefore just continue.
            var position =
                previousJob.RuleId == ruleId && !previousJob.RunFromSnapshots ?
                previousJob.Position :
                null;

            // Set the current run first to indicate that we are running a rule at the moment.
            var run = currentRun = new Run(ct)
            {
                Job = new RuleRunnerState
                {
                    RuleId = ruleId,
                    RunId = DomainId.NewGuid(),
                    RunFromSnapshots = fromSnapshots,
                    Position = position
                }
            };

            state.Value = run.Job;
            try
            {
                await state.WriteAsync(run.CancellationToken);

                await ProcessAsync(run, run.CancellationToken);
            }
            finally
            {
                // Unset the run to indicate that we are done.
                currentRun.Dispose();
                currentRun = null;
            }
        }, ct);
    }

    private async Task ProcessAsync(Run run,
        CancellationToken ct)
    {
        try
        {
            var rule = await appProvider.GetRuleAsync(appId, run.Job.RuleId, ct);

            // The rule might have been deleted in the meantime.
            if (rule == null)
            {
                throw new DomainObjectNotFoundException(run.Job.RuleId.ToString()!);
            }

            using (localCache.StartContext())
            {
                // Also run disabled rules, because we want to enable rules to be only used with manual trigger.
                run.Context = new RuleContext
                {
                    AppId = rule.AppId,
                    Rule = rule.RuleDef,
                    RuleId = rule.Id,
                    IncludeStale = true,
                    IncludeSkipped = true
                };

                if (run.Job.RunFromSnapshots && ruleService.CanCreateSnapshotEvents(rule.RuleDef))
                {
                    await EnqueueFromSnapshotsAsync(run, ct);
                }
                else
                {
                    await EnqueueFromEventsAsync(run, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to run rule with ID {ruleId}.", run.Job.RuleId);
        }
        finally
        {
            // Remove the state to indicate that the run has been completed.
            await state.ClearAsync(default);
        }
    }

    private async Task EnqueueFromSnapshotsAsync(Run run,
        CancellationToken ct)
    {
        // We collect errors and allow a few erors before we throw an exception.
        var errors = 0;

        // Write in batches of 100 items for better performance. Using completes the last write.
        await using var batch = new RuleQueueWriter(ruleEventRepository, ruleUsageTracker, null);

        await foreach (var result in ruleService.CreateSnapshotJobsAsync(run.Context, ct))
        {
            await batch.WriteAsync(result);

            if (result.EnrichmentError != null)
            {
                errors++;

                // We accept a few errors and stop the process if there are too many errors.
                if (errors >= MaxErrors)
                {
                    throw result.EnrichmentError;
                }

                log.LogWarning(result.EnrichmentError, "Failed to run rule with ID {ruleId}, continue with next job.", result.RuleId);
            }
        }
    }

    private async Task EnqueueFromEventsAsync(Run run,
        CancellationToken ct)
    {
        // We collect errors and allow a few erors before we throw an exception.
        var errors = 0;

        // Write in batches of 100 items for better performance. Using completes the last write.
        await using var batch = new RuleQueueWriter(ruleEventRepository, ruleUsageTracker, null);

        // Use a prefix query so that the storage can use an index for the query.
        var streamFilter = StreamFilter.Prefix($"([a-zA-Z0-9]+)\\-{appId}");

        await foreach (var storedEvent in eventStore.QueryAllAsync(streamFilter, run.Job.Position, ct: ct))
        {
            var @event = eventFormatter.ParseIfKnown(storedEvent);

            if (@event == null)
            {
                continue;
            }

            run.Job.Position = storedEvent.EventPosition;

            await foreach (var result in ruleService.CreateJobsAsync(@event, run.Context.ToRulesContext(), ct))
            {
                if (await batch.WriteAsync(result))
                {
                    // Update the process when something has been written.
                    await state.WriteAsync(ct);
                }

                if (result.EnrichmentError != null)
                {
                    errors++;

                    // We accept a few errors and stop the process if there are too many errors.
                    if (errors >= MaxErrors)
                    {
                        throw result.EnrichmentError;
                    }

                    log.LogWarning(result.EnrichmentError, "Failed to run rule with ID {ruleId}, continue with next job.", result.RuleId);
                }
            }
        }

        if (await batch.FlushAsync())
        {
            // Update the process when something has been written.
            await state.WriteAsync(ct);
        }
    }
}
