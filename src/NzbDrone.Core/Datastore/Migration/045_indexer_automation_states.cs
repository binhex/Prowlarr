using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(045)]
    public class indexer_automation_states : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.TableForModel("IndexerAutomationStates")
                  .WithColumn("Key").AsString().NotNullable().Unique()
                  .WithColumn("IndexerId").AsInt32().Nullable()
                  .WithColumn("FailureSince").AsDateTimeOffset().Nullable()
                  .WithColumn("LastFailureAt").AsDateTimeOffset().Nullable()
                  .WithColumn("LastAttemptAt").AsDateTimeOffset().Nullable()
                  .WithColumn("NextAttemptAt").AsDateTimeOffset().Nullable()
                  .WithColumn("LastAttemptResult").AsString().Nullable()
                  .WithColumn("LastEscalationLevel").AsInt32().Nullable()
                  .WithColumn("TrackingSince").AsDateTimeOffset().Nullable()
                  .WithColumn("RemovedAt").AsDateTimeOffset().Nullable()
                  .WithColumn("Suppressed").AsBoolean().NotNullable().WithDefaultValue(false)
                  .WithColumn("SuppressedAt").AsDateTimeOffset().Nullable();

            Create.Index("IX_IndexerAutomationStates_IndexerId")
                  .OnTable("IndexerAutomationStates")
                  .OnColumn("IndexerId").Ascending();
        }
    }
}
