using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Indexers.Automation;
using Prowlarr.Http.REST;

namespace Prowlarr.Api.V1.Indexers
{
    public class IndexerAutomationStateResource : RestResource
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public int? IndexerId { get; set; }
        public DateTime? FailureSince { get; set; }
        public DateTime? NextAttemptAt { get; set; }
        public string LastAttemptResult { get; set; }
        public DateTime? RemovedAt { get; set; }
        public bool Suppressed { get; set; }
    }

    public static class IndexerAutomationStateResourceMapper
    {
        public static IndexerAutomationStateResource ToResource(this IndexerAutomationState model, string name)
        {
            if (model == null)
            {
                return null;
            }

            return new IndexerAutomationStateResource
            {
                Id = model.Id,
                Key = model.Key,
                Name = name ?? model.Key,
                IndexerId = model.IndexerId,
                FailureSince = model.FailureSince,
                NextAttemptAt = model.NextAttemptAt,
                LastAttemptResult = model.LastAttemptResult,
                RemovedAt = model.RemovedAt,
                Suppressed = model.Suppressed
            };
        }

        public static List<IndexerAutomationStateResource> ToResource(this IEnumerable<IndexerAutomationState> models, IDictionary<string, string> names)
        {
            return models.Select(m => m.ToResource(names.TryGetValue(m.Key, out var name) ? name : null)).ToList();
        }
    }
}
