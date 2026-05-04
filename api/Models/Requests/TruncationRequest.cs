namespace RusticalandOPS.Api.Models.Requests;

using System.Text.Json.Serialization;

public sealed class TruncationRequest
{
    [JsonPropertyName("beforeDateUtc")] public DateTime? BeforeDateUtc { get; set; }
    [JsonPropertyName("dryRun")] public bool DryRun { get; set; }
}
