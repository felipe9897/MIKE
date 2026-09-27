using System;

namespace Mike.Common
{
    public interface IApprovalService
    {
        string RequestApproval(string toolId, string args);
        bool IsApproved(string approvalId);
        bool TryConsumeApproval(string approvalId, string toolId, string args);
        void MarkApproved(string approvalId);
    }
}
