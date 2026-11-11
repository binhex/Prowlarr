using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Indexers.Automation
{
    public static class IndexerAutomationAttemptResult
    {
        public const string Passed = "Passed";
        public const string Failed = "Failed";
        public const string Removed = "Removed";
        public const string Suppressed = "Suppressed";
    }

    public class IndexerAutomationState : ModelBase
    {
        public string Key { get; set; }
        public int? IndexerId { get; set; }
        public DateTime? FailureSince { get; set; }
        public DateTime? LastFailureAt { get; set; }
        public DateTime? LastAttemptAt { get; set; }
        public DateTime? NextAttemptAt { get; set; }
        public string LastAttemptResult { get; set; }
        public int? LastEscalationLevel { get; set; }
        public DateTime? TrackingSince { get; set; }
        public DateTime? RemovedAt { get; set; }
        public bool Suppressed { get; set; }
        public DateTime? SuppressedAt { get; set; }
    }
}
