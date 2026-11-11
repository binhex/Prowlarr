using System.Collections.Generic;

namespace NzbDrone.Core.Indexers.Automation
{
    public class IndexerAutomationSettings
    {
        public bool Enabled { get; set; }
        public List<DownloadProtocol> Protocols { get; set; } = new List<DownloadProtocol>();
        public List<string> Languages { get; set; } = new List<string>();
        public List<IndexerPrivacy> Privacy { get; set; } = new List<IndexerPrivacy>();
        public List<int> Categories { get; set; } = new List<int>();
        public int TestTimeoutSeconds { get; set; } = 60;
        public int RetryDays { get; set; } = 7;
        public int RemovalDays { get; set; } = 14;
        public int MaxAddsPerPass { get; set; } = 3;

        // Not persisted: set when a stored criteria list could not be parsed, so the pass can fail closed.
        public bool IsValid { get; set; } = true;
    }
}
