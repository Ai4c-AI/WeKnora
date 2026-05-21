using WeKnora.OntologyReasoner.Core.Assembly;
using WeKnora.OntologyReasoner.Core.Models;
using WeKnora.OntologyReasoner.Core.Storage;
using Xunit;

namespace WeKnora.OntologyReasoner.Tests.Assembly;

public class SliceAssemblerTests
{
    [Fact]
    public async Task Reason_WithTooManyChunkIds_ReturnsErrorWithoutConnecting()
    {
        var assembler = new SliceAssembler(new PostgresOntologyRepo("Host=invalid;Username=invalid;Password=invalid;Database=invalid"));
        var request = new ReasonRequest
        {
            TenantId = 1,
            KnowledgeBaseIds = ["kb-1"],
            ChunkIds = Enumerable.Range(0, 51).Select(i => $"chunk-{i}").ToList(),
            Query = new ReasonQuery { Type = "entailment", Body = "ASK" },
        };

        var response = await assembler.Reason(request);

        Assert.Equal("error", response.Status);
        Assert.Contains("chunk_ids exceeds 50 limit", response.Warnings);
    }

    [Fact]
    public async Task Reason_WithNoChunkIds_ReturnsNoOntologyWarning()
    {
        var assembler = new SliceAssembler(new PostgresOntologyRepo("Host=invalid;Username=invalid;Password=invalid;Database=invalid"));
        var request = new ReasonRequest
        {
            TenantId = 1,
            KnowledgeBaseIds = ["kb-1"],
            ChunkIds = [],
            Query = new ReasonQuery { Type = "entailment", Body = "ASK" },
        };

        var response = await assembler.Reason(request);

        Assert.Equal("ok", response.Status);
        Assert.Equal("fetched", response.DataSource);
        Assert.Contains("No ontology data found for provided chunks", response.Warnings);
    }
}
