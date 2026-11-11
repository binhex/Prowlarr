using System;
using System.Linq;

namespace NzbDrone.Core.Indexers.Automation
{
    public static class IndexerCriteriaMatcher
    {
        public static bool Matches(IndexerCandidate candidate, IndexerAutomationSettings settings)
        {
            if (settings.Protocols.Count > 0 && !settings.Protocols.Contains(candidate.Protocol))
            {
                return false;
            }

            if (settings.Privacy.Count > 0 && !settings.Privacy.Contains(candidate.Privacy))
            {
                return false;
            }

            if (settings.Languages.Count > 0 && !MatchesLanguage(candidate.Language, settings))
            {
                return false;
            }

            if (settings.Categories.Count > 0 && !candidate.CategoryIds.Overlaps(settings.Categories))
            {
                return false;
            }

            return true;
        }

        private static bool MatchesLanguage(string language, IndexerAutomationSettings settings)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return false;
            }

            return settings.Languages.Any(selected => selected.Equals(language, StringComparison.OrdinalIgnoreCase));
        }
    }
}
