using System.Text.Json.Serialization;

namespace WeKnora.OntologyReasoner.Core.Models;

public record MicroTBox
{
    [JsonPropertyName("classes")]
    public List<ClassDecl> Classes { get; init; } = [];

    [JsonPropertyName("properties")]
    public List<PropertyDecl> Properties { get; init; } = [];

    [JsonPropertyName("shapes")]
    public List<ShapeDecl> Shapes { get; init; } = [];

    [JsonPropertyName("aliases")]
    public Dictionary<string, List<string>> Aliases { get; init; } = new();

    [JsonPropertyName("axioms")]
    public List<FreeAxiom> Axioms { get; init; } = [];

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }
}

public record ClassDecl
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("subClassOf")]
    public string? SubClassOf { get; init; }

    [JsonPropertyName("disjointWith")]
    public List<string> DisjointWith { get; init; } = [];

    [JsonPropertyName("evidence")]
    public required string Evidence { get; init; }
}

public record PropertyDecl
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("domain")]
    public required string Domain { get; init; }

    [JsonPropertyName("range")]
    public required string Range { get; init; }

    [JsonPropertyName("characteristics")]
    public List<string> Characteristics { get; init; } = [];

    [JsonPropertyName("inverseOf")]
    public string? InverseOf { get; init; }

    [JsonPropertyName("evidence")]
    public required string Evidence { get; init; }
}

public record ShapeDecl
{
    [JsonPropertyName("target_class")]
    public required string TargetClass { get; init; }

    [JsonPropertyName("constraints")]
    public List<ShapeConstraint> Constraints { get; init; } = [];

    [JsonPropertyName("evidence")]
    public required string Evidence { get; init; }
}

public record ShapeConstraint
{
    [JsonPropertyName("property")]
    public required string Property { get; init; }

    [JsonPropertyName("min_count")]
    public int? MinCount { get; init; }

    [JsonPropertyName("max_count")]
    public int? MaxCount { get; init; }

    [JsonPropertyName("datatype")]
    public string? Datatype { get; init; }

    [JsonPropertyName("in_values")]
    public List<string>? InValues { get; init; }
}

public record FreeAxiom
{
    [JsonPropertyName("statement")]
    public required string Statement { get; init; }

    [JsonPropertyName("evidence")]
    public required string Evidence { get; init; }
}

public record TripleDto
{
    [JsonPropertyName("s")]
    public required string S { get; init; }

    [JsonPropertyName("p")]
    public required string P { get; init; }

    [JsonPropertyName("o")]
    public required string O { get; init; }
}
