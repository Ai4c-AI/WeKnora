using System.Text.Json.Serialization;

namespace WeKnora.OntologyReasoner.Core.Models;

public record ReasonRequest
{
    [JsonPropertyName("tenant_id")]
    public ulong TenantId { get; init; }

    [JsonPropertyName("knowledge_base_ids")]
    public List<string> KnowledgeBaseIds { get; init; } = [];

    [JsonPropertyName("chunk_ids")]
    public List<string> ChunkIds { get; init; } = [];

    [JsonPropertyName("instance_facts")]
    public List<TripleDto>? InstanceFacts { get; init; }

    [JsonPropertyName("query")]
    public required ReasonQuery Query { get; init; }
}

public record ReasonQuery
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("body")]
    public required string Body { get; init; }

    [JsonPropertyName("profile")]
    public string Profile { get; init; } = "n3-extended";
}
