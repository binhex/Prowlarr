using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Indexers.Automation
{
    public class IndexerAutomationCommand : Command
    {
        public override bool SendUpdatesToClient => true;
    }
}
