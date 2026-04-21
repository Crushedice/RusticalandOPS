using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Core.Interaction;

internal sealed class ResponseComposer : IResponseComposer
{
    public string Compose(ToolExecutionContext context, ToolExecutionResult result)
    {
        if (result.Success)
        {
            return result.Message;
        }

        if (!string.IsNullOrWhiteSpace(result.ErrorCode) && result.ErrorCode == "clarification_required")
        {
            return result.Message;
        }

        return $"Operation could not be completed: {result.Message}";
    }
}