using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Automation;
using NzbDrone.Core.Messaging.Commands;
using Prowlarr.Http;

namespace Prowlarr.Api.V1.Indexers
{
    [V1ApiController("indexer/automation")]
    public class IndexerAutomationController : Controller
    {
        private readonly IIndexerAutomationSettingsService _settingsService;
        private readonly IIndexerAutomationService _automationService;
        private readonly IIndexerAutomationStateRepository _stateRepository;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IManageCommandQueue _commandQueueManager;

        public IndexerAutomationController(IIndexerAutomationSettingsService settingsService,
                                           IIndexerAutomationService automationService,
                                           IIndexerAutomationStateRepository stateRepository,
                                           IIndexerFactory indexerFactory,
                                           IManageCommandQueue commandQueueManager)
        {
            _settingsService = settingsService;
            _automationService = automationService;
            _stateRepository = stateRepository;
            _indexerFactory = indexerFactory;
            _commandQueueManager = commandQueueManager;
        }

        [HttpGet]
        [Produces("application/json")]
        public IndexerAutomationResource GetSettings()
        {
            return _settingsService.Get().ToResource();
        }

        [HttpPut]
        [Consumes("application/json")]
        [Produces("application/json")]
        public ActionResult<IndexerAutomationResource> SaveSettings([FromBody] IndexerAutomationResource resource)
        {
            if (resource == null)
            {
                return BadRequest("A settings body is required");
            }

            _settingsService.Save(resource.ToModel());

            // Return the persisted, clamped values rather than the client's request.
            return Accepted(_settingsService.Get().ToResource());
        }

        [HttpGet("state")]
        [Produces("application/json")]
        public List<IndexerAutomationStateResource> GetState()
        {
            // Names come from the persisted indexers only. This still maps Cardigann definitions, but it
            // avoids the candidate enumeration (and its potential network fetch) on every state poll.
            var names = _indexerFactory.All()
                .GroupBy(IndexerCandidate.GetKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

            var states = _stateRepository.All().ToList();

            // Rows for indexers that are no longer installed still need a readable label in the UI.
            foreach (var state in states)
            {
                if (!names.ContainsKey(state.Key))
                {
                    names[state.Key] = GetDisplayName(state.Key);
                }
            }

            return states.ToResource(names);
        }

        private static string GetDisplayName(string key)
        {
            var name = key.StartsWith("Cardigann:", StringComparison.OrdinalIgnoreCase)
                ? key.Substring("Cardigann:".Length)
                : key;

            var separator = name.IndexOf(':');

            if (separator >= 0 && separator < name.Length - 1)
            {
                name = name.Substring(separator + 1);
            }

            return name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 4)
                : name;
        }

        [HttpPost("run")]
        [Produces("application/json")]
        public IActionResult Run()
        {
            _commandQueueManager.Push(new IndexerAutomationCommand(), CommandPriority.Normal, CommandTrigger.Manual);

            return Accepted();
        }

        [HttpDelete("state/{key}")]
        [Produces("application/json")]
        public IActionResult ClearSuppression(string key)
        {
            // Executed by the service so the write is serialised with the pass and the delete handler.
            return _automationService.ClearSuppression(key) ? Ok() : NotFound();
        }
    }
}
