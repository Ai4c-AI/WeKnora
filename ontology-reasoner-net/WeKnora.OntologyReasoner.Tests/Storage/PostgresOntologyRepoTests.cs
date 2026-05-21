using WeKnora.OntologyReasoner.Core.Storage;
using Xunit;

namespace WeKnora.OntologyReasoner.Tests.Storage;

public class PostgresOntologyRepoTests
{
    [Fact]
    public async Task GetChunkOntologies_WithEmptyChunkIds_ReturnsEmptyWithoutConnecting()
    {
        var repo = new PostgresOntologyRepo("Host=invalid;Username=invalid;Password=invalid;Database=invalid");

        var result = await repo.GetChunkOntologies([]);

        Assert.Empty(result);
    }
}
