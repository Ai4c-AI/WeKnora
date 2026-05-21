using System.Text.Json;
using WeKnora.OntologyReasoner.Core.Models;
using Xunit;

namespace WeKnora.OntologyReasoner.Tests.Models;

public class CoreModelJsonTests
{
    [Fact]
    public void MicroTBox_DeserializesGoJsonShape()
    {
        const string json = """
            {
              "classes": [
                { "id": "Person", "label": "Person", "subClassOf": "Agent", "disjointWith": ["Place"], "evidence": "Romeo is a person" }
              ],
              "properties": [
                { "id": "loves", "label": "loves", "domain": "Person", "range": "Person", "characteristics": ["symmetric"], "inverseOf": "lovedBy", "evidence": "Romeo loves Juliet" }
              ],
              "shapes": [
                { "target_class": "Person", "constraints": [{ "property": "name", "min_count": 1, "max_count": 1, "datatype": "string", "in_values": ["Romeo"] }], "evidence": "Romeo" }
              ],
              "aliases": { "Person": ["Human"] },
              "axioms": [{ "statement": "Person disjointWith Place", "evidence": "Romeo is not a place" }],
              "confidence": 0.92
            }
            """;

        var tbox = JsonSerializer.Deserialize<MicroTBox>(json);

        Assert.NotNull(tbox);
        Assert.Equal("Person", tbox.Classes[0].Id);
        Assert.Equal("Agent", tbox.Classes[0].SubClassOf);
        Assert.Equal("Place", tbox.Classes[0].DisjointWith[0]);
        Assert.Equal("loves", tbox.Properties[0].Id);
        Assert.Equal("lovedBy", tbox.Properties[0].InverseOf);
        Assert.Equal("Person", tbox.Shapes[0].TargetClass);
        Assert.Equal("name", tbox.Shapes[0].Constraints[0].Property);
        Assert.Equal("Human", tbox.Aliases["Person"][0]);
        Assert.Equal("Person disjointWith Place", tbox.Axioms[0].Statement);
        Assert.Equal(0.92, tbox.Confidence);
    }

    [Fact]
    public void ReasonRequest_SerializesTenantIdAndQueryShape()
    {
        var request = new ReasonRequest
        {
            TenantId = ulong.MaxValue,
            KnowledgeBaseIds = ["kb-1"],
            ChunkIds = ["chunk-1"],
            InstanceFacts = [new TripleDto { S = "Romeo", P = "loves", O = "Juliet" }],
            Query = new ReasonQuery { Type = "entailment", Body = "Who loves Juliet?" },
        };

        var json = JsonSerializer.Serialize(request);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(ulong.MaxValue, root.GetProperty("tenant_id").GetUInt64());
        Assert.Equal("kb-1", root.GetProperty("knowledge_base_ids")[0].GetString());
        Assert.Equal("chunk-1", root.GetProperty("chunk_ids")[0].GetString());
        Assert.Equal("Romeo", root.GetProperty("instance_facts")[0].GetProperty("s").GetString());
        Assert.Equal("entailment", root.GetProperty("query").GetProperty("type").GetString());
        Assert.Equal("n3-extended", root.GetProperty("query").GetProperty("profile").GetString());
    }

    [Fact]
    public void ReasonResponse_SerializesReasoningResultShape()
    {
        var response = new ReasonResponse
        {
            Status = "ok",
            Results = [new Dictionary<string, object?> { ["answer"] = "Romeo" }],
            InferredTriples = [new TripleDto { S = "Romeo", P = "type", O = "Person" }],
            DataSource = "ontology-slice",
            EvidenceChunks = ["chunk-1"],
            Warnings = ["partial"],
            ElapsedMs = 42,
        };

        var json = JsonSerializer.Serialize(response);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("Romeo", root.GetProperty("results")[0].GetProperty("answer").GetString());
        Assert.Equal("Person", root.GetProperty("inferred_triples")[0].GetProperty("o").GetString());
        Assert.Equal("ontology-slice", root.GetProperty("data_source").GetString());
        Assert.Equal("chunk-1", root.GetProperty("evidence_chunks")[0].GetString());
        Assert.Equal("partial", root.GetProperty("warnings")[0].GetString());
        Assert.Equal(42, root.GetProperty("elapsed_ms").GetInt64());
    }
}
