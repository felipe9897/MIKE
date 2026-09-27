using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Mike.Common
{
    public enum PermissionLevel
    {
        R0 = 0, // Read-only (Public)
        R1 = 1, // Read-only (Private/User)
        R2 = 2, // Write (User)
        R3 = 3, // Write (Admin)
        R4 = 4   // System/Destructive
    }

    public class ToolDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public PermissionLevel RequiredLevel { get; set; }
        public int MaxArgumentBytes { get; set; } = 64 * 1024;
        public Func<string, bool>? ValidateArguments { get; set; }
        public Func<string, Task<string>> Action { get; set; } = async (args) => "Not implemented";
    }

    public class ToolBroker
    {
        private readonly Dictionary<string, ToolDefinition> _tools = new Dictionary<string, ToolDefinition>();
        private readonly IApprovalService? _approvalService;

        public ToolBroker(IApprovalService? approvalService = null)
        {
            _approvalService = approvalService;
        }

        public void RegisterTool(ToolDefinition tool)
        {
            if (tool is null) throw new ArgumentNullException(nameof(tool));
            if (string.IsNullOrWhiteSpace(tool.Id) || tool.Id.Length > 128 ||
                tool.Id.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.')))
                throw new ArgumentException("Tool IDs must be short and use only letters, digits, '.', '_' or '-'.", nameof(tool));
            if (!Enum.IsDefined(tool.RequiredLevel) || tool.MaxArgumentBytes <= 0 || tool.MaxArgumentBytes > MikeIpcProtocol.MaxPayloadSize)
                throw new ArgumentException("Invalid tool permission or argument limit.", nameof(tool));
            if (tool.Action is null) throw new ArgumentException("Tool action is required.", nameof(tool));
            _tools[tool.Id] = tool;
        }

        public async Task<string> ExecuteToolAsync(string toolId, string args, PermissionLevel currentLevel, string? approvalId = null)
        {
            if (string.IsNullOrWhiteSpace(toolId) || args is null || !Enum.IsDefined(currentLevel))
                return Error("Invalid tool request.");
            if (!_tools.TryGetValue(toolId, out var tool))
                return Error("Tool not found.");
            if (Encoding.UTF8.GetByteCount(args) > tool.MaxArgumentBytes)
                return Error("Tool arguments exceed the configured limit.");
            if (tool.ValidateArguments is not null && !tool.ValidateArguments(args))
                return Error("Tool arguments were rejected by policy.");

            if (currentLevel < tool.RequiredLevel)
            {
                if (!string.IsNullOrEmpty(approvalId) && _approvalService?.TryConsumeApproval(approvalId, toolId, args) == true)
                {
                    // Approved! Proceed to execution
                }
                else
                {
                    if (_approvalService == null)
                    {
                        return System.Text.Json.JsonSerializer.Serialize(new {
                            type = "permission_required",
                            toolId = toolId,
                            requiredLevel = tool.RequiredLevel,
                            message = $"This action requires {tool.RequiredLevel} privileges."
                        });
                    }

                    string newApprovalId = _approvalService.RequestApproval(toolId, args);
                    return System.Text.Json.JsonSerializer.Serialize(new {
                        type = "approval_required",
                        approvalId = newApprovalId,
                        toolId = toolId,
                        requiredLevel = tool.RequiredLevel,
                        message = $"This action requires {tool.RequiredLevel} privileges. Please approve this request."
                    });
                }
            }

            try
            {
                string result = await tool.Action(args);
                return System.Text.Json.JsonSerializer.Serialize(new {
                    type = "success",
                    result = result
                });
            }
            catch (Exception)
            {
                return System.Text.Json.JsonSerializer.Serialize(new {
                    type = "error",
                    message = "Tool execution failed."
                });
            }
        }

        private static string Error(string message) => System.Text.Json.JsonSerializer.Serialize(new { type = "error", message });

        public List<ToolDefinition> GetAvailableTools(PermissionLevel level)
        {
            return _tools.Values.Where(t => t.RequiredLevel <= level).ToList();
        }
    }
}
