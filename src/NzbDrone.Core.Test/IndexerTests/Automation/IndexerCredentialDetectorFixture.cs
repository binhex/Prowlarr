using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Automation;
using NzbDrone.Core.Indexers.Definitions.Cardigann;
using NzbDrone.Core.Indexers.Settings;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Test.IndexerTests.Automation
{
    [TestFixture]
    public class IndexerCredentialDetectorFixture
    {
        private class NoAuthSettings : NoAuthTorrentBaseSettings
        {
        }

        private class UserPassSettings : UserPassTorrentBaseSettings
        {
        }

        private class CookieSettings : CookieTorrentBaseSettings
        {
        }

        private class ApiKeySettings : NoAuthTorrentBaseSettings
        {
            [FieldDefinition(4, Label = "ApiKey", Privacy = PrivacyLevel.ApiKey)]
            public string ApiKey { get; set; }
        }

        private class PasswordTypeSettings : NoAuthTorrentBaseSettings
        {
            [FieldDefinition(4, Label = "Secret", Type = FieldType.Password)]
            public string Secret { get; set; }
        }

        private class MixedSettings : NoAuthTorrentBaseSettings
        {
            public string NotAField { get; set; }
        }

        private class MamIdSettings : NoAuthTorrentBaseSettings
        {
            [FieldDefinition(4, Label = "MamId")]
            public string MamId { get; set; }
        }

        private static IndexerDefinition Definition(object settings)
        {
            return new IndexerDefinition { Implementation = "Test", Settings = (IProviderConfig)settings };
        }

        [Test]
        public void should_treat_no_auth_settings_as_credential_free()
        {
            IndexerCredentialDetector.RequiresCredentials(Definition(new NoAuthSettings()), null).Should().BeFalse();
        }

        [Test]
        public void should_detect_username_password_settings()
        {
            IndexerCredentialDetector.RequiresCredentials(Definition(new UserPassSettings()), null).Should().BeTrue();
        }

        [Test]
        public void should_detect_cookie_settings()
        {
            IndexerCredentialDetector.RequiresCredentials(Definition(new CookieSettings()), null).Should().BeTrue();
        }

        [Test]
        public void should_detect_api_key_field()
        {
            IndexerCredentialDetector.RequiresCredentials(Definition(new ApiKeySettings()), null).Should().BeTrue();
        }

        [Test]
        public void should_treat_cardigann_without_a_settings_block_as_credential_free()
        {
            // The injected username/password fields are optional, so no settings + no login is public.
            var definition = Definition(new CardigannSettings { DefinitionFile = "anidex.yml" });
            var cardigann = new CardigannDefinition { Settings = null, Login = null };

            IndexerCredentialDetector.RequiresCredentials(definition, cardigann).Should().BeFalse();
        }

        [Test]
        public void should_treat_cardigann_with_login_block_as_requiring_credentials()
        {
            var definition = Definition(new CardigannSettings { DefinitionFile = "private.yml" });
            var cardigann = new CardigannDefinition { Login = new LoginBlock { Path = "login.php" } };

            IndexerCredentialDetector.RequiresCredentials(definition, cardigann).Should().BeTrue();
        }

        [Test]
        public void should_detect_cardigann_password_field()
        {
            var definition = Definition(new CardigannSettings { DefinitionFile = "apikey.yml" });
            var cardigann = new CardigannDefinition
            {
                Settings = new List<SettingsField> { new SettingsField { Name = "passkey", Type = "password" } }
            };

            IndexerCredentialDetector.RequiresCredentials(definition, cardigann).Should().BeTrue();
        }

        [Test]
        public void should_detect_cardigann_auth_shaped_field_name()
        {
            var definition = Definition(new CardigannSettings { DefinitionFile = "rss.yml" });
            var cardigann = new CardigannDefinition
            {
                Settings = new List<SettingsField> { new SettingsField { Name = "rsskey", Type = "text" } }
            };

            IndexerCredentialDetector.RequiresCredentials(definition, cardigann).Should().BeTrue();
        }

        [Test]
        public void should_treat_cardigann_with_only_sort_fields_as_credential_free()
        {
            var definition = Definition(new CardigannSettings { DefinitionFile = "sort.yml" });
            var cardigann = new CardigannDefinition
            {
                Settings = new List<SettingsField> { new SettingsField { Name = "sort", Type = "select" } }
            };

            IndexerCredentialDetector.RequiresCredentials(definition, cardigann).Should().BeFalse();
        }

        [Test]
        public void should_treat_uncached_cardigann_definition_as_requiring_credentials()
        {
            var definition = Definition(new CardigannSettings { DefinitionFile = "missing.yml" });

            IndexerCredentialDetector.RequiresCredentials(definition, null).Should().BeTrue();
        }

        [Test]
        public void should_ignore_null_cardigann_settings_field()
        {
            var definition = Definition(new CardigannSettings { DefinitionFile = "nullfield.yml" });
            var cardigann = new CardigannDefinition
            {
                Settings = new List<SettingsField> { null }
            };

            IndexerCredentialDetector.RequiresCredentials(definition, cardigann).Should().BeFalse();
        }

        [Test]
        public void should_skip_properties_without_field_definition_attribute()
        {
            IndexerCredentialDetector.RequiresCredentials(Definition(new MixedSettings()), null).Should().BeFalse();
        }

        [Test]
        public void should_detect_a_mam_id_secret()
        {
            IndexerCredentialDetector.RequiresCredentials(Definition(new MamIdSettings()), null).Should().BeTrue();
        }

        [Test]
        public void should_detect_password_field_type_without_privacy_level()
        {
            IndexerCredentialDetector.RequiresCredentials(Definition(new PasswordTypeSettings()), null).Should().BeTrue();
        }
    }
}
