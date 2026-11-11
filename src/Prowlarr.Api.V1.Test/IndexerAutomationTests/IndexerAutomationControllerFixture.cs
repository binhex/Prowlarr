using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Automation;
using NzbDrone.Test.Common;
using Prowlarr.Api.V1.Indexers;

namespace NzbDrone.Api.Test.IndexerAutomationTests
{
    [TestFixture]
    public class IndexerAutomationControllerFixture : TestBase
    {
        private IndexerAutomationController _subject;

        [SetUp]
        public void Setup()
        {
            _subject = Mocker.Resolve<IndexerAutomationController>();
        }

        [Test]
        public void should_map_settings_to_resource()
        {
            Mocker.GetMock<IIndexerAutomationSettingsService>()
                .Setup(s => s.Get())
                .Returns(new IndexerAutomationSettings
                {
                    Enabled = true,
                    Protocols = new List<DownloadProtocol> { DownloadProtocol.Torrent },
                    Privacy = new List<IndexerPrivacy> { IndexerPrivacy.Public },
                    Languages = new List<string> { "en-US" },
                    Categories = new List<int> { 2000 },
                    TestTimeoutSeconds = 60,
                    RetryDays = 7,
                    RemovalDays = 14,
                    MaxAddsPerPass = 3
                });

            var resource = _subject.GetSettings();

            resource.Enabled.Should().BeTrue();
            resource.Protocols.Should().BeEquivalentTo(new List<DownloadProtocol> { DownloadProtocol.Torrent });
            resource.Categories.Should().BeEquivalentTo(new List<int> { 2000 });
            resource.RemovalDays.Should().Be(14);
        }

        [Test]
        public void should_save_settings_from_resource()
        {
            var resource = new IndexerAutomationResource
            {
                Enabled = true,
                Protocols = new List<DownloadProtocol> { DownloadProtocol.Usenet },
                Privacy = new List<IndexerPrivacy> { IndexerPrivacy.Private },
                Languages = new List<string>(),
                Categories = new List<int> { 5000 },
                TestTimeoutSeconds = 30,
                RetryDays = 3,
                RemovalDays = 10,
                MaxAddsPerPass = 2
            };

            _subject.SaveSettings(resource);

            Mocker.GetMock<IIndexerAutomationSettingsService>().Verify(
                s => s.Save(It.Is<IndexerAutomationSettings>(m =>
                    m.Enabled &&
                    m.TestTimeoutSeconds == 30 &&
                    m.RemovalDays == 10 &&
                    m.Categories.Contains(5000))),
                Times.Once());
        }

        [Test]
        public void should_return_persisted_settings_after_save()
        {
            Mocker.GetMock<IIndexerAutomationSettingsService>()
                .Setup(s => s.Get())
                .Returns(new IndexerAutomationSettings { TestTimeoutSeconds = 10 });

            var result = _subject.SaveSettings(new IndexerAutomationResource { TestTimeoutSeconds = 5 });

            var accepted = result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.AcceptedResult>().Subject;
            var persisted = accepted.Value.Should().BeOfType<IndexerAutomationResource>().Subject;

            persisted.TestTimeoutSeconds.Should().Be(10);
        }

        [Test]
        public void should_reject_a_missing_settings_body()
        {
            var result = _subject.SaveSettings(null);

            result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>();
        }

        [Test]
        public void should_list_state_with_names_from_persisted_indexers()
        {
            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.All())
                .Returns(new List<IndexerDefinition>
                {
                    new IndexerDefinition { Id = 1, Name = "My Anidex", Implementation = "Anidex" }
                });

            Mocker.GetMock<IIndexerAutomationStateRepository>()
                .Setup(r => r.All())
                .Returns(new List<IndexerAutomationState>
                {
                    new IndexerAutomationState { Key = "Anidex:My Anidex", IndexerId = 1, LastAttemptResult = IndexerAutomationAttemptResult.Passed },
                    new IndexerAutomationState { Key = "Cardigann:gone.yml", Suppressed = true }
                });

            var state = _subject.GetState();

            state.Should().HaveCount(2);
            state.Should().Contain(s => s.Key == "Anidex:My Anidex" && s.Name == "My Anidex");
            state.Should().Contain(s => s.Key == "Cardigann:gone.yml" && s.Name == "gone" && s.Suppressed);
        }

        [Test]
        public void should_queue_the_automation_command_when_run()
        {
            _subject.Run();

            Mocker.GetMock<NzbDrone.Core.Messaging.Commands.IManageCommandQueue>().Verify(
                q => q.Push(It.IsAny<IndexerAutomationCommand>(), It.IsAny<NzbDrone.Core.Messaging.Commands.CommandPriority>(), It.IsAny<NzbDrone.Core.Messaging.Commands.CommandTrigger>()),
                Times.Once());
        }

        [Test]
        public void should_delegate_clear_suppression_to_the_service()
        {
            Mocker.GetMock<IIndexerAutomationService>()
                .Setup(s => s.ClearSuppression("CARDIGANN:ANIDEX.YML"))
                .Returns(true);

            var result = _subject.ClearSuppression("CARDIGANN:ANIDEX.YML");

            result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkResult>();
            Mocker.GetMock<IIndexerAutomationService>().Verify(
                s => s.ClearSuppression("CARDIGANN:ANIDEX.YML"),
                Times.Once());
        }

        [Test]
        public void should_return_not_found_when_clearing_an_unknown_key()
        {
            Mocker.GetMock<IIndexerAutomationService>()
                .Setup(s => s.ClearSuppression("missing"))
                .Returns(false);

            _subject.ClearSuppression("missing").Should().BeOfType<Microsoft.AspNetCore.Mvc.NotFoundResult>();
        }
    }
}
