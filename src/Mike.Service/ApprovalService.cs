using System;
using Mike.Common;
using Microsoft.Extensions.Logging;

namespace Mike.Service
{
    public class ApprovalService : IApprovalService
    {
        private readonly ApprovalRegistry _registry;
        private readonly ILogger _logger;

        public ApprovalService(ApprovalRegistry registry, ILogger logger)
        {
            _registry = registry;
            _logger = logger;
        }

        public string RequestApproval(string toolId, string args)
        {
            string id = _registry.CreateRequest(toolId, args);
            _logger.LogInformation($"Approval requested for tool {toolId} (ID: {id})");
            return id;
        }

        public bool IsApproved(string approvalId)
        {
            return _registry.GetRequest(approvalId)?.IsApproved ?? false;
        }

        public bool TryConsumeApproval(string approvalId, string toolId, string args) =>
            _registry.TryConsume(approvalId, toolId, args);

        public void MarkApproved(string approvalId)
        {
            if (_registry.TryApprove(approvalId))
            {
                _logger.LogInformation($"Approval {approvalId} granted.");
            }
            else
            {
                _logger.LogWarning($"Failed to approve request {approvalId} (expired or not found).");
            }
        }
    }
}
