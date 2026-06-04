using WeKnora.OntologyReasoner.Core.Models;

namespace WeKnora.OntologyReasoner.Core.Storage;

public interface IOntologyRepo
{
    Task<List<OntologyChunkData>> GetChunkOntologies(long tenantId, IReadOnlyList<string> knowledgeBaseIds, IReadOnlyList<string> chunkIds);

    Task<Dictionary<string, string>> GetCanonicalMap(long tenantId, string knowledgeBaseId);
}

public record OntologyChunkData(MicroTBox TBox, List<TripleDto> Facts, string ChunkId, string KnowledgeBaseId);
