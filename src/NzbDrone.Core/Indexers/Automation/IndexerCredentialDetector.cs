using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Reflection;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers.Definitions.Cardigann;

namespace NzbDrone.Core.Indexers.Automation
{
    public static class IndexerCredentialDetector
    {
        private static readonly HashSet<string> CredentialFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "username",
            "password",
            "apikey",
            "api_key",
            "cookie",
            "rsskey",
            "token",
            "passkey",
            "pid",
            "mamid",
            "mam_id"
        };

        public static bool RequiresCredentials(IndexerDefinition definition, CardigannDefinition cardigannDefinition)
        {
            if (cardigannDefinition != null)
            {
                return RequiresCardigannCredentials(cardigannDefinition);
            }

            if (definition.Settings is CardigannSettings)
            {
                // A Cardigann definition whose YML is not cached cannot be classified, so treat it as credentialed.
                return true;
            }

            return definition.Settings != null && HasCredentialField(definition.Settings.GetType());
        }

        private static bool RequiresCardigannCredentials(CardigannDefinition definition)
        {
            if (definition.Login != null)
            {
                return true;
            }

            // A missing settings block is not itself proof of credentials: the injected username/password
            // fields are optional, so a login-free public definition still passes a probe.
            return (definition.Settings ?? new List<SettingsField>()).Any(IsCredentialField);
        }

        private static bool IsCredentialField(SettingsField field)
        {
            if (field == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(field.Type) && field.Type.Equals("password", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(field.Name) && CredentialFieldNames.Contains(field.Name);
        }

        private static bool HasCredentialField(Type settingsType)
        {
            foreach (var property in settingsType.GetProperties())
            {
                var field = property.GetAttribute<FieldDefinitionAttribute>(false);

                if (field == null)
                {
                    continue;
                }

                if (field.Type == FieldType.Password)
                {
                    return true;
                }

                if (field.Privacy == PrivacyLevel.UserName ||
                    field.Privacy == PrivacyLevel.Password ||
                    field.Privacy == PrivacyLevel.ApiKey)
                {
                    return true;
                }

                // Some native contracts carry secrets without Privacy metadata (passkey, cookie, api key).
                // Explicit metadata wins: only fall back to the property name when Privacy is the default.
                if (field.Privacy == PrivacyLevel.Normal &&
                    !string.IsNullOrWhiteSpace(property.Name) &&
                    CredentialFieldNames.Contains(property.Name))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
