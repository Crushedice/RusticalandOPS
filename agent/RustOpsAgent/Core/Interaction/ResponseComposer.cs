using Microsoft.SemanticKernel;
using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Core.Interaction;

internal sealed class ResponseComposer : IResponseComposer
{
    private readonly Kernel? _kernel;
    private readonly LlmSettings _settings;

    public ResponseComposer(Kernel? kernel, LlmSettings settings)
    {
        _kernel = kernel;
        _settings = settings;
    }

    public async Task<ComposedReply> ComposeAsync(ToolExecutionContext context, ToolExecutionResult result, CancellationToken cancellationToken)
    {
        var fallback = ComposeFallback(result);
        if (_kernel is null || !_settings.Enabled)
        {
            return new ComposedReply(
                fallback,
                "response-compose-fallback",
                false,
                false,
                _kernel is null ? "template_no_kernel" : "template_llm_disabled",
                fallback);
        }

        var payloadPreview = result.Payload?.ToString();
        if (!string.IsNullOrWhiteSpace(payloadPreview) && payloadPreview.Length > 1200)
        {
            payloadPreview = payloadPreview[..1200];
        }

        var prompt = $$"""
{{BuildSystemPrefix()}}
You are an operations reasoning assistant for an IT operations admin.
Write a concise operator response in plain text.
Requirements:
- Start with the direct outcome.
- Mention connector/source and next action when relevant.
- Do not invent facts beyond the provided data.
- Keep it under 120 words unless critical failure details require more.

Context:
- adminMessage: {{context.Message}}
- intent: {{context.Route.Intent}}
- selectedServer: {{result.SelectedServer ?? context.Route.Slots.ServerName ?? "unknown"}}
- success: {{result.Success}}
- errorCode: {{result.ErrorCode ?? "none"}}
- toolMessage: {{result.Message}}
- payloadPreview: {{payloadPreview ?? "none"}}

Return only the final reply text.
""";

        try
        {
            var response = await _kernel.InvokePromptAsync(prompt, cancellationToken: cancellationToken);
            var message = (response.GetValue<string>() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(message))
            {
                return new ComposedReply(fallback, "response-compose", true, false, "llm_empty_response", fallback);
            }

            return new ComposedReply(
                message,
                "response-compose",
                true,
                true,
                "llm",
                message.Length > 180 ? message[..180] : message);
        }
        catch (Exception ex)
        {
            return new ComposedReply(
                fallback,
                "response-compose",
                true,
                false,
                $"llm_error:{ex.GetType().Name}",
                fallback);
        }
    }

    private static string ComposeFallback(ToolExecutionResult result)
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

    private string BuildSystemPrefix()
    {
        if (!_settings.UseChatSystemPrompt || string.IsNullOrWhiteSpace(_settings.ChatSystemPrompt))
        {
            return string.Empty;
        }

        return $"System guidance:\n{_settings.ChatSystemPrompt!.Trim()}\n\n";
    }
}
