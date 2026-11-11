using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Automation;
using NzbDrone.Core.Indexers.Definitions.Cardigann;
using NzbDrone.Core.IndexerVersions;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.IndexerTests.Automation
{
    [TestFixture]
    public class IndexerCandidateProviderFixture : CoreTest<IndexerCandidateProvider>
    {
        private IndexerDefinition _native;
        private IndexerDefinition _cardigann;

        [SetUp]
        public void Setup()
        {
            _native = new IndexerDefinition
            {
                Name = "Anidex",
                Implementation = "Anidex",
                Protocol = DownloadProtocol.Torrent,
                Privacy = IndexerPrivacy.Public,
                Language = "en-US",
                Settings = new NzbDrone.Core.Indexers.Settings.NoAuthTorrentBaseSettings(),
                Capabilities = new IndexerCapabilities()
            };

            _cardigann = new IndexerDefinition
            {
                Name = "SomeTracker",
                Implementation = "Cardigann",
                Protocol = DownloadProtocol.Torrent,
                Privacy = IndexerPrivacy.Private,
                Language = "en-US",
                Settings = new CardigannSettings { DefinitionFile = "sometracker.yml" },
                Capabilities = new IndexerCapabilities()
            };

            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.GetDefaultDefinitions())
                .Returns(new List<IndexerDefinition> { _native, _cardigann });

            Mocker.GetMock<IIndexerDefinitionUpdateService>()
                .Setup(d => d.GetCachedDefinition("sometracker.yml"))
                .Returns(new CardigannDefinition { Login = new LoginBlock { Path = "login.php" } });
        }

        [Test]
        public void should_map_native_definition_to_credential_free_candidate()
        {
            var candidate = Subject.GetCandidates().Single(c => c.Key == "Anidex");

            candidate.Name.Should().Be("Anidex");
            candidate.RequiresCredentials.Should().BeFalse();
            candidate.Protocol.Should().Be(DownloadProtocol.Torrent);
            candidate.Privacy.Should().Be(IndexerPrivacy.Public);
        }

        [Test]
        public void should_map_cardigann_definition_with_login_to_credentialed_candidate()
        {
            var candidate = Subject.GetCandidates().Single(c => c.Key == "Cardigann:sometracker.yml");

            candidate.RequiresCredentials.Should().BeTrue();
        }

        [Test]
        public void should_skip_cardigann_definition_that_is_not_cached()
        {
            _cardigann.Settings = new CardigannSettings { DefinitionFile = "missing.yml" };

            Subject.GetCandidates().Should().HaveCount(1);
        }

        [Test]
        public void should_resolve_a_fresh_definition_by_key()
        {
            var definition = Subject.GetDefinition("Anidex");

            definition.Should().NotBeNull();
            definition.Name.Should().Be("Anidex");
        }

        [Test]
        public void should_return_null_for_an_unknown_definition_key()
        {
            Subject.GetDefinition("nope").Should().BeNull();
        }

        [Test]
        public void should_cache_candidates_between_calls()
        {
            Subject.GetCandidates();
            Subject.GetCandidates();

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.GetDefaultDefinitions(), Times.Once());
        }

        [Test]
        public void should_return_a_fresh_definition_instance_on_every_resolve()
        {
            var first = Subject.GetDefinition("Anidex");
            var second = Subject.GetDefinition("Anidex");

            first.Should().NotBeSameAs(second);
            first.ExtraFields.Should().NotBeSameAs(second.ExtraFields);
        }

        [Test]
        public void should_return_empty_category_ids_when_capabilities_are_missing()
        {
            _native.Capabilities = null;

            var candidate = Subject.GetCandidates().Single(c => c.Key == "Anidex");

            candidate.CategoryIds.Should().BeEmpty();
        }
    }
}
