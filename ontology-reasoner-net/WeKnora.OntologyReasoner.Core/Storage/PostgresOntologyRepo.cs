using System.Text.Json;
using Dapper;
using Npgsql;
using WeKnora.OntologyReasoner.Core.Models;

namespace WeKnora.OntologyReasoner.Core.Storage;

public class PostgresOntologyRepo : IOntologyRepo
{
    private readonly string _connectionString;

    public PostgresOntologyRepo(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<List<OntologyChunkData>> GetChunkOntologies(
        long tenantId,
        IReadOnlyList<string> knowledgeBaseIds,
        IReadOnlyList<string> chunkIds)
    {
        if (chunkIds.Count == 0)
        {
            return [];
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var rows = await conn.QueryAsync<(string id, string knowledge_base_id, string? ontology_json, string? instance_facts_json)>(
            """
            SELECT id, knowledge_base_id, ontology_json::text, instance_facts_json::text
            FROM chunks
            WHERE tenant_id = @tenantId
              AND knowledge_base_id = ANY(@knowledgeBaseIds)
              AND id = ANY(@ids)
              AND ontology_json IS NOT NULL
            """,
            new { tenantId, knowledgeBaseIds = knowledgeBaseIds.ToArray(), ids = chunkIds.ToArray() });

        var results = new List<OntologyChunkData>();
        foreach (var row in rows)
        {
            var tbox = JsonSerializer.Deserialize<MicroTBox>(row.ontology_json!);
            if (tbox is null)
            {
                continue;
            }

            var facts = row.instance_facts_json is not null
                ? JsonSerializer.Deserialize<List<TripleDto>>(row.instance_facts_json) ?? []
                : [];
            results.Add(new OntologyChunkData(tbox, facts, row.id, row.knowledge_base_id));
        }

        return results;
    }

    public async Task<Dictionary<string, string>> GetCanonicalMap(long tenantId, string knowledgeBaseId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var rows = await conn.QueryAsync<(string canonical_id, string[] aliases)>(
            "SELECT canonical_id, aliases FROM ontology_canonical_map WHERE tenant_id = @tenantId AND knowledge_base_id = @knowledgeBaseId",
            new { tenantId, knowledgeBaseId });

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            map.TryAdd(row.canonical_id, row.canonical_id);
            foreach (var alias in row.aliases)
            {
                map.TryAdd(alias, row.canonical_id);
            }
        }

        return map;
    }
}
