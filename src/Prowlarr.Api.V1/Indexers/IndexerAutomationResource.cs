using System.Collections.Generic;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Automation;
using Prowlarr.Http.REST;

namespace Prowlarr.Api.V1.Indexers
{
    public class IndexerAutomationResource : RestResource
    {
        public bool Enabled { get; set; }
        public List<DownloadProtocol> Protocols { get; set; } = new List<DownloadProtocol>();
        public List<string> Languages { get; set; } = new List<string>();
        public List<IndexerPrivacy> Privacy { get; set; } = new List<IndexerPrivacy>();
        public List<int> Categories { get; set; } = new List<int>();
        public int TestTimeoutSeconds { get; set; }
        public int RetryDays { get; set; }
        public int RemovalDays { get; set; }
        public int MaxAddsPerPass { get; set; }
        public bool IsValid { get; set; }
    }

    public static class IndexerAutomationResourceMapper
    {
        public static IndexerAutomationResource ToResource(this IndexerAutomationSettings model)
        {
            if (model == null)
            {
                return null;
            }

            return new IndexerAutomationResource
            {
                Id = 1,
                Enabled = model.Enabled,
                Protocols = model.Protocols,
                Languages = model.Languages,
                Privacy = model.Privacy,
                Categories = model.Categories,
                TestTimeoutSeconds = model.TestTimeoutSeconds,
                RetryDays = model.RetryDays,
                RemovalDays = model.RemovalDays,
                MaxAddsPerPass = model.MaxAddsPerPass,
                IsValid = model.IsValid
            };
        }

        public static IndexerAutomationSettings ToModel(this IndexerAutomationResource resource)
        {
            if (resource == null)
            {
                return null;
            }

            return new IndexerAutomationSettings
            {
                Enabled = resource.Enabled,
                Protocols = resource.Protocols ?? new List<DownloadProtocol>(),
                Languages = resource.Languages ?? new List<string>(),
                Privacy = resource.Privacy ?? new List<IndexerPrivacy>(),
                Categories = resource.Categories ?? new List<int>(),
                TestTimeoutSeconds = resource.TestTimeoutSeconds,
                RetryDays = resource.RetryDays,
                RemovalDays = resource.RemovalDays,
                MaxAddsPerPass = resource.MaxAddsPerPass
            };
        }
    }
}
