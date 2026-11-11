using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NLog;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Indexers.Automation
{
    public interface IIndexerAutomationSettingsService
    {
        IndexerAutomationSettings Get();
        void Save(IndexerAutomationSettings settings);
    }

    public class IndexerAutomationSettingsService : IIndexerAutomationSettingsService
    {
        public const int MinimumTestTimeoutSeconds = 10;
        public const int MaximumTestTimeoutSeconds = 600;
        public const int MinimumDays = 1;
        public const int MaximumDays = 365;
        public const int MinimumAddsPerPass = 1;
        public const int MaximumAddsPerPass = 20;

        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public IndexerAutomationSettingsService(IConfigService configService, Logger logger)
        {
            _configService = configService;
            _logger = logger;
        }

        public IndexerAutomationSettings Get()
        {
            var isValid = true;

            var settings = new IndexerAutomationSettings
            {
                Enabled = _configService.IndexerAutomationEnabled,
                Protocols = DeserializeList<DownloadProtocol>(_configService.IndexerAutomationProtocols, ref isValid),
                Languages = DeserializeList<string>(_configService.IndexerAutomationLanguages, ref isValid),
                Privacy = DeserializeList<IndexerPrivacy>(_configService.IndexerAutomationPrivacy, ref isValid),
                Categories = DeserializeList<int>(_configService.IndexerAutomationCategories, ref isValid),
                TestTimeoutSeconds = _configService.IndexerAutomationTestTimeoutSeconds,
                RetryDays = _configService.IndexerAutomationRetryDays,
                RemovalDays = _configService.IndexerAutomationRemovalDays,
                MaxAddsPerPass = _configService.IndexerAutomationMaxAddsPerPass,
                IsValid = isValid
            };

            return Clamp(settings);
        }

        public void Save(IndexerAutomationSettings settings)
        {
            var clamped = Clamp(settings);

            // Enabled is written last: a partial write must never leave automation enabled against
            // thresholds that were not persisted.
            _configService.IndexerAutomationProtocols = STJson.ToJson(clamped.Protocols);
            _configService.IndexerAutomationLanguages = STJson.ToJson(clamped.Languages);
            _configService.IndexerAutomationPrivacy = STJson.ToJson(clamped.Privacy);
            _configService.IndexerAutomationCategories = STJson.ToJson(clamped.Categories);
            _configService.IndexerAutomationTestTimeoutSeconds = clamped.TestTimeoutSeconds;
            _configService.IndexerAutomationRetryDays = clamped.RetryDays;
            _configService.IndexerAutomationRemovalDays = clamped.RemovalDays;
            _configService.IndexerAutomationMaxAddsPerPass = clamped.MaxAddsPerPass;
            _configService.IndexerAutomationEnabled = clamped.Enabled;
        }

        public static IndexerAutomationSettings Clamp(IndexerAutomationSettings settings)
        {
            settings.Protocols ??= new List<DownloadProtocol>();
            settings.Languages ??= new List<string>();
            settings.Privacy ??= new List<IndexerPrivacy>();
            settings.Categories ??= new List<int>();

            settings.TestTimeoutSeconds = Math.Clamp(settings.TestTimeoutSeconds, MinimumTestTimeoutSeconds, MaximumTestTimeoutSeconds);
            settings.RetryDays = Math.Clamp(settings.RetryDays, MinimumDays, MaximumDays);
            settings.RemovalDays = Math.Clamp(settings.RemovalDays, MinimumDays, MaximumDays);
            settings.MaxAddsPerPass = Math.Clamp(settings.MaxAddsPerPass, MinimumAddsPerPass, MaximumAddsPerPass);

            return settings;
        }

        private List<T> DeserializeList<T>(string json, ref bool isValid)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<T>();
            }

            try
            {
                var values = STJson.Deserialize<List<T>>(json);

                if (values == null)
                {
                    return new List<T>();
                }

                // A hand-edited row can contain null entries; they must not reach the matcher.
                return values.Where(v => v != null).ToList();
            }
            catch (JsonException ex)
            {
                // An empty dimension means "unconstrained", so silently falling back to an empty list would
                // turn a filtered rule into "match everything". Flag the settings invalid instead: the pass
                // skips while the value stays broken, and the endpoint still returns defaults.
                _logger.Warn(ex, "Indexer automation setting '{0}' is not valid JSON, the pass will be skipped until it is fixed", typeof(T).Name);
                isValid = false;

                return new List<T>();
            }
        }
    }
}
