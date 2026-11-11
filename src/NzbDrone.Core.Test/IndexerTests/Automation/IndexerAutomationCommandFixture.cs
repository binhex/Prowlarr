using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Indexers.Automation;

namespace NzbDrone.Core.Test.IndexerTests.Automation
{
    [TestFixture]
    public class IndexerAutomationCommandFixture
    {
        [Test]
        public void should_report_progress_to_client()
        {
            new IndexerAutomationCommand().SendUpdatesToClient.Should().BeTrue();
        }

        [Test]
        public void should_be_named_indexer_automation()
        {
            new IndexerAutomationCommand().Name.Should().Be("IndexerAutomation");
        }
    }
}
