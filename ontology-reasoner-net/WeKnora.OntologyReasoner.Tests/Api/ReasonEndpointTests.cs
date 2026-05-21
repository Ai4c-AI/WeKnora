using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WeKnora.OntologyReasoner.Api.Endpoints;
using WeKnora.OntologyReasoner.Core.Assembly;
using WeKnora.OntologyReasoner.Core.Models;
using WeKnora.OntologyReasoner.Core.Storage;
using Xunit;

namespace WeKnora.OntologyReasoner.Tests.Api;

public class ReasonEndpointTests
{
    [Fact]
    public async Task Handle_ReturnsReasonResponseAsOkJson()
    {
        var request = new ReasonRequest
        {
            TenantId = 1,
            KnowledgeBaseIds = ["kb-1"],
            ChunkIds = Enumerable.Range(0, 51).Select(i => $"chunk-{i}").ToList(),
            Query = new ReasonQuery { Type = "entailment", Body = "ASK" },
        };
        var assembler = new SliceAssembler(new PostgresOntologyRepo("Host=invalid;Username=invalid;Password=invalid;Database=invalid"));

        var result = await ReasonEndpoint.Handle(request, assembler);

        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var response = await JsonSerializer.DeserializeAsync<ReasonResponse>(context.Response.Body);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.NotNull(response);
        Assert.Equal("error", response.Status);
        Assert.Contains("chunk_ids exceeds 50 limit", response.Warnings);
    }
}
