using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Automation;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.IndexerTests.Automation
{
    [TestFixture]
    public class IndexerAutomationSettingsServiceFixture : CoreTest<IndexerAutomationSettingsService>
    {
        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationEnabled).Returns(false);
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationProtocols).Returns("[]");
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationLanguages).Returns("[]");
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationPrivacy).Returns("[]");
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationCategories).Returns("[]");
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationTestTimeoutSeconds).Returns(60);
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationRetryDays).Returns(7);
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationRemovalDays).Returns(14);
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationMaxAddsPerPass).Returns(3);
        }

        [Test]
        public void should_return_defaults_when_nothing_configured()
        {
            var settings = Subject.Get();

            settings.Enabled.Should().BeFalse();
            settings.Protocols.Should().BeEmpty();
            settings.Languages.Should().BeEmpty();
            settings.Privacy.Should().BeEmpty();
            settings.Categories.Should().BeEmpty();
            settings.TestTimeoutSeconds.Should().Be(60);
            settings.RetryDays.Should().Be(7);
            settings.RemovalDays.Should().Be(14);
            settings.MaxAddsPerPass.Should().Be(3);
        }

        [Test]
        public void should_deserialize_json_list_values()
        {
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationProtocols).Returns("[\"torrent\",\"usenet\"]");
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationPrivacy).Returns("[\"public\",\"semiPrivate\"]");
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationLanguages).Returns("[\"en-US\"]");
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationCategories).Returns("[2000,5000]");

            var settings = Subject.Get();

            settings.Protocols.Should().BeEquivalentTo(new List<DownloadProtocol> { DownloadProtocol.Torrent, DownloadProtocol.Usenet });
            settings.Privacy.Should().BeEquivalentTo(new List<IndexerPrivacy> { IndexerPrivacy.Public, IndexerPrivacy.SemiPrivate });
            settings.Languages.Should().BeEquivalentTo(new List<string> { "en-US" });
            settings.Categories.Should().BeEquivalentTo(new List<int> { 2000, 5000 });
        }

        [Test]
        public void should_clamp_out_of_range_values_on_read()
        {
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationTestTimeoutSeconds).Returns(5);
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationRetryDays).Returns(0);
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationRemovalDays).Returns(9999);
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationMaxAddsPerPass).Returns(500);

            var settings = Subject.Get();

            settings.TestTimeoutSeconds.Should().Be(10);
            settings.RetryDays.Should().Be(1);
            settings.RemovalDays.Should().Be(365);
            settings.MaxAddsPerPass.Should().Be(20);
        }

        [Test]
        public void should_clamp_and_serialize_on_save()
        {
            Subject.Save(new IndexerAutomationSettings
            {
                Enabled = true,
                Protocols = new List<DownloadProtocol> { DownloadProtocol.Torrent },
                TestTimeoutSeconds = 1,
                RetryDays = 7,
                RemovalDays = 14,
                MaxAddsPerPass = 3
            });

            Mocker.GetMock<IConfigService>().VerifySet(c => c.IndexerAutomationEnabled = true, Times.Once());
            Mocker.GetMock<IConfigService>().VerifySet(c => c.IndexerAutomationTestTimeoutSeconds = 10, Times.Once());
            Mocker.GetMock<IConfigService>().VerifySet(
                c => c.IndexerAutomationProtocols = It.Is<string>(json => STJson.Deserialize<List<DownloadProtocol>>(json).SequenceEqual(new List<DownloadProtocol> { DownloadProtocol.Torrent })),
                Times.Once());
        }

        [Test]
        public void should_return_empty_lists_when_configured_values_are_blank()
        {
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationProtocols).Returns(string.Empty);
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationLanguages).Returns("   ");
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationPrivacy).Returns((string)null);
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationCategories).Returns(string.Empty);

            var settings = Subject.Get();

            settings.Protocols.Should().BeEmpty();
            settings.Languages.Should().BeEmpty();
            settings.Privacy.Should().BeEmpty();
            settings.Categories.Should().BeEmpty();
        }

        [Test]
        public void should_flag_settings_invalid_when_a_criteria_list_is_corrupt()
        {
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationProtocols).Returns("not-json");

            var settings = Subject.Get();

            settings.IsValid.Should().BeFalse();
            settings.Protocols.Should().BeEmpty();

            ExceptionVerification.ExpectedWarns(1);
        }
    }
}
