using System.Threading;
using FluentAssertions;
using FluentValidation.Results;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Automation;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.IndexerTests.Automation
{
    [TestFixture]
    public class IndexerAutomationProbeFixture : CoreTest<IndexerAutomationProbe>
    {
        private IndexerDefinition _definition;

        [SetUp]
        public void Setup()
        {
            _definition = new IndexerDefinition { Name = "Test", Implementation = "Anidex" };
        }

        [Test]
        public void should_return_true_when_test_passes()
        {
            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.Test(It.IsAny<IndexerDefinition>()))
                .Returns(new ValidationResult());

            var success = Subject.TryProbe(_definition, 5, out var failure);

            success.Should().BeTrue();
            failure.Should().BeNull();
        }

        [Test]
        public void should_return_false_with_messages_when_test_fails()
        {
            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.Test(It.IsAny<IndexerDefinition>()))
                .Returns(new ValidationResult(new[] { new ValidationFailure(string.Empty, "Unable to connect") }));

            var success = Subject.TryProbe(_definition, 5, out var failure);

            success.Should().BeFalse();
            failure.Should().Contain("Unable to connect");
        }

        [Test]
        public void should_return_false_when_test_times_out()
        {
            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.Test(It.IsAny<IndexerDefinition>()))
                .Returns(() =>
                {
                    Thread.Sleep(3000);
                    return new ValidationResult();
                });

            var success = Subject.TryProbe(_definition, 1, out var failure);

            success.Should().BeFalse();
            failure.Should().Contain("timed out");
        }

        [Test]
        public void should_return_false_when_test_throws()
        {
            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.Test(It.IsAny<IndexerDefinition>()))
                .Throws(new System.Exception("boom"));

            var success = Subject.TryProbe(_definition, 5, out var failure);

            success.Should().BeFalse();
            failure.Should().Contain("boom");
        }
    }
}
