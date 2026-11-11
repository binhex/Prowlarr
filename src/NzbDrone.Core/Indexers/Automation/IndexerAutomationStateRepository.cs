using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Indexers.Automation
{
    public interface IIndexerAutomationStateRepository : IBasicRepository<IndexerAutomationState>
    {
        IndexerAutomationState FindByKey(string key);
    }

    public class IndexerAutomationStateRepository : BasicRepository<IndexerAutomationState>, IIndexerAutomationStateRepository
    {
        public IndexerAutomationStateRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public IndexerAutomationState FindByKey(string key)
        {
            return Query(x => x.Key == key).SingleOrDefault();
        }
    }
}
