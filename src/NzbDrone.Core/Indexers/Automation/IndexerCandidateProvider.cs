using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Indexers.Definitions.Cardigann;
using NzbDrone.Core.IndexerVersions;

namespace NzbDrone.Core.Indexers.Automation
{
    public interface IIndexerCandidateProvider
    {
        List<IndexerCandidate> GetCandidates();
        IndexerDefinition GetDefinition(string key);
    }

    public class IndexerCandidateProvider : IIndexerCandidateProvider
    {
        // Definitions are refreshed daily by IndexerDefinitionUpdateCommand, so a 6 hour cache keeps the
        // hourly pass off the definition store (which may perform a remote fetch) without staleness risk.
        private static readonly TimeSpan CandidateCacheDuration = TimeSpan.FromHours(6);

        private readonly IIndexerFactory _indexerFactory;
        private readonly IIndexerDefinitionUpdateService _definitionService;
        private readonly Logger _logger;

        private readonly object _cacheLock = new object();
        private List<IndexerCandidate> _cachedCandidates;
        private Dictionary<string, IndexerDefinition> _cachedDefinitions = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _cacheExpiresAt = DateTime.MinValue;

        public IndexerCandidateProvider(IIndexerFactory indexerFactory,
                                        IIndexerDefinitionUpdateService definitionService,
                                        Logger logger)
        {
            _indexerFactory = indexerFactory;
            _definitionService = definitionService;
            _logger = logger;
        }

        public List<IndexerCandidate> GetCandidates()
        {
            // Enumerating definitions triggers a definition-store read (and potentially a network fetch),
            // so the hourly pass reuses a short-lived snapshot. Definitions are refreshed daily.
            lock (_cacheLock)
            {
                if (_cachedCandidates != null && DateTime.UtcNow < _cacheExpiresAt)
                {
                    return new List<IndexerCandidate>(_cachedCandidates);
                }
            }

            return new List<IndexerCandidate>(Rebuild());
        }

        public IndexerDefinition GetDefinition(string key)
        {
            lock (_cacheLock)
            {
                if (_cachedDefinitions.TryGetValue(key, out var cached) && DateTime.UtcNow < _cacheExpiresAt)
                {
                    // Return a fresh copy: the add path mutates the definition and Create writes its Id.
                    return CloneDefinition(cached);
                }
            }

            Rebuild();

            lock (_cacheLock)
            {
                return _cachedDefinitions.TryGetValue(key, out var definition) ? CloneDefinition(definition) : null;
            }
        }

        private List<IndexerCandidate> Rebuild()
        {
            var definitions = new Dictionary<string, IndexerDefinition>(StringComparer.OrdinalIgnoreCase);
            var candidates = BuildCandidates(definitions);

            lock (_cacheLock)
            {
                _cachedCandidates = candidates;
                _cachedDefinitions = definitions;
                _cacheExpiresAt = DateTime.UtcNow.Add(CandidateCacheDuration);

                return candidates;
            }
        }

        private static IndexerDefinition CloneDefinition(IndexerDefinition definition)
        {
            return new IndexerDefinition
            {
                Name = definition.Name,
                Implementation = definition.Implementation,
                Settings = definition.Settings,
                ConfigContract = definition.ConfigContract,
                Enable = definition.Enable,
                IndexerUrls = definition.IndexerUrls,
                LegacyUrls = definition.LegacyUrls,
                Description = definition.Description,
                Encoding = definition.Encoding,
                Language = definition.Language,
                Protocol = definition.Protocol,
                Privacy = definition.Privacy,
                SupportsRss = definition.SupportsRss,
                SupportsSearch = definition.SupportsSearch,
                SupportsRedirect = definition.SupportsRedirect,
                SupportsPagination = definition.SupportsPagination,
                Capabilities = definition.Capabilities,
                ExtraFields = definition.ExtraFields == null
                    ? null
                    : new List<SettingsField>(definition.ExtraFields)
            };
        }

        private List<IndexerCandidate> BuildCandidates(Dictionary<string, IndexerDefinition> definitions)
        {
            var candidates = new List<IndexerCandidate>();

            foreach (var definition in _indexerFactory.GetDefaultDefinitions())
            {
                try
                {
                    var candidate = MapCandidate(definition, definitions);

                    if (candidate != null)
                    {
                        candidates.Add(candidate);
                    }
                }
                catch (Exception ex)
                {
                    // A single broken or uncached definition must never abort the add phase.
                    _logger.Warn(ex, "Skipping indexer candidate '{0}': definition could not be resolved", definition?.Name);
                }
            }

            return candidates;
        }

        private IndexerCandidate MapCandidate(IndexerDefinition definition, Dictionary<string, IndexerDefinition> definitions)
        {
            var cardigannSettings = definition.Settings as CardigannSettings;
            var cardigannDefinition = cardigannSettings == null
                ? null
                : _definitionService.GetCachedDefinition(cardigannSettings.DefinitionFile);

            if (cardigannSettings != null && cardigannDefinition == null)
            {
                _logger.Debug("Skipping indexer '{0}': definition file '{1}' is not cached", definition.Name, cardigannSettings.DefinitionFile);
                return null;
            }

            definitions[IndexerCandidate.GetKey(definition)] = definition;

            return new IndexerCandidate
            {
                Key = IndexerCandidate.GetKey(definition),
                Name = definition.Name,
                Protocol = definition.Protocol,
                Privacy = definition.Privacy,
                Language = definition.Language,
                CategoryIds = GetCategoryIds(definition),
                RequiresCredentials = IndexerCredentialDetector.RequiresCredentials(definition, cardigannDefinition)
            };
        }

        private static HashSet<int> GetCategoryIds(IndexerDefinition definition)
        {
            var categories = definition.Capabilities?.Categories?.GetTorznabCategoryList();

            if (categories == null)
            {
                return new HashSet<int>();
            }

            return categories.Select(c => c.Id).ToHashSet();
        }
    }
}
