using System;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Indexers.Automation;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore
{
    [TestFixture]
    public class IndexerAutomationStateRepositoryFixture : DbTest<IndexerAutomationStateRepository, IndexerAutomationState>
    {
        [Test]
        public void should_insert_and_find_by_key()
        {
            Subject.Upsert(new IndexerAutomationState
            {
                Key = "Cardigann:anidex.yml",
                IndexerId = 12,
                FailureSince = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
                LastAttemptResult = IndexerAutomationAttemptResult.Failed,
                Suppressed = false
            });

            var stored = Subject.FindByKey("Cardigann:anidex.yml");

            stored.Should().NotBeNull();
            stored.IndexerId.Should().Be(12);
            stored.LastAttemptResult.Should().Be(IndexerAutomationAttemptResult.Failed);
            stored.Suppressed.Should().BeFalse();
            stored.FailureSince.Should().BeCloseTo(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromSeconds(1));
        }

        [Test]
        public void should_return_null_for_unknown_key()
        {
            Subject.FindByKey("nope").Should().BeNull();
        }
    }
}
