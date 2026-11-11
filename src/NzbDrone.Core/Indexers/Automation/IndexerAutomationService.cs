using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.Indexers.Automation
{
    public interface IIndexerAutomationService
    {
        void Execute(IndexerAutomationCommand message);
        bool ClearSuppression(string key);
    }

    public class IndexerAutomationService : IIndexerAutomationService,
                                           IExecute<IndexerAutomationCommand>,
                                           IHandle<ApplicationStartedEvent>,
                                           IHandle<ProviderAddedEvent<IIndexer>>,
                                           IHandle<ProviderDeletedEvent<IIndexer>>,
                                           IHandle<ProviderBulkDeletedEvent<IIndexer>>
    {
        private readonly IIndexerFactory _indexerFactory;
        private readonly IIndexerStatusRepository _indexerStatusRepository;
        private readonly IIndexerAutomationStateRepository _stateRepository;
        private readonly IIndexerAutomationSettingsService _settingsService;
        private readonly IIndexerCandidateProvider _candidateProvider;
        private readonly IAppProfileService _appProfileService;
        private readonly IIndexerAutomationProbe _probe;
        private readonly Logger _logger;

        private static readonly Expression<Func<IndexerAutomationState, object>>[] ReleaseFields =
        {
            s => s.IndexerId, s => s.FailureSince, s => s.LastEscalationLevel, s => s.TrackingSince
        };

        private static readonly Expression<Func<IndexerAutomationState, object>>[] StreakFields =
        {
            s => s.FailureSince, s => s.LastFailureAt, s => s.LastEscalationLevel, s => s.TrackingSince
        };

        private static readonly Expression<Func<IndexerAutomationState, object>>[] RemovalFields =
        {
            s => s.IndexerId, s => s.FailureSince, s => s.LastEscalationLevel, s => s.RemovedAt, s => s.NextAttemptAt, s => s.LastAttemptResult, s => s.TrackingSince
        };

        private static readonly Expression<Func<IndexerAutomationState, object>>[] AddFields =
        {
            s => s.IndexerId, s => s.FailureSince, s => s.LastEscalationLevel, s => s.RemovedAt, s => s.NextAttemptAt, s => s.LastAttemptResult, s => s.LastAttemptAt, s => s.TrackingSince
        };

        // Binding must also persist the removal-state reset, otherwise a re-bound indexer keeps
        // "Removed" + a stale NextAttemptAt in the database forever.
        private static readonly Expression<Func<IndexerAutomationState, object>>[] BindFields =
        {
            s => s.IndexerId, s => s.FailureSince, s => s.LastEscalationLevel, s => s.RemovedAt, s => s.NextAttemptAt, s => s.LastAttemptResult, s => s.TrackingSince
        };

        private static readonly Expression<Func<IndexerAutomationState, object>>[] SuppressionFields =
        {
            s => s.IndexerId, s => s.Suppressed, s => s.SuppressedAt, s => s.LastAttemptResult
        };

        private readonly object _passLock = new object();

        // Guards every read-modify-write of the suppression columns: the delete handler runs on an API
        // request thread while the pass runs on the command thread.
        private readonly object _suppressionLock = new object();

        // Read by API request threads inside the delete event handler while the pass thread writes.
        private readonly ConcurrentDictionary<int, byte> _deletingIndexerIds = new ConcurrentDictionary<int, byte>();

        public IndexerAutomationService(IIndexerFactory indexerFactory,
                                        IIndexerStatusRepository indexerStatusRepository,
                                        IIndexerAutomationStateRepository stateRepository,
                                        IIndexerAutomationSettingsService settingsService,
                                        IIndexerCandidateProvider candidateProvider,
                                        IAppProfileService appProfileService,
                                        IIndexerAutomationProbe probe,
                                        Logger logger)
        {
            _indexerFactory = indexerFactory;
            _indexerStatusRepository = indexerStatusRepository;
            _stateRepository = stateRepository;
            _settingsService = settingsService;
            _candidateProvider = candidateProvider;
            _appProfileService = appProfileService;
            _probe = probe;
            _logger = logger;
        }

        public void Execute(IndexerAutomationCommand message)
        {
            if (!Monitor.TryEnter(_passLock))
            {
                _logger.Info("Indexer automation pass already running, skipping this run");

                return;
            }

            try
            {
                var settings = _settingsService.Get();

                if (!settings.Enabled)
                {
                    _logger.Debug("Indexer automation is disabled");
                    return;
                }

                if (!settings.IsValid)
                {
                    _logger.Error("Indexer automation settings are invalid (a criteria list is not valid JSON), skipping this pass");
                    return;
                }

                var indexers = _indexerFactory.All();
                var statuses = new Dictionary<int, IndexerStatus>();

                foreach (var status in _indexerStatusRepository.All())
                {
                    statuses.TryAdd(status.ProviderId, status);
                }

                var states = new Dictionary<string, IndexerAutomationState>(StringComparer.OrdinalIgnoreCase);

                foreach (var state in _stateRepository.All())
                {
                    if (!states.TryAdd(state.Key, state))
                    {
                        _logger.Warn("Duplicate indexer automation state key '{0}' ignored", state.Key);
                    }
                }

                UpdateFailureState(indexers, statuses, states);
                RemoveStaleIndexers(indexers, states, settings);
                ReconcileSuppression(states);
                AddCandidates(indexers, states, settings);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Indexer automation pass failed");
            }
            finally
            {
                Monitor.Exit(_passLock);
            }
        }

        private void UpdateFailureState(List<IndexerDefinition> indexers,
                                        Dictionary<int, IndexerStatus> statuses,
                                        Dictionary<string, IndexerAutomationState> states)
        {
            var now = DateTime.UtcNow;
            var enabledIds = indexers.Where(i => i.Enable).Select(i => i.Id).ToHashSet();
            var existingIds = indexers.Select(i => i.Id).ToHashSet();

            // Release rows whose indexer is gone; a merely disabled indexer keeps its binding (so a manual
            // delete of a disabled indexer can still be mapped back and suppressed) but loses its streak.
            foreach (var state in states.Values.Where(s => s.IndexerId != null))
            {
                if (!existingIds.Contains(state.IndexerId.Value))
                {
                    state.IndexerId = null;
                    state.FailureSince = null;
                    state.LastEscalationLevel = 0;
                    state.TrackingSince = null;
                    Save(state, ReleaseFields);
                }
                else if (!enabledIds.Contains(state.IndexerId.Value))
                {
                    // Keep the binding (so a manual delete can still be mapped and suppressed) but end the
                    // streak and move the floor: re-enabling must not resurrect the pre-disable episode.
                    var changed = state.FailureSince != null || state.LastEscalationLevel != 0 || state.TrackingSince == null;

                    state.FailureSince = null;
                    state.LastEscalationLevel = 0;
                    state.TrackingSince ??= now;

                    if (changed)
                    {
                        Save(state, StreakFields);
                    }
                }
            }

            foreach (var indexer in indexers)
            {
                var state = GetOrCreateState(IndexerCandidate.GetKey(indexer), states);

                // Several implementations allow multiple instances that share one definition key
                // (Newznab, Torznab, TorrentRss, duplicates of any native indexer). Only one instance
                // owns the row, so one instance's streak can never remove a different, healthy instance.
                if (state.IndexerId != null && state.IndexerId != indexer.Id)
                {
                    continue;
                }

                var boundThisPass = false;

                if (state.IndexerId == null)
                {
                    if (!indexer.Enable)
                    {
                        continue;
                    }

                    boundThisPass = true;

                    // Newly bound instance: never inherit a previous instance's streak or removal state.
                    state.IndexerId = indexer.Id;
                    state.FailureSince = null;
                    state.LastEscalationLevel = 0;
                    state.RemovedAt = null;
                    state.NextAttemptAt = null;
                    state.TrackingSince = now;

                    if (state.LastAttemptResult != null && state.LastAttemptResult != IndexerAutomationAttemptResult.Suppressed)
                    {
                        state.LastAttemptResult = null;
                    }
                }

                if (!indexer.Enable)
                {
                    // Bound but disabled: the streak was cleared above; nothing to track.
                    continue;
                }

                statuses.TryGetValue(indexer.Id, out var status);

                var escalationLevel = status?.EscalationLevel ?? 0;

                // RecordSuccess only decrements one level per success, so a decrease between passes is
                // evidence of recovery and must break the streak. A level that restarts from zero updates
                // InitialFailure, which is adopted below as the new episode start.
                var recovered = escalationLevel == 0 ||
                                (state.LastEscalationLevel.HasValue && escalationLevel < state.LastEscalationLevel.Value);

                if (recovered)
                {
                    // Clear the streak for this pass, but keep the tracking floor: a flaky indexer that
                    // occasionally succeeds must not reset a genuine multi-week failure episode.
                    state.FailureSince = null;
                }
                else
                {
                    // Only a *new* episode (escalation restarted from zero) may adopt InitialFailure;
                    // otherwise an existing streak keeps its start. Re-anchoring a flaky indexer to a
                    // months-old InitialFailure would delete it one pass after a success.
                    if (state.FailureSince == null)
                    {
                        var newEpisode = state.LastEscalationLevel == null || state.LastEscalationLevel == 0;
                        var episodeStart = newEpisode
                            ? status?.InitialFailure ?? now
                            : now;

                        var floor = boundThisPass ? now : (state.TrackingSince ?? DateTime.MinValue);

                        state.FailureSince = episodeStart < floor ? floor : episodeStart;
                    }

                    state.LastFailureAt = status?.MostRecentFailure ?? now;
                }

                state.LastEscalationLevel = escalationLevel;
                Save(state, boundThisPass ? BindFields : StreakFields);
            }
        }

        private void RemoveStaleIndexers(List<IndexerDefinition> indexers,
                                         Dictionary<string, IndexerAutomationState> states,
                                         IndexerAutomationSettings settings)
        {
            var now = DateTime.UtcNow;

            foreach (var indexer in indexers.Where(i => i.Enable))
            {
                var key = IndexerCandidate.GetKey(indexer);

                if (!states.TryGetValue(key, out var state) || state.FailureSince == null)
                {
                    continue;
                }

                // Only ever remove the indexer this row is bound to.
                if (state.IndexerId != indexer.Id)
                {
                    continue;
                }

                if ((now - state.FailureSince.Value).TotalDays < settings.RemovalDays)
                {
                    continue;
                }

                // Already gone (deleted manually between the snapshot and now): the delete handler has
                // recorded the suppression, so leave the row alone instead of overwriting it as "Removed".
                var current = _indexerFactory.Find(indexer.Id);

                if (current == null)
                {
                    _logger.Debug("Indexer '{0}' was already removed before automation could delete it", indexer.Name);
                    continue;
                }

                if (!current.Enable)
                {
                    // Disabled since the pass started: the user chose to pause it, not delete it.
                    _logger.Debug("Indexer '{0}' was disabled during the pass, skipping removal", indexer.Name);
                    continue;
                }

                try
                {
                    _logger.Info("Indexer automation removing '{0}' after {1} days in a failed state", indexer.Name, settings.RemovalDays);

                    // Persist the retry gate first: if the process dies after Delete, the candidate must
                    // still wait RetryDays instead of being re-added on the next pass.
                    state.NextAttemptAt = now.AddDays(settings.RetryDays);
                    state.RemovedAt = now;
                    Save(state, RemovalFields);

                    _deletingIndexerIds.TryAdd(indexer.Id, 0);
                    _indexerFactory.Delete(indexer.Id);

                    state.IndexerId = null;
                    state.FailureSince = null;
                    state.LastAttemptResult = IndexerAutomationAttemptResult.Removed;
                    Save(state, RemovalFields);
                }
                catch (Exception ex)
                {
                    // The indexer still exists: undo the pre-persisted retry gate so the row does not
                    // claim a removal that never happened and the streak keeps ageing.
                    state.RemovedAt = null;
                    state.NextAttemptAt = null;
                    Save(state, RemovalFields);

                    _logger.Error(ex, "Failed to remove indexer '{0}'", indexer.Name);
                }
                finally
                {
                    _deletingIndexerIds.TryRemove(indexer.Id, out _);
                }
            }
        }

        private void ReconcileSuppression(Dictionary<string, IndexerAutomationState> states)
        {
            List<IndexerDefinition> current = null;

            foreach (var snapshot in states.Values.Where(s => s.Suppressed).ToList())
            {
                current ??= _indexerFactory.All();

                if (!current.Any(i => string.Equals(IndexerCandidate.GetKey(i), snapshot.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // Re-read immediately before the write and skip when a concurrent manual delete has since
                // released the binding (IndexerId == null) or set the suppression: clearing it here would
                // let the add phase re-create the indexer the user just deleted.
                lock (_suppressionLock)
                {
                    var persisted = _stateRepository.FindByKey(snapshot.Key);

                    if (persisted == null || !persisted.Suppressed)
                    {
                        continue;
                    }

                    persisted.Suppressed = false;
                    persisted.SuppressedAt = null;

                    if (persisted.LastAttemptResult == IndexerAutomationAttemptResult.Suppressed)
                    {
                        persisted.LastAttemptResult = null;
                    }

                    _stateRepository.SetFields(persisted, s => s.Suppressed, s => s.SuppressedAt, s => s.LastAttemptResult);
                }

                snapshot.Suppressed = false;
                snapshot.SuppressedAt = null;
            }
        }

        private void AddCandidates(List<IndexerDefinition> indexers,
                                   Dictionary<string, IndexerAutomationState> states,
                                   IndexerAutomationSettings settings)
        {
            var profiles = _appProfileService.All();
            var profile = profiles.FirstOrDefault(p => p.Name == "Standard") ?? profiles.FirstOrDefault();

            if (profile == null)
            {
                _logger.Warn("Indexer automation cannot add indexers: no app profile is configured");
                return;
            }

            var present = indexers.Select(IndexerCandidate.GetKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var now = DateTime.UtcNow;

            // Re-read suppression: a manual delete during this pass must not be undone by the add phase.
            var suppressedNow = _stateRepository.All()
                .Where(s => s.Suppressed)
                .Select(s => s.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var due = _candidateProvider.GetCandidates()
                .Where(c => !c.RequiresCredentials)
                .Where(c => !present.Contains(c.Key))
                .Where(c => !suppressedNow.Contains(c.Key))
                .Where(c => IsDue(states, c.Key, now))
                .Where(c => IndexerCriteriaMatcher.Matches(c, settings))
                .GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
                .Take(settings.MaxAddsPerPass)
                .ToList();

            foreach (var candidate in due)
            {
                try
                {
                    TryAddCandidate(candidate, profile, settings, states);
                }
                catch (Exception ex)
                {
                    // One bad candidate must never abort the pass or starve the candidates ordered after it.
                    _logger.Error(ex, "Indexer automation failed to add '{0}'", candidate.Name);

                    var state = GetOrCreateState(candidate.Key, states);
                    state.LastAttemptResult = IndexerAutomationAttemptResult.Failed;
                    state.NextAttemptAt = DateTime.UtcNow.AddDays(settings.RetryDays);
                    Save(state, AddFields);
                }
            }
        }

        private void TryAddCandidate(IndexerCandidate candidate,
                                     AppSyncProfile profile,
                                     IndexerAutomationSettings settings,
                                     Dictionary<string, IndexerAutomationState> states)
        {
            var state = GetOrCreateState(candidate.Key, states);
            state.LastAttemptAt = DateTime.UtcNow;

            // Candidates are cached metadata; resolve a fresh definition so each attempt starts clean.
            var definition = _candidateProvider.GetDefinition(candidate.Key);

            if (definition == null)
            {
                state.LastAttemptResult = IndexerAutomationAttemptResult.Failed;
                state.NextAttemptAt = DateTime.UtcNow.AddDays(settings.RetryDays);
                Save(state, AddFields);

                _logger.Warn("Indexer automation cannot resolve the definition for '{0}'", candidate.Name);

                return;
            }

            definition.Id = 0;
            definition.Enable = true;
            definition.Priority = 25;
            definition.AppProfileId = profile.Id;
            definition.DownloadClientId = 0;
            definition.Added = DateTime.UtcNow;
            definition.Redirect = definition.Protocol == DownloadProtocol.Usenet && definition.SupportsRedirect;

            if (!_probe.TryProbe(definition, settings.TestTimeoutSeconds, out var failureMessage))
            {
                state.LastAttemptResult = IndexerAutomationAttemptResult.Failed;
                state.NextAttemptAt = DateTime.UtcNow.AddDays(settings.RetryDays);
                Save(state, AddFields);

                _logger.Info("Indexer automation probe failed for '{0}': {1}", candidate.Name, failureMessage);

                return;
            }

            // The pass can run for minutes; re-check presence immediately before creating so a
            // manual add during the pass cannot be duplicated. Names are unique in the database,
            // so a taken name would fail Create on every retry.
            lock (_suppressionLock)
            {
                var persistedState = _stateRepository.FindByKey(candidate.Key);

                if (persistedState != null && persistedState.Suppressed)
                {
                    _logger.Debug("Indexer '{0}' was suppressed during the pass, skipping automation add", candidate.Name);
                    return;
                }
            }

            var existing = _indexerFactory.All();

            if (existing.Any(i => string.Equals(IndexerCandidate.GetKey(i), candidate.Key, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.Debug("Indexer '{0}' was added during the pass, skipping automation add", candidate.Name);
                return;
            }

            if (existing.Any(i => string.Equals(i.Name, definition.Name, StringComparison.OrdinalIgnoreCase)))
            {
                // Names are unique in the database, so retrying can never succeed; record the attempt so
                // the candidate stops consuming a probe slot every pass.
                state.LastAttemptResult = IndexerAutomationAttemptResult.Failed;
                state.NextAttemptAt = DateTime.UtcNow.AddDays(settings.RetryDays);
                Save(state, AddFields);

                _logger.Info("Indexer automation cannot add '{0}': the name is already in use", candidate.Name);

                return;
            }

            var created = _indexerFactory.Create(definition);

            state.IndexerId = created.Id;
            state.FailureSince = null;
            state.LastEscalationLevel = 0;
            state.NextAttemptAt = null;
            state.RemovedAt = null;
            state.TrackingSince = DateTime.UtcNow;
            state.LastAttemptResult = IndexerAutomationAttemptResult.Passed;
            Save(state, AddFields);

            _logger.Info("Indexer automation added '{0}'", candidate.Name);
        }

        private static bool IsDue(Dictionary<string, IndexerAutomationState> states, string key, DateTime now)
        {
            return !states.TryGetValue(key, out var state) ||
                   state.NextAttemptAt == null ||
                   state.NextAttemptAt <= now;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            // Seed a row for every existing indexer: a manual delete that happens before the first pass
            // can then be mapped back to its key and suppressed.
            try
            {
                var now = DateTime.UtcNow;

                foreach (var definition in _indexerFactory.All())
                {
                    var key = IndexerCandidate.GetKey(definition);
                    var state = _stateRepository.FindByKey(key);

                    if (state == null)
                    {
                        _stateRepository.Upsert(new IndexerAutomationState
                        {
                            Key = key,
                            IndexerId = definition.Id,
                            TrackingSince = now
                        });

                        continue;
                    }

                    if (state.IndexerId != null)
                    {
                        continue;
                    }

                    state.IndexerId = definition.Id;
                    state.TrackingSince ??= now;
                    Save(state, BindFields);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to seed indexer automation state rows");
            }
        }

        public bool ClearSuppression(string key)
        {
            lock (_suppressionLock)
            {
                var state = _stateRepository.All()
                    .FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

                if (state == null)
                {
                    return false;
                }

                if (!state.Suppressed)
                {
                    // Nothing to clear: leave the retry backoff and result history intact.
                    return true;
                }

                state.Suppressed = false;
                state.SuppressedAt = null;
                state.NextAttemptAt = null;
                state.LastAttemptResult = null;
                _stateRepository.SetFields(state, s => s.Suppressed, s => s.SuppressedAt, s => s.NextAttemptAt, s => s.LastAttemptResult);

                return true;
            }
        }

        public void Handle(ProviderAddedEvent<IIndexer> message)
        {
            // Bind a state row as soon as an indexer exists, so a manual delete before the next pass
            // can still be mapped back to its candidate key and suppressed.
            var definition = message.Definition as IndexerDefinition;

            if (definition == null || definition.Id <= 0)
            {
                return;
            }

            var key = IndexerCandidate.GetKey(definition);
            var state = _stateRepository.FindByKey(key);

            if (state == null)
            {
                state = new IndexerAutomationState { Key = key };
            }

            if (state.IndexerId != null && state.IndexerId != definition.Id)
            {
                // The row belongs to another instance of the same definition; leave it alone.
                return;
            }

            state.IndexerId = definition.Id;
            state.TrackingSince = DateTime.UtcNow;
            state.RemovedAt = null;
            state.NextAttemptAt = null;
            state.FailureSince = null;
            state.LastEscalationLevel = 0;

            if (state.LastAttemptResult != null && state.LastAttemptResult != IndexerAutomationAttemptResult.Suppressed)
            {
                state.LastAttemptResult = null;
            }

            Save(state, BindFields);
        }

        public void Handle(ProviderDeletedEvent<IIndexer> message)
        {
            SuppressManuallyDeletedIndexer(message.ProviderId);
        }

        public void Handle(ProviderBulkDeletedEvent<IIndexer> message)
        {
            foreach (var id in message.ProviderIds)
            {
                SuppressManuallyDeletedIndexer(id);
            }
        }

        private void SuppressManuallyDeletedIndexer(int indexerId)
        {
            if (_deletingIndexerIds.ContainsKey(indexerId))
            {
                return;
            }

            lock (_suppressionLock)
            {
                var state = _stateRepository.All().FirstOrDefault(s => s.IndexerId == indexerId);

                if (state == null)
                {
                    _logger.Warn("Indexer {0} was deleted but has no automation state row, it cannot be suppressed", indexerId);
                    return;
                }

                state.IndexerId = null;
                state.Suppressed = true;
                state.SuppressedAt = DateTime.UtcNow;
                state.LastAttemptResult = IndexerAutomationAttemptResult.Suppressed;
                Save(state, SuppressionFields);

                _logger.Info("Indexer automation suppressed '{0}' after a manual delete", state.Key);
            }
        }

        private IndexerAutomationState GetOrCreateState(string key, Dictionary<string, IndexerAutomationState> states)
        {
            if (states.TryGetValue(key, out var state))
            {
                return state;
            }

            state = new IndexerAutomationState { Key = key };
            states[key] = state;

            return state;
        }

        // Persists only the columns the calling phase owns, so a concurrent manual delete (which writes
        // SuppressionFields) can never be overwritten by a stale in-memory copy from another phase.
        private void Save(IndexerAutomationState state, Expression<Func<IndexerAutomationState, object>>[] ownedFields)
        {
            var persisted = _stateRepository.FindByKey(state.Key);

            if (persisted == null)
            {
                state.Id = 0;

                try
                {
                    _stateRepository.Upsert(state);
                }
                catch (Exception ex)
                {
                    if (_stateRepository.FindByKey(state.Key) != null)
                    {
                        // A concurrent writer inserted the same key first; the row exists now.
                        _logger.Debug(ex, "Indexer automation state row '{0}' was inserted concurrently", state.Key);
                    }
                    else
                    {
                        _logger.Error(ex, "Failed to persist indexer automation state row '{0}'", state.Key);
                    }
                }

                return;
            }

            state.Id = persisted.Id;

            _stateRepository.SetFields(state, ownedFields);
        }
    }
}
