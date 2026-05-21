using System.Text.Json;
using Dapper;
using Npgsql;
using WeKnora.OntologyReasoner.Core.Models;

namespace WeKnora.OntologyReasoner.Core.Storage;

public class PostgresOntologyRepo
{
    private readonly string _connectionString;

    public PostgresOntologyRepo(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<List<(MicroTBox TBox, List<TripleDto> Facts, string ChunkId)>> GetChunkOntologies(IReadOnlyList<string> chunkIds)
    {
        if (chunkIds.Count == 0)
        {
            return [];
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var rows = await conn.QueryAsync<(string id, string? ontology_json, string? instance_facts_json)>(
            "SELECT id, ontology_json::text, instance_facts_json::text FROM chunks WHERE id = ANY(@ids) AND ontology_json IS NOT NULL",
            new { ids = chunkIds.ToArray() });

        var results = new List<(MicroTBox, List<TripleDto>, string)>();
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
            results.Add((tbox, facts, row.id));
        }

        return results;
    }

    public async Task<Dictionary<string, string>> GetCanonicalMap(ulong tenantId, string knowledgeBaseId)
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
