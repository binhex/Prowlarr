using System.Collections.Generic;
using NzbDrone.Core.Indexers.Definitions.Cardigann;

namespace NzbDrone.Core.Indexers.Automation
{
    public class IndexerCandidate
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public DownloadProtocol Protocol { get; set; }
        public IndexerPrivacy Privacy { get; set; }
        public string Language { get; set; }
        public HashSet<int> CategoryIds { get; set; } = new HashSet<int>();
        public bool RequiresCredentials { get; set; }

        public static string GetKey(IndexerDefinition definition)
        {
            if (definition.Settings is CardigannSettings cardigann)
            {
                return $"Cardigann:{cardigann.DefinitionFile}";
            }

            // Keyed by the immutable implementation, never the user-editable display name: renaming an
            // indexer must not change its identity (that would let the add phase create a duplicate and
            // would lose its suppression). Preset families (Newznab/Torznab/TorrentRss) therefore share
            // one key per implementation; only the bound instance is tracked, which the spec documents.
            return definition.Implementation;
        }
    }
}
