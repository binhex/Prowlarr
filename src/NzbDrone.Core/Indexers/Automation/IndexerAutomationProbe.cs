using System;
using System.Linq;
using System.Threading.Tasks;
using NLog;

namespace NzbDrone.Core.Indexers.Automation
{
    public interface IIndexerAutomationProbe
    {
        bool TryProbe(IndexerDefinition definition, int timeoutSeconds, out string failureMessage);
    }

    public class IndexerAutomationProbe : IIndexerAutomationProbe
    {
        private readonly IIndexerFactory _indexerFactory;
        private readonly Logger _logger;

        public IndexerAutomationProbe(IIndexerFactory indexerFactory, Logger logger)
        {
            _indexerFactory = indexerFactory;
            _logger = logger;
        }

        public bool TryProbe(IndexerDefinition definition, int timeoutSeconds, out string failureMessage)
        {
            failureMessage = null;

            var task = Task.Run(() => _indexerFactory.Test(definition));

            try
            {
                if (!task.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    // The indexer test API has no cancellation token, so the abandoned probe keeps running.
                    // Observe its eventual failure so it cannot surface as an unobserved task exception.
                    task.ContinueWith(t => _logger.Debug(t.Exception, "Abandoned indexer automation probe for '{0}' finished after timeout", definition.Name), TaskContinuationOptions.OnlyOnFaulted);

                    failureMessage = $"Probe timed out after {timeoutSeconds} seconds";
                    _logger.Debug("Indexer automation probe for '{0}' timed out after {1}s", definition.Name, timeoutSeconds);

                    return false;
                }

                var result = task.Result;

                if (result == null)
                {
                    failureMessage = "Probe returned no result";

                    return false;
                }

                if (result.IsValid)
                {
                    return true;
                }

                failureMessage = string.Join("; ", result.Errors.Select(e => e.ErrorMessage));

                return false;
            }
            catch (AggregateException ex)
            {
                failureMessage = ex.Flatten().InnerExceptions.FirstOrDefault()?.Message ?? ex.Message;

                return false;
            }
        }
    }
}
