using System.Collections.Generic;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Security;

namespace NzbDrone.Core.Configuration
{
    public interface IConfigService
    {
        void SaveConfigDictionary(Dictionary<string, object> configValues);

        bool IsDefined(string key);

        //History
        int HistoryCleanupDays { get; set; }

        //UI
        int FirstDayOfWeek { get; set; }
        string CalendarWeekColumnHeader { get; set; }

        string ShortDateFormat { get; set; }
        string LongDateFormat { get; set; }
        string TimeFormat { get; set; }
        bool ShowRelativeDates { get; set; }
        bool EnableColorImpairedMode { get; set; }
        string UILanguage { get; set; }

        //Internal
        string PlexClientIdentifier { get; }

        //Forms Auth
        string RijndaelPassphrase { get; }
        string HmacPassphrase { get; }
        string RijndaelSalt { get; }
        string HmacSalt { get; }

        //Link Protection
        string DownloadProtectionKey { get; }

        //Proxy
        bool ProxyEnabled { get; }
        ProxyType ProxyType { get; }
        string ProxyHostname { get; }
        int ProxyPort { get; }
        string ProxyUsername { get; }
        string ProxyPassword { get; }
        string ProxyBypassFilter { get; }
        bool ProxyBypassLocalAddresses { get; }

        // Backups
        string BackupFolder { get; }
        int BackupInterval { get; }
        int BackupRetention { get; }

        // Indexers
        bool LogIndexerResponse { get; set; }

        // Indexer Automation
        bool IndexerAutomationEnabled { get; set; }
        string IndexerAutomationProtocols { get; set; }
        string IndexerAutomationLanguages { get; set; }
        string IndexerAutomationPrivacy { get; set; }
        string IndexerAutomationCategories { get; set; }
        int IndexerAutomationTestTimeoutSeconds { get; set; }
        int IndexerAutomationRetryDays { get; set; }
        int IndexerAutomationRemovalDays { get; set; }
        int IndexerAutomationMaxAddsPerPass { get; set; }

        CertificateValidationType CertificateValidation { get; }
        string ApplicationUrl { get; }
    }
}
