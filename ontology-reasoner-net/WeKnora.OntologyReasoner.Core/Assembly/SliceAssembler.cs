using System.Diagnostics;
using VDS.RDF;
using WeKnora.OntologyReasoner.Core.Aligner;
using WeKnora.OntologyReasoner.Core.Engine;
using WeKnora.OntologyReasoner.Core.Generators;
using WeKnora.OntologyReasoner.Core.Models;
using WeKnora.OntologyReasoner.Core.Storage;

namespace WeKnora.OntologyReasoner.Core.Assembly;

public class SliceAssembler
{
    private const string BaseNamespace = "http://weknora.local/ontology/";

    private readonly IOntologyRepo _repo;
    private readonly ReasoningEngine _reasoningEngine;
    private readonly ShaclValidator _shaclValidator;

    public SliceAssembler(IOntologyRepo repo)
        : this(repo, new ReasoningEngine(), new ShaclValidator())
    {
    }

    public SliceAssembler(IOntologyRepo repo, ReasoningEngine reasoningEngine, ShaclValidator shaclValidator)
    {
        _repo = repo;
        _reasoningEngine = reasoningEngine;
        _shaclValidator = shaclValidator;
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

        var chunkData = await _repo.GetChunkOntologies(request.TenantId, request.KnowledgeBaseIds, request.ChunkIds);
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

        var canonicalMaps = await GetCanonicalMaps(request);
        var alignedTBoxes = new List<MicroTBox>();
        var fetchedFacts = new List<TripleDto>();
        foreach (var item in chunkData)
        {
            var effectiveMap = BuildEffectiveMap(GetCanonicalMap(canonicalMaps, item.KnowledgeBaseId), item.TBox);
            alignedTBoxes.Add(CanonicalAligner.Align(item.TBox, effectiveMap));
            fetchedFacts.AddRange(AlignFacts(item.Facts, effectiveMap));
        }

        var warnings = new List<string>();
        var dataSource = request.InstanceFacts is { Count: > 0 } ? "provided" : "fetched";
        var alignedFacts = request.InstanceFacts is { Count: > 0 }
            ? AlignFacts(request.InstanceFacts, BuildSafeSharedMap(canonicalMaps, chunkData.Select(item => item.TBox), warnings))
            : fetchedFacts;
        var dataGraph = RdfGenerator.GenerateDataGraph(alignedFacts, BaseNamespace);
        var evidenceChunks = chunkData.Select(item => item.ChunkId).ToList();

        if (IsShaclRequest(request))
        {
            var response = ValidateShacl(alignedTBoxes, dataGraph, dataSource, evidenceChunks, sw);
            return response with { Warnings = response.Warnings.Concat(warnings).ToList() };
        }

        return ReasonEntailments(alignedTBoxes, dataGraph, dataSource, evidenceChunks, sw) with { Warnings = warnings };
    }

    private async Task<Dictionary<string, Dictionary<string, string>>> GetCanonicalMaps(ReasonRequest request)
    {
        var canonicalMaps = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var knowledgeBaseId in request.KnowledgeBaseIds)
        {
            canonicalMaps[knowledgeBaseId] = await _repo.GetCanonicalMap(request.TenantId, knowledgeBaseId);
        }

        return canonicalMaps;
    }

    private static Dictionary<string, string> GetCanonicalMap(
        IReadOnlyDictionary<string, Dictionary<string, string>> canonicalMaps,
        string knowledgeBaseId)
    {
        return canonicalMaps.TryGetValue(knowledgeBaseId, out var canonicalMap)
            ? canonicalMap
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> BuildEffectiveMap(
        IReadOnlyDictionary<string, string> canonicalMap,
        MicroTBox tbox)
    {
        var effectiveMap = new Dictionary<string, string>(canonicalMap, StringComparer.OrdinalIgnoreCase);
        foreach (var (canonical, aliases) in tbox.Aliases)
        {
            effectiveMap.TryAdd(canonical, canonical);
            foreach (var alias in aliases)
            {
                effectiveMap.TryAdd(alias, canonical);
            }
        }

        return effectiveMap;
    }

    private static Dictionary<string, string> BuildSafeSharedMap(
        IReadOnlyDictionary<string, Dictionary<string, string>> canonicalMaps,
        IEnumerable<MicroTBox> tboxes,
        List<string> warnings)
    {
        var candidates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var canonicalMap in canonicalMaps.Values)
        {
            AddCandidates(candidates, canonicalMap);
        }

        foreach (var tbox in tboxes)
        {
            AddCandidates(candidates, BuildEffectiveMap(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), tbox));
        }

        var sharedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, canonicals) in candidates)
        {
            if (canonicals.Count == 1)
            {
                sharedMap[alias] = canonicals.Single();
            }
            else
            {
                warnings.Add($"Canonical alias conflict for {alias}; leaving provided facts unchanged for that alias");
            }
        }

        return sharedMap;
    }

    private static void AddCandidates(
        Dictionary<string, HashSet<string>> candidates,
        IReadOnlyDictionary<string, string> canonicalMap)
    {
        foreach (var (alias, canonical) in canonicalMap)
        {
            if (!candidates.TryGetValue(alias, out var canonicals))
            {
                canonicals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                candidates[alias] = canonicals;
            }

            canonicals.Add(canonical);
        }
    }

    private static List<TripleDto> AlignFacts(IReadOnlyList<TripleDto> facts, IReadOnlyDictionary<string, string> canonicalMap)
    {
        return facts.Select(fact => fact with
        {
            P = Resolve(fact.P, canonicalMap),
            O = string.Equals(fact.P, "rdf:type", StringComparison.OrdinalIgnoreCase)
                ? Resolve(fact.O, canonicalMap)
                : fact.O,
        }).ToList();
    }

    private static string Resolve(string id, IReadOnlyDictionary<string, string> canonicalMap)
    {
        return canonicalMap.TryGetValue(id, out var canonical) ? canonical : id;
    }

    private ReasonResponse ReasonEntailments(
        IReadOnlyList<MicroTBox> tboxes,
        IGraph dataGraph,
        string dataSource,
        List<string> evidenceChunks,
        Stopwatch sw)
    {
        var schemaGraph = RdfGenerator.GenerateSchema(tboxes, BaseNamespace);
        var n3Rules = N3RuleGenerator.Generate(tboxes, BaseNamespace);
        var reasonedGraph = _reasoningEngine.Reason(dataGraph, schemaGraph, n3Rules);
        var inferredTriples = reasonedGraph.Triples
            .Where(triple => !dataGraph.ContainsTriple(triple) && !schemaGraph.ContainsTriple(triple))
            .Select(ToTripleDto)
            .DistinctBy(triple => (triple.S, triple.P, triple.O))
            .ToList();

        return new ReasonResponse
        {
            Status = "ok",
            DataSource = dataSource,
            EvidenceChunks = evidenceChunks,
            InferredTriples = inferredTriples,
            Results = [new Dictionary<string, object?> { ["inferred_count"] = inferredTriples.Count }],
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    private ReasonResponse ValidateShacl(
        IReadOnlyList<MicroTBox> tboxes,
        IGraph dataGraph,
        string dataSource,
        List<string> evidenceChunks,
        Stopwatch sw)
    {
        var shapesGraph = ShaclGenerator.Generate(tboxes, BaseNamespace);
        var response = _shaclValidator.Validate(dataGraph, shapesGraph);
        return response with
        {
            DataSource = dataSource,
            EvidenceChunks = evidenceChunks,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    private static bool IsShaclRequest(ReasonRequest request)
    {
        return string.Equals(request.Query.Profile, "shacl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Query.Type, "shacl", StringComparison.OrdinalIgnoreCase);
    }

    private static TripleDto ToTripleDto(Triple triple)
    {
        return new TripleDto
        {
            S = FromNode(triple.Subject),
            P = FromPredicate(triple.Predicate),
            O = FromNode(triple.Object),
        };
    }

    private static string FromPredicate(INode node)
    {
        var value = FromNode(node);
        return value == "http://www.w3.org/1999/02/22-rdf-syntax-ns#type" ? "rdf:type" : value;
    }

    private static string FromNode(INode node)
    {
        return node switch
        {
            IUriNode uriNode when uriNode.Uri.AbsoluteUri.StartsWith(BaseNamespace, StringComparison.Ordinal) =>
                Uri.UnescapeDataString(uriNode.Uri.AbsoluteUri[BaseNamespace.Length..]),
            IUriNode uriNode => uriNode.Uri.AbsoluteUri,
            ILiteralNode literalNode => literalNode.Value,
            _ => node.ToString(),
        };
    }
}
