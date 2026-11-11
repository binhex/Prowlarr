using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Automation;
using NzbDrone.Core.Indexers.Definitions.Cardigann;

namespace NzbDrone.Core.Test.IndexerTests.Automation
{
    [TestFixture]
    public class IndexerCriteriaMatcherFixture
    {
        private static IndexerCandidate Candidate(
            DownloadProtocol protocol = DownloadProtocol.Torrent,
            IndexerPrivacy privacy = IndexerPrivacy.Public,
            string language = "en-US",
            params int[] categories)
        {
            return new IndexerCandidate
            {
                Key = "Test",
                Name = "Test",
                Protocol = protocol,
                Privacy = privacy,
                Language = language,
                CategoryIds = new HashSet<int>(categories)
            };
        }

        [Test]
        public void should_match_when_all_dimensions_are_empty()
        {
            IndexerCriteriaMatcher.Matches(Candidate(categories: 2000), new IndexerAutomationSettings()).Should().BeTrue();
        }

        [Test]
        public void should_not_match_when_protocol_is_not_selected()
        {
            var settings = new IndexerAutomationSettings { Protocols = new List<DownloadProtocol> { DownloadProtocol.Usenet } };

            IndexerCriteriaMatcher.Matches(Candidate(), settings).Should().BeFalse();
        }

        [Test]
        public void should_not_match_when_privacy_is_not_selected()
        {
            var settings = new IndexerAutomationSettings { Privacy = new List<IndexerPrivacy> { IndexerPrivacy.Private } };

            IndexerCriteriaMatcher.Matches(Candidate(), settings).Should().BeFalse();
        }

        [Test]
        public void should_match_language_case_insensitively()
        {
            var settings = new IndexerAutomationSettings { Languages = new List<string> { "EN-us" } };

            IndexerCriteriaMatcher.Matches(Candidate(language: "en-US"), settings).Should().BeTrue();
        }

        [Test]
        public void should_not_match_language_when_candidate_has_none_and_languages_are_selected()
        {
            var settings = new IndexerAutomationSettings { Languages = new List<string> { "en-US" } };

            IndexerCriteriaMatcher.Matches(Candidate(language: null), settings).Should().BeFalse();
        }

        [Test]
        public void should_match_any_selected_category()
        {
            var settings = new IndexerAutomationSettings { Categories = new List<int> { 2000, 5000 } };

            IndexerCriteriaMatcher.Matches(Candidate(categories: new[] { 2000, 5000, 5030, 5040 }), settings).Should().BeTrue();
        }

        [Test]
        public void should_not_match_when_no_selected_category_overlaps()
        {
            var settings = new IndexerAutomationSettings { Categories = new List<int> { 3000 } };

            IndexerCriteriaMatcher.Matches(Candidate(categories: new[] { 2000, 5000 }), settings).Should().BeFalse();
        }

        [Test]
        public void should_derive_key_for_native_definition()
        {
            var definition = new IndexerDefinition { Implementation = "Anidex" };

            IndexerCandidate.GetKey(definition).Should().Be("Anidex");
        }

        [Test]
        public void should_derive_a_rename_stable_key_for_a_renamed_native_definition()
        {
            var definition = new IndexerDefinition { Name = "My Anidex", Implementation = "Anidex" };

            IndexerCandidate.GetKey(definition).Should().Be("Anidex");
        }

        [Test]
        public void should_derive_key_for_cardigann_definition()
        {
            var definition = new IndexerDefinition
            {
                Implementation = "Cardigann",
                Settings = new CardigannSettings { DefinitionFile = "anidex.yml" }
            };

            IndexerCandidate.GetKey(definition).Should().Be("Cardigann:anidex.yml");
        }
    }
}
