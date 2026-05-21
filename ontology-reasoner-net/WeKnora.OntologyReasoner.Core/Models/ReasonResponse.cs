using System.Text.Json.Serialization;

namespace WeKnora.OntologyReasoner.Core.Models;

public record ReasonResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("results")]
    public List<Dictionary<string, object?>> Results { get; init; } = [];

    [JsonPropertyName("inferred_triples")]
    public List<TripleDto> InferredTriples { get; init; } = [];

    [JsonPropertyName("data_source")]
    public required string DataSource { get; init; }

    [JsonPropertyName("evidence_chunks")]
    public List<string> EvidenceChunks { get; init; } = [];

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; init; } = [];

    [JsonPropertyName("elapsed_ms")]
    public long ElapsedMs { get; init; }
}
