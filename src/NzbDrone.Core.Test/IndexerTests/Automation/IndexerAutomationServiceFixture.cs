using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Automation;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.ThingiProvider.Events;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.IndexerTests.Automation
{
    [TestFixture]
    public class IndexerAutomationServiceFixture : CoreTest<IndexerAutomationService>
    {
        private IndexerDefinition _existing;
        private IndexerAutomationSettings _settings;
        private List<IndexerAutomationState> _states;
        private List<IndexerStatus> _statuses;
        private string _probeFailure;

        [SetUp]
        public void Setup()
        {
            _existing = new IndexerDefinition
            {
                Id = 1,
                Name = "Anidex",
                Implementation = "Anidex",
                Enable = true,
                Settings = new NzbDrone.Core.Indexers.Settings.NoAuthTorrentBaseSettings()
            };

            _settings = new IndexerAutomationSettings
            {
                Enabled = true,
                RetryDays = 7,
                RemovalDays = 14,
                TestTimeoutSeconds = 60,
                MaxAddsPerPass = 3
            };

            _states = new List<IndexerAutomationState>();
            _statuses = new List<IndexerStatus>();

            Mocker.GetMock<IIndexerAutomationSettingsService>().Setup(s => s.Get()).Returns(() => _settings);
            Mocker.GetMock<IIndexerFactory>().Setup(f => f.All()).Returns(() => new List<IndexerDefinition> { _existing });
            Mocker.GetMock<IIndexerFactory>().Setup(f => f.Find(1)).Returns(_existing);
            Mocker.GetMock<IIndexerStatusRepository>().Setup(r => r.All()).Returns(() => _statuses);
            Mocker.GetMock<IIndexerAutomationStateRepository>().Setup(r => r.All()).Returns(() => _states);
            Mocker.GetMock<IIndexerAutomationStateRepository>()
                .Setup(r => r.FindByKey(It.IsAny<string>()))
                .Returns<string>(k =>
                {
                    var match = _states.FirstOrDefault(s => string.Equals(s.Key, k, StringComparison.OrdinalIgnoreCase));

                    if (match == null)
                    {
                        return null;
                    }

                    return new IndexerAutomationState
                    {
                        Id = match.Id,
                        Key = match.Key,
                        IndexerId = match.IndexerId,
                        FailureSince = match.FailureSince,
                        LastFailureAt = match.LastFailureAt,
                        LastAttemptAt = match.LastAttemptAt,
                        NextAttemptAt = match.NextAttemptAt,
                        LastAttemptResult = match.LastAttemptResult,
                        RemovedAt = match.RemovedAt,
                        Suppressed = match.Suppressed,
                        SuppressedAt = match.SuppressedAt
                    };
                });
            Mocker.GetMock<IIndexerCandidateProvider>().Setup(p => p.GetCandidates()).Returns(new List<IndexerCandidate>());
            Mocker.GetMock<IAppProfileService>().Setup(s => s.All()).Returns(new List<AppSyncProfile> { new AppSyncProfile { Id = 1, Name = "Standard" } });
        }

        private void WithFailureStreak(int days)
        {
            _states.Add(new IndexerAutomationState
            {
                Key = "Anidex",
                IndexerId = 1,
                FailureSince = DateTime.UtcNow.AddDays(-days)
            });

            _statuses.Add(new IndexerStatus
            {
                ProviderId = 1,
                EscalationLevel = 2,
                InitialFailure = DateTime.UtcNow.AddDays(-days),
                MostRecentFailure = DateTime.UtcNow.AddDays(-days)
            });
        }

        private IndexerCandidate CreateCandidate(string key, bool requiresCredentials = false)
        {
            return new IndexerCandidate
            {
                Key = key,
                Name = key,
                Protocol = DownloadProtocol.Torrent,
                Privacy = IndexerPrivacy.Public,
                Language = "en-US",
                CategoryIds = new HashSet<int> { 2000 },
                RequiresCredentials = requiresCredentials
            };
        }

        private void WithCandidates(params IndexerCandidate[] candidates)
        {
            Mocker.GetMock<IIndexerCandidateProvider>()
                .Setup(p => p.GetCandidates())
                .Returns(candidates.ToList());

            Mocker.GetMock<IIndexerCandidateProvider>()
                .Setup(p => p.GetDefinition(It.IsAny<string>()))
                .Returns<string>(key => new IndexerDefinition
                {
                    Name = key,
                    Implementation = key,
                    Protocol = DownloadProtocol.Torrent,
                    Privacy = IndexerPrivacy.Public,
                    Language = "en-US",
                    Settings = new NzbDrone.Core.Indexers.Settings.NoAuthTorrentBaseSettings(),
                    Capabilities = new IndexerCapabilities()
                });
        }

        private void WithProbeResult(bool success)
        {
            _probeFailure = success ? null : "probe failed";

            Mocker.GetMock<IIndexerAutomationProbe>()
                .Setup(p => p.TryProbe(It.IsAny<IndexerDefinition>(), It.IsAny<int>(), out _probeFailure))
                .Returns(success);
        }

        [Test]
        public void should_do_nothing_when_disabled()
        {
            _settings.Enabled = false;

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Delete(It.IsAny<int>()), Times.Never());
            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(r => r.Upsert(It.IsAny<IndexerAutomationState>()), Times.Never());
            _states.Should().BeEmpty();
        }

        [Test]
        public void should_start_failure_streak_when_indexer_is_escalated()
        {
            _statuses.Add(new IndexerStatus { ProviderId = 1, EscalationLevel = 1, MostRecentFailure = DateTime.UtcNow });

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.Upsert(It.Is<IndexerAutomationState>(s => s.Key == "Anidex" && s.IndexerId == 1 && s.FailureSince != null)),
                Times.AtLeastOnce());
        }

        [Test]
        public void should_clear_failure_streak_when_indexer_recovers()
        {
            WithFailureStreak(3);
            _statuses.Clear();
            _statuses.Add(new IndexerStatus { ProviderId = 1, EscalationLevel = 0 });

            Subject.Execute(new IndexerAutomationCommand());

            _states.Single(s => s.Key == "Anidex").FailureSince.Should().BeNull();
        }

        [Test]
        public void should_remove_indexer_when_streak_reaches_threshold()
        {
            WithFailureStreak(20);

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Delete(1), Times.Once());

            var removed = _states.Single(s => s.Key == "Anidex");
            removed.LastAttemptResult.Should().Be(IndexerAutomationAttemptResult.Removed);
            removed.NextAttemptAt.Should().NotBeNull();
            removed.RemovedAt.Should().NotBeNull();
        }

        [Test]
        public void should_not_remove_indexer_below_threshold()
        {
            WithFailureStreak(13);

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Delete(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_not_remove_disabled_indexer()
        {
            _existing.Enable = false;
            WithFailureStreak(20);

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Delete(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_add_matching_credential_free_candidate_when_probe_passes()
        {
            WithCandidates(CreateCandidate("NewTracker"));
            WithProbeResult(true);

            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.Create(It.IsAny<IndexerDefinition>()))
                .Returns<IndexerDefinition>(d =>
                {
                    d.Id = 42;
                    return d;
                });

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(
                f => f.Create(It.Is<IndexerDefinition>(d => d.Name == "NewTracker" && d.Enable && d.AppProfileId == 1)),
                Times.Once());

            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.Upsert(It.Is<IndexerAutomationState>(s =>
                    s.Key == "NewTracker" &&
                    s.IndexerId == 42 &&
                    s.LastAttemptResult == IndexerAutomationAttemptResult.Passed &&
                    s.NextAttemptAt == null)),
                Times.AtLeastOnce());
        }

        [Test]
        public void should_not_create_when_probe_fails_and_should_schedule_retry()
        {
            WithCandidates(CreateCandidate("NewTracker"));
            WithProbeResult(false);

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Create(It.IsAny<IndexerDefinition>()), Times.Never());
            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.Upsert(It.Is<IndexerAutomationState>(s =>
                    s.Key == "NewTracker" &&
                    s.LastAttemptResult == IndexerAutomationAttemptResult.Failed &&
                    s.NextAttemptAt != null)),
                Times.AtLeastOnce());
        }

        [Test]
        public void should_skip_candidates_that_require_credentials()
        {
            WithCandidates(CreateCandidate("PrivateTracker", requiresCredentials: true));
            WithProbeResult(true);

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerAutomationProbe>().Verify(
                p => p.TryProbe(It.IsAny<IndexerDefinition>(), It.IsAny<int>(), out _probeFailure),
                Times.Never());
            _states.Should().NotContain(s => s.Key == "PrivateTracker");
        }

        [Test]
        public void should_skip_candidates_that_are_already_present()
        {
            WithCandidates(CreateCandidate("Anidex"));
            WithProbeResult(true);

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Create(It.IsAny<IndexerDefinition>()), Times.Never());
        }

        [Test]
        public void should_skip_candidates_that_are_suppressed()
        {
            _states.Add(new IndexerAutomationState { Key = "NewTracker", Suppressed = true, SuppressedAt = DateTime.UtcNow });
            WithCandidates(CreateCandidate("NewTracker"));
            WithProbeResult(true);

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Create(It.IsAny<IndexerDefinition>()), Times.Never());
        }

        [Test]
        public void should_skip_candidates_that_are_not_due()
        {
            _states.Add(new IndexerAutomationState { Key = "NewTracker", NextAttemptAt = DateTime.UtcNow.AddDays(3) });
            WithCandidates(CreateCandidate("NewTracker"));
            WithProbeResult(true);

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Create(It.IsAny<IndexerDefinition>()), Times.Never());
        }

        [Test]
        public void should_respect_max_adds_per_pass()
        {
            _settings.MaxAddsPerPass = 1;
            WithCandidates(CreateCandidate("A"), CreateCandidate("B"), CreateCandidate("C"));
            WithProbeResult(true);

            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.Create(It.IsAny<IndexerDefinition>()))
                .Returns<IndexerDefinition>(d =>
                {
                    d.Id = 99;
                    return d;
                });

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Create(It.IsAny<IndexerDefinition>()), Times.Once());
        }

        [Test]
        public void should_suppress_indexer_deleted_manually()
        {
            _states.Add(new IndexerAutomationState { Key = "Anidex", IndexerId = 7 });

            Subject.Handle(new ProviderDeletedEvent<IIndexer>(7));

            var suppressed = _states.Single(s => s.Key == "Anidex");
            suppressed.Suppressed.Should().BeTrue();
            suppressed.LastAttemptResult.Should().Be(IndexerAutomationAttemptResult.Suppressed);
        }

        [Test]
        public void should_not_suppress_indexer_deleted_by_automation()
        {
            WithFailureStreak(20);

            var suppressedDuringDelete = false;

            Mocker.GetMock<IIndexerAutomationStateRepository>()
                .Setup(r => r.Upsert(It.IsAny<IndexerAutomationState>()))
                .Callback<IndexerAutomationState>(s =>
                {
                    if (s.LastAttemptResult == IndexerAutomationAttemptResult.Suppressed)
                    {
                        suppressedDuringDelete = true;
                    }
                });

            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.Delete(1))
                .Callback(() => Subject.Handle(new ProviderDeletedEvent<IIndexer>(1)));

            Subject.Execute(new IndexerAutomationCommand());

            suppressedDuringDelete.Should().BeFalse();
        }

        [Test]
        public void should_skip_pass_when_another_pass_holds_the_lock()
        {
            var gate = typeof(IndexerAutomationService)
                .GetField("_passLock", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(Subject);

            var locked = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();

            var holder = Task.Run(() =>
            {
                lock (gate)
                {
                    locked.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
            });

            locked.Wait(TimeSpan.FromSeconds(10));

            try
            {
                Subject.Execute(new IndexerAutomationCommand());
            }
            finally
            {
                release.Set();
                holder.Wait(TimeSpan.FromSeconds(10));
            }

            Mocker.GetMock<IIndexerAutomationSettingsService>().Verify(s => s.Get(), Times.Never());
        }

        [Test]
        public void should_swallow_and_log_pass_exceptions()
        {
            Mocker.GetMock<IIndexerAutomationSettingsService>()
                .Setup(s => s.Get())
                .Throws(new InvalidOperationException("boom"));

            Subject.Invoking(s => s.Execute(new IndexerAutomationCommand())).Should().NotThrow();

            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void should_continue_when_removal_throws()
        {
            WithFailureStreak(20);

            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.Delete(1))
                .Throws(new InvalidOperationException("delete failed"));

            Subject.Invoking(s => s.Execute(new IndexerAutomationCommand())).Should().NotThrow();

            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.Upsert(It.Is<IndexerAutomationState>(s => s.LastAttemptResult == IndexerAutomationAttemptResult.Removed)),
                Times.Never());

            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void should_clear_suppression_when_indexer_exists_again()
        {
            _states.Add(new IndexerAutomationState
            {
                Key = "Anidex",
                IndexerId = 1,
                Suppressed = true,
                SuppressedAt = DateTime.UtcNow,
                LastAttemptResult = IndexerAutomationAttemptResult.Suppressed
            });

            Subject.Execute(new IndexerAutomationCommand());

            _states.Single(s => s.Key == "Anidex").Suppressed.Should().BeFalse();
        }

        [Test]
        public void should_not_add_when_no_app_profile_exists()
        {
            WithCandidates(CreateCandidate("NewTracker"));
            WithProbeResult(true);

            Mocker.GetMock<IAppProfileService>().Setup(s => s.All()).Returns(new List<AppSyncProfile>());

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Create(It.IsAny<IndexerDefinition>()), Times.Never());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_suppress_every_indexer_in_a_bulk_delete()
        {
            _states.Add(new IndexerAutomationState { Key = "Anidex", IndexerId = 7 });
            _states.Add(new IndexerAutomationState { Key = "Cardigann:other.yml", IndexerId = 8 });

            Subject.Handle(new ProviderBulkDeletedEvent<IIndexer>(new[] { 7, 8 }));

            _states.Should().OnlyContain(s => s.Suppressed);
            _states.Should().OnlyContain(s => s.LastAttemptResult == IndexerAutomationAttemptResult.Suppressed);
        }

        [Test]
        public void should_ignore_delete_of_unknown_indexer()
        {
            Subject.Handle(new ProviderDeletedEvent<IIndexer>(99));

            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.Upsert(It.IsAny<IndexerAutomationState>()),
                Times.Never());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_reanchor_a_flaky_indexer_to_an_old_initial_failure()
        {
            // Level 2 -> 1 (recovery) -> 2 (new failure) must not re-anchor to the months-old episode.
            _states.Add(new IndexerAutomationState
            {
                Key = "Anidex",
                IndexerId = 1,
                FailureSince = DateTime.UtcNow.AddDays(-90),
                LastEscalationLevel = 2
            });

            _statuses.Add(new IndexerStatus
            {
                ProviderId = 1,
                EscalationLevel = 1,
                InitialFailure = DateTime.UtcNow.AddDays(-90),
                MostRecentFailure = DateTime.UtcNow
            });

            Subject.Execute(new IndexerAutomationCommand());

            _states.Single(s => s.Key == "Anidex").FailureSince.Should().BeNull();

            // Next pass: it fails again from a cleared streak, so the streak starts now, not 90 days ago.
            _statuses.Clear();
            _statuses.Add(new IndexerStatus
            {
                ProviderId = 1,
                EscalationLevel = 2,
                InitialFailure = DateTime.UtcNow.AddDays(-90),
                MostRecentFailure = DateTime.UtcNow
            });

            Subject.Execute(new IndexerAutomationCommand());

            _states.Single(s => s.Key == "Anidex").FailureSince.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Delete(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_not_let_a_rebound_row_inherit_another_indexers_streak()
        {
            // Row was bound to indexer 99 which no longer exists; indexer 1 shares the same key.
            _states.Add(new IndexerAutomationState
            {
                Key = "Anidex",
                IndexerId = 99,
                FailureSince = DateTime.UtcNow.AddDays(-20),
                LastAttemptResult = IndexerAutomationAttemptResult.Removed
            });

            _statuses.Add(new IndexerStatus
            {
                ProviderId = 1,
                EscalationLevel = 1,
                InitialFailure = DateTime.UtcNow.AddMinutes(-5),
                MostRecentFailure = DateTime.UtcNow
            });

            Subject.Execute(new IndexerAutomationCommand());

            var state = _states.Single(s => s.Key == "Anidex");
            state.IndexerId.Should().Be(1);
            state.RemovedAt.Should().BeNull();

            // The previous instance's 20-day streak must not carry over: a freshly bound instance is
            // floored at the bind time.
            state.FailureSince.Should().NotBeNull();
            state.FailureSince.Value.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Delete(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_keep_the_binding_but_end_the_streak_when_an_indexer_is_disabled()
        {
            _existing.Enable = false;
            _states.Add(new IndexerAutomationState { Key = "Anidex", IndexerId = 1, FailureSince = DateTime.UtcNow.AddDays(-20) });

            Subject.Execute(new IndexerAutomationCommand());

            var state = _states.Single(s => s.Key == "Anidex");
            state.IndexerId.Should().Be(1);
            state.FailureSince.Should().BeNull();
            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Delete(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_keep_suppression_when_the_indexer_is_gone()
        {
            var suppressed = new IndexerAutomationState
            {
                Key = "Cardigann:gone.yml",
                Suppressed = true,
                SuppressedAt = DateTime.UtcNow
            };

            _states.Add(suppressed);
            Mocker.GetMock<IIndexerAutomationStateRepository>().Setup(r => r.FindByKey("Cardigann:gone.yml")).Returns(suppressed);

            Subject.Execute(new IndexerAutomationCommand());

            suppressed.Suppressed.Should().BeTrue();
            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.Upsert(It.Is<IndexerAutomationState>(s => s.Key == "Cardigann:gone.yml" && !s.Suppressed)),
                Times.Never());
        }

        [Test]
        public void should_bind_a_state_row_when_an_indexer_is_added()
        {
            var added = new IndexerDefinition
            {
                Id = 5,
                Name = "Anidex",
                Implementation = "Anidex",
                Settings = new NzbDrone.Core.Indexers.Settings.NoAuthTorrentBaseSettings()
            };

            Subject.Handle(new ProviderAddedEvent<IIndexer>(added));

            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.Upsert(It.Is<IndexerAutomationState>(s => s.Key == "Anidex" && s.IndexerId == 5)),
                Times.Once());
        }

        [Test]
        public void should_isolate_a_failing_candidate_from_the_rest_of_the_pass()
        {
            _settings.MaxAddsPerPass = 2;
            WithCandidates(CreateCandidate("A"), CreateCandidate("B"));
            WithProbeResult(true);

            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.Create(It.Is<IndexerDefinition>(d => d.Name == "A")))
                .Throws(new InvalidOperationException("create failed"));

            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.Create(It.Is<IndexerDefinition>(d => d.Name == "B")))
                .Returns<IndexerDefinition>(d =>
                {
                    d.Id = 77;
                    return d;
                });

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Create(It.Is<IndexerDefinition>(d => d.Name == "B")), Times.Once());
            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.Upsert(It.Is<IndexerAutomationState>(s => s.Key == "A" && s.LastAttemptResult == IndexerAutomationAttemptResult.Failed && s.NextAttemptAt != null)),
                Times.AtLeastOnce());
            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.Upsert(It.Is<IndexerAutomationState>(s => s.Key == "B" && s.LastAttemptResult == IndexerAutomationAttemptResult.Passed)),
                Times.AtLeastOnce());

            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void should_skip_the_add_when_the_indexer_appears_during_the_pass()
        {
            WithCandidates(CreateCandidate("NewTracker"));
            WithProbeResult(true);

            var calls = 0;
            var manual = new IndexerDefinition
            {
                Id = 9,
                Name = "NewTracker",
                Implementation = "NewTracker",
                Enable = true,
                Settings = new NzbDrone.Core.Indexers.Settings.NoAuthTorrentBaseSettings()
            };

            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.All())
                .Returns(() =>
                {
                    calls++;
                    return calls >= 2
                        ? new List<IndexerDefinition> { _existing, manual }
                        : new List<IndexerDefinition> { _existing };
                });

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Create(It.IsAny<IndexerDefinition>()), Times.Never());
        }

        [Test]
        public void should_persist_a_released_row_and_never_remove_a_re_enabled_indexer()
        {
            // Pass 1: the indexer is disabled, so its stale 20-day streak must be released and persisted.
            _existing.Enable = false;
            _states.Add(new IndexerAutomationState { Key = "Anidex", IndexerId = 1, FailureSince = DateTime.UtcNow.AddDays(-20) });
            _statuses.Add(new IndexerStatus { ProviderId = 1, EscalationLevel = 1, InitialFailure = DateTime.UtcNow.AddDays(-30) });

            Subject.Execute(new IndexerAutomationCommand());

            var released = _states.Single(s => s.Key == "Anidex");
            released.IndexerId.Should().Be(1);
            released.FailureSince.Should().BeNull();
            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.SetFields(It.IsAny<IndexerAutomationState>(), It.IsAny<System.Linq.Expressions.Expression<Func<IndexerAutomationState, object>>[]>()),
                Times.AtLeastOnce());

            // Pass 2: re-enabled. The stale InitialFailure must not remove it immediately.
            _existing.Enable = true;

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Delete(It.IsAny<int>()), Times.Never());

            var rebound = _states.Single(s => s.Key == "Anidex");
            rebound.IndexerId.Should().Be(1);
            rebound.FailureSince.Should().NotBeNull();
            rebound.FailureSince.Value.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
        }

        [Test]
        public void should_clear_the_streak_when_the_escalation_level_decreases()
        {
            _states.Add(new IndexerAutomationState
            {
                Key = "Anidex",
                IndexerId = 1,
                FailureSince = DateTime.UtcNow.AddDays(-20),
                LastEscalationLevel = 3
            });

            _statuses.Add(new IndexerStatus
            {
                ProviderId = 1,
                EscalationLevel = 2,
                InitialFailure = DateTime.UtcNow.AddDays(-20),
                MostRecentFailure = DateTime.UtcNow
            });

            Subject.Execute(new IndexerAutomationCommand());

            _states.Single(s => s.Key == "Anidex").FailureSince.Should().BeNull();
            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Delete(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_clear_removal_state_when_an_indexer_is_added_back()
        {
            _states.Add(new IndexerAutomationState
            {
                Key = "Anidex",
                RemovedAt = DateTime.UtcNow.AddDays(-1),
                NextAttemptAt = DateTime.UtcNow.AddDays(6),
                LastAttemptResult = IndexerAutomationAttemptResult.Removed
            });

            var added = new IndexerDefinition
            {
                Id = 3,
                Name = "Anidex",
                Implementation = "Anidex",
                Settings = new NzbDrone.Core.Indexers.Settings.NoAuthTorrentBaseSettings()
            };

            Subject.Handle(new ProviderAddedEvent<IIndexer>(added));

            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.SetFields(
                    It.Is<IndexerAutomationState>(s =>
                        s.Key == "Anidex" &&
                        s.IndexerId == 3 &&
                        s.RemovedAt == null &&
                        s.NextAttemptAt == null &&
                        s.LastAttemptResult == null),
                    It.IsAny<System.Linq.Expressions.Expression<Func<IndexerAutomationState, object>>[]>()),
                Times.Once());
        }

        [Test]
        public void should_bind_state_rows_even_when_automation_is_disabled()
        {
            _settings.Enabled = false;

            var added = new IndexerDefinition
            {
                Id = 4,
                Name = "Anidex",
                Implementation = "Anidex",
                Settings = new NzbDrone.Core.Indexers.Settings.NoAuthTorrentBaseSettings()
            };

            Subject.Handle(new ProviderAddedEvent<IIndexer>(added));

            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.Upsert(It.Is<IndexerAutomationState>(s => s.Key == "Anidex" && s.IndexerId == 4)),
                Times.Once());
        }

        [Test]
        public void should_suppress_a_manually_deleted_disabled_indexer()
        {
            _existing.Enable = false;
            _states.Add(new IndexerAutomationState { Key = "Anidex", IndexerId = 1, FailureSince = DateTime.UtcNow.AddDays(-20) });

            // A pass runs while the indexer is disabled: the binding is kept so the delete can be mapped.
            Subject.Execute(new IndexerAutomationCommand());

            Subject.Handle(new ProviderDeletedEvent<IIndexer>(1));

            var state = _states.Single(s => s.Key == "Anidex");
            state.Suppressed.Should().BeTrue();
            state.LastAttemptResult.Should().Be(IndexerAutomationAttemptResult.Suppressed);
        }

        [Test]
        public void should_seed_a_row_for_an_existing_indexer_at_startup()
        {
            Subject.Handle(new NzbDrone.Core.Lifecycle.ApplicationStartedEvent());

            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.Upsert(It.Is<IndexerAutomationState>(s => s.Key == "Anidex" && s.IndexerId == 1)),
                Times.Once());
        }

        [Test]
        public void should_keep_a_suppressed_row_for_a_deleted_indexer_at_startup()
        {
            Mocker.GetMock<IIndexerFactory>().Setup(f => f.All()).Returns(new List<IndexerDefinition>());

            var suppressed = new IndexerAutomationState
            {
                Key = "Anidex",
                Suppressed = true,
                SuppressedAt = DateTime.UtcNow
            };

            _states.Add(suppressed);
            Mocker.GetMock<IIndexerAutomationStateRepository>().Setup(r => r.FindByKey("Anidex")).Returns(suppressed);

            Subject.Handle(new NzbDrone.Core.Lifecycle.ApplicationStartedEvent());

            suppressed.Suppressed.Should().BeTrue();
            suppressed.IndexerId.Should().BeNull();
        }

        [Test]
        public void should_not_clear_suppression_for_an_indexer_that_is_gone()
        {
            Mocker.GetMock<IIndexerFactory>().Setup(f => f.All()).Returns(new List<IndexerDefinition>());

            var suppressed = new IndexerAutomationState
            {
                Key = "Anidex",
                IndexerId = null,
                Suppressed = true,
                SuppressedAt = DateTime.UtcNow
            };

            _states.Add(suppressed);
            Mocker.GetMock<IIndexerAutomationStateRepository>().Setup(r => r.FindByKey("Anidex")).Returns(suppressed);

            Subject.Execute(new IndexerAutomationCommand());

            suppressed.Suppressed.Should().BeTrue();
        }
    }
}
