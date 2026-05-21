using System.Diagnostics;
using WeKnora.OntologyReasoner.Core.Models;
using WeKnora.OntologyReasoner.Core.Storage;

namespace WeKnora.OntologyReasoner.Core.Assembly;

public class SliceAssembler
{
    private readonly PostgresOntologyRepo _repo;

    public SliceAssembler(PostgresOntologyRepo repo)
    {
        _repo = repo;
    }

    public async Task<ReasonResponse> Reason(ReasonRequest request)
    {
        var sw = Stopwatch.StartNew();
        if (request.ChunkIds.Count > 50)
        {
            return new ReasonResponse
            {
                Status = "error",
                DataSource = "none",
                Warnings = ["chunk_ids exceeds 50 limit"],
                ElapsedMs = sw.ElapsedMilliseconds,
            };
        }

        var chunkData = await _repo.GetChunkOntologies(request.ChunkIds);
        if (chunkData.Count == 0)
        {
            return new ReasonResponse
            {
                Status = "ok",
                DataSource = "fetched",
                Warnings = ["No ontology data found for provided chunks"],
                ElapsedMs = sw.ElapsedMilliseconds,
            };
        }

        return new ReasonResponse
        {
            Status = "ok",
            DataSource = request.InstanceFacts is { Count: > 0 } ? "provided" : "fetched",
            EvidenceChunks = chunkData.Select(item => item.ChunkId).ToList(),
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }
}
