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

    [Fact]
    public async Task Reason_PassesTenantAndKnowledgeBaseIdsToChunkOntologyFetch()
    {
        var repo = new FakeOntologyRepo(
            [
                (1UL, "kb-allowed", new MicroTBox
                {
                    Properties =
                    [
                        new PropertyDecl
                        {
                            Id = "ancestorOf",
                            Label = "ancestor of",
                            Domain = "Person",
                            Range = "Person",
                            Characteristics = ["transitive"],
                            Evidence = "chunk-1",
                        },
                    ],
                }, [], "chunk-1"),
                (2UL, "kb-denied", new MicroTBox
                {
                    Properties =
                    [
                        new PropertyDecl
                        {
                            Id = "ancestorOf",
                            Label = "ancestor of",
                            Domain = "Person",
                            Range = "Person",
                            Characteristics = ["transitive"],
                            Evidence = "chunk-2",
                        },
                    ],
                }, [], "chunk-2"),
            ],
            new Dictionary<string, string>());
        var assembler = new SliceAssembler(repo);
        var request = new ReasonRequest
        {
            TenantId = 1,
            KnowledgeBaseIds = ["kb-allowed"],
            ChunkIds = ["chunk-1", "chunk-2"],
            Query = new ReasonQuery { Type = "entailment", Body = "ASK" },
        };

        var response = await assembler.Reason(request);

        Assert.Equal("ok", response.Status);
        Assert.Equal(1UL, repo.LastFetchTenantId);
        Assert.Equal(["kb-allowed"], repo.LastFetchKnowledgeBaseIds);
        Assert.Equal(["chunk-1", "chunk-2"], repo.LastFetchChunkIds);
        Assert.Equal(["chunk-1"], response.EvidenceChunks);
    }

    [Fact]
    public async Task Reason_WithFetchedFactsAndTransitiveProperty_ReturnsInferredTriples()
    {
        var repo = new FakeOntologyRepo(
            [
                (1UL, "kb-1", new MicroTBox
                {
                    Properties =
                    [
                        new PropertyDecl
                        {
                            Id = "parentOf",
                            Label = "parent of",
                            Domain = "Person",
                            Range = "Person",
                            Characteristics = ["transitive"],
                            Evidence = "chunk-1",
                        },
                    ],
                },
                [
                    new TripleDto { S = "alice", P = "parentOf", O = "bob" },
                    new TripleDto { S = "bob", P = "parentOf", O = "carol" },
                ],
                "chunk-1"),
            ],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["parentOf"] = "ancestorOf",
            });
        var assembler = new SliceAssembler(repo);
        var request = new ReasonRequest
        {
            TenantId = 1,
            KnowledgeBaseIds = ["kb-1", "kb-2"],
            ChunkIds = ["chunk-1"],
            Query = new ReasonQuery { Type = "entailment", Body = "ASK" },
        };

        var response = await assembler.Reason(request);

        Assert.Equal("ok", response.Status);
        Assert.Equal("fetched", response.DataSource);
        Assert.Equal(["chunk-1"], response.EvidenceChunks);
        Assert.Equal([(1UL, "kb-1"), (1UL, "kb-2")], repo.CanonicalMapCalls);
        Assert.Contains(response.InferredTriples, t => t.S == "alice" && t.P == "ancestorOf" && t.O == "carol");
        Assert.DoesNotContain(response.InferredTriples, t => t.P == "parentOf");
        Assert.Contains(response.Results, result => (int)result["inferred_count"]! == response.InferredTriples.Count);
    }

    [Fact]
    public async Task Reason_WithProvidedFacts_UsesProvidedDataAndCanonicalMapForInference()
    {
        var repo = new FakeOntologyRepo(
            [
                (new MicroTBox
                {
                    Properties =
                    [
                        new PropertyDecl
                        {
                            Id = "parentOf",
                            Label = "parent of",
                            Domain = "Person",
                            Range = "Person",
                            Characteristics = ["transitive"],
                            Evidence = "chunk-1",
                        },
                    ],
                }, [], "chunk-1"),
            ],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["parentOf"] = "ancestorOf",
            });
        var assembler = new SliceAssembler(repo);
        var request = new ReasonRequest
        {
            TenantId = 1,
            KnowledgeBaseIds = ["kb-1"],
            ChunkIds = ["chunk-1"],
            InstanceFacts =
            [
                new TripleDto { S = "alice", P = "parentOf", O = "bob" },
                new TripleDto { S = "bob", P = "parentOf", O = "carol" },
            ],
            Query = new ReasonQuery { Type = "entailment", Body = "ASK" },
        };

        var response = await assembler.Reason(request);

        Assert.Equal("ok", response.Status);
        Assert.Equal("provided", response.DataSource);
        Assert.Contains(response.InferredTriples, t => t.S == "alice" && t.P == "ancestorOf" && t.O == "carol");
    }

    [Fact]
    public async Task Reason_WithShaclProfile_ValidatesProvidedFactsAndPropagatesViolations()
    {
        var repo = new FakeOntologyRepo(
            [
                (new MicroTBox
                {
                    Classes =
                    [
                        new ClassDecl { Id = "Person", Label = "Person", Evidence = "chunk-1" },
                    ],
                    Shapes =
                    [
                        new ShapeDecl
                        {
                            TargetClass = "Person",
                            Constraints =
                            [
                                new ShapeConstraint { Property = "email", MaxCount = 1 },
                            ],
                            Evidence = "chunk-1",
                        },
                    ],
                }, [], "chunk-1"),
            ],
            new Dictionary<string, string>());
        var assembler = new SliceAssembler(repo);
        var request = new ReasonRequest
        {
            TenantId = 1,
            KnowledgeBaseIds = ["kb-1"],
            ChunkIds = ["chunk-1"],
            InstanceFacts =
            [
                new TripleDto { S = "alice", P = "rdf:type", O = "Person" },
                new TripleDto { S = "alice", P = "email", O = "email1" },
                new TripleDto { S = "alice", P = "email", O = "email2" },
            ],
            Query = new ReasonQuery { Type = "query", Profile = "shacl", Body = "validate" },
        };

        var response = await assembler.Reason(request);

        Assert.Equal("violations", response.Status);
        Assert.Equal("provided", response.DataSource);
        Assert.Equal(["chunk-1"], response.EvidenceChunks);
        Assert.NotEmpty(response.Results);
    }

    [Fact]
    public async Task Reason_WithLocalClassAliasOnProvidedFacts_AlignsRdfTypeObjectForShaclTarget()
    {
        var repo = new FakeOntologyRepo(
            [
                (new MicroTBox
                {
                    Classes =
                    [
                        new ClassDecl { Id = "Person", Label = "Person", Evidence = "chunk-1" },
                    ],
                    Aliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Person"] = ["Human"],
                    },
                    Shapes =
                    [
                        new ShapeDecl
                        {
                            TargetClass = "Person",
                            Constraints =
                            [
                                new ShapeConstraint { Property = "email", MinCount = 1 },
                            ],
                            Evidence = "chunk-1",
                        },
                    ],
                }, [], "chunk-1"),
            ],
            new Dictionary<string, string>());
        var assembler = new SliceAssembler(repo);
        var request = new ReasonRequest
        {
            TenantId = 1,
            KnowledgeBaseIds = ["kb-1"],
            ChunkIds = ["chunk-1"],
            InstanceFacts =
            [
                new TripleDto { S = "alice", P = "rdf:type", O = "Human" },
            ],
            Query = new ReasonQuery { Type = "query", Profile = "shacl", Body = "validate" },
        };

        var response = await assembler.Reason(request);

        Assert.Equal("violations", response.Status);
        Assert.Contains(response.Results, result => result.TryGetValue("focusNode", out var focusNode) && focusNode?.ToString() == "http://weknora.local/ontology/alice");
    }

    [Fact]
    public async Task Reason_WithLocalPropertyAliasOnFetchedFacts_AlignsPredicateForEntailment()
    {
        var repo = new FakeOntologyRepo(
            [
                (new MicroTBox
                {
                    Properties =
                    [
                        new PropertyDecl
                        {
                            Id = "ancestorOf",
                            Label = "ancestor of",
                            Domain = "Person",
                            Range = "Person",
                            Characteristics = ["transitive"],
                            Evidence = "chunk-1",
                        },
                    ],
                    Aliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ancestorOf"] = ["parentOf"],
                    },
                },
                [
                    new TripleDto { S = "alice", P = "parentOf", O = "bob" },
                    new TripleDto { S = "bob", P = "parentOf", O = "carol" },
                ],
                "chunk-1"),
            ],
            new Dictionary<string, string>());
        var assembler = new SliceAssembler(repo);
        var request = new ReasonRequest
        {
            TenantId = 1,
            KnowledgeBaseIds = ["kb-1"],
            ChunkIds = ["chunk-1"],
            Query = new ReasonQuery { Type = "entailment", Body = "ASK" },
        };

        var response = await assembler.Reason(request);

        Assert.Contains(response.InferredTriples, t => t.S == "alice" && t.P == "ancestorOf" && t.O == "carol");
    }

    [Fact]
    public async Task Reason_WithConflictingKbCanonicalMaps_AlignsFetchedFactsPerOwningKnowledgeBase()
    {
        var repo = new FakeOntologyRepo(
            [
                (1UL, "kb-1", new MicroTBox
                {
                    Properties =
                    [
                        new PropertyDecl
                        {
                            Id = "marriedTo",
                            Label = "married to",
                            Domain = "Person",
                            Range = "Person",
                            Characteristics = ["transitive"],
                            Evidence = "chunk-1",
                        },
                    ],
                },
                [
                    new TripleDto { S = "a1", P = "spouseOf", O = "b1" },
                    new TripleDto { S = "b1", P = "spouseOf", O = "c1" },
                ],
                "chunk-1"),
                (1UL, "kb-2", new MicroTBox
                {
                    Properties =
                    [
                        new PropertyDecl
                        {
                            Id = "partnerOf",
                            Label = "partner of",
                            Domain = "Person",
                            Range = "Person",
                            Characteristics = ["transitive"],
                            Evidence = "chunk-2",
                        },
                    ],
                },
                [
                    new TripleDto { S = "a2", P = "spouseOf", O = "b2" },
                    new TripleDto { S = "b2", P = "spouseOf", O = "c2" },
                ],
                "chunk-2"),
            ],
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["kb-1"] = new(StringComparer.OrdinalIgnoreCase) { ["spouseOf"] = "marriedTo" },
                ["kb-2"] = new(StringComparer.OrdinalIgnoreCase) { ["spouseOf"] = "partnerOf" },
            });
        var assembler = new SliceAssembler(repo);
        var request = new ReasonRequest
        {
            TenantId = 1,
            KnowledgeBaseIds = ["kb-1", "kb-2"],
            ChunkIds = ["chunk-1", "chunk-2"],
            Query = new ReasonQuery { Type = "entailment", Body = "ASK" },
        };

        var response = await assembler.Reason(request);

        Assert.Contains(response.InferredTriples, t => t.S == "a1" && t.P == "marriedTo" && t.O == "c1");
        Assert.Contains(response.InferredTriples, t => t.S == "a2" && t.P == "partnerOf" && t.O == "c2");
        Assert.DoesNotContain(response.InferredTriples, t => t.S == "a1" && t.P == "partnerOf");
        Assert.DoesNotContain(response.InferredTriples, t => t.S == "a2" && t.P == "marriedTo");
    }

    [Fact]
    public async Task Reason_WithProvidedFactsAndConflictingKbCanonicalMaps_LeavesConflictingAliasesUnchangedAndWarns()
    {
        var repo = new FakeOntologyRepo(
            [
                (1UL, "kb-1", new MicroTBox
                {
                    Properties =
                    [
                        new PropertyDecl
                        {
                            Id = "marriedTo",
                            Label = "married to",
                            Domain = "Person",
                            Range = "Person",
                            Characteristics = ["transitive"],
                            Evidence = "chunk-1",
                        },
                    ],
                }, [], "chunk-1"),
                (1UL, "kb-2", new MicroTBox
                {
                    Properties =
                    [
                        new PropertyDecl
                        {
                            Id = "partnerOf",
                            Label = "partner of",
                            Domain = "Person",
                            Range = "Person",
                            Characteristics = ["transitive"],
                            Evidence = "chunk-2",
                        },
                    ],
                }, [], "chunk-2"),
            ],
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["kb-1"] = new(StringComparer.OrdinalIgnoreCase) { ["spouseOf"] = "marriedTo" },
                ["kb-2"] = new(StringComparer.OrdinalIgnoreCase) { ["spouseOf"] = "partnerOf" },
            });
        var assembler = new SliceAssembler(repo);
        var request = new ReasonRequest
        {
            TenantId = 1,
            KnowledgeBaseIds = ["kb-1", "kb-2"],
            ChunkIds = ["chunk-1", "chunk-2"],
            InstanceFacts =
            [
                new TripleDto { S = "alice", P = "spouseOf", O = "bob" },
                new TripleDto { S = "bob", P = "spouseOf", O = "carol" },
            ],
            Query = new ReasonQuery { Type = "entailment", Body = "ASK" },
        };

        var response = await assembler.Reason(request);

        Assert.DoesNotContain(response.InferredTriples, t => t.P is "marriedTo" or "partnerOf");
        Assert.Contains("Canonical alias conflict for spouseOf; leaving provided facts unchanged for that alias", response.Warnings);
    }

    private sealed class FakeOntologyRepo : IOntologyRepo
    {
        private readonly List<(ulong TenantId, string KnowledgeBaseId, MicroTBox TBox, List<TripleDto> Facts, string ChunkId)> _chunkOntologies;
        private readonly Dictionary<string, Dictionary<string, string>> _canonicalMapsByKnowledgeBaseId;

        public FakeOntologyRepo(
            List<(MicroTBox TBox, List<TripleDto> Facts, string ChunkId)> chunkOntologies,
            Dictionary<string, string> canonicalMap)
            : this(chunkOntologies.Select(item => (1UL, "kb-1", item.TBox, item.Facts, item.ChunkId)).ToList(), canonicalMap)
        {
        }

        public FakeOntologyRepo(
            List<(ulong TenantId, string KnowledgeBaseId, MicroTBox TBox, List<TripleDto> Facts, string ChunkId)> chunkOntologies,
            Dictionary<string, string> canonicalMap)
            : this(
                chunkOntologies,
                chunkOntologies
                    .Select(item => item.KnowledgeBaseId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(kbId => kbId, _ => canonicalMap, StringComparer.OrdinalIgnoreCase))
        {
        }

        public FakeOntologyRepo(
            List<(ulong TenantId, string KnowledgeBaseId, MicroTBox TBox, List<TripleDto> Facts, string ChunkId)> chunkOntologies,
            Dictionary<string, Dictionary<string, string>> canonicalMapsByKnowledgeBaseId)
        {
            _chunkOntologies = chunkOntologies;
            _canonicalMapsByKnowledgeBaseId = canonicalMapsByKnowledgeBaseId;
        }

        public ulong? LastFetchTenantId { get; private set; }

        public IReadOnlyList<string>? LastFetchKnowledgeBaseIds { get; private set; }

        public IReadOnlyList<string>? LastFetchChunkIds { get; private set; }

        public List<(ulong TenantId, string KnowledgeBaseId)> CanonicalMapCalls { get; } = [];

        public Task<List<OntologyChunkData>> GetChunkOntologies(ulong tenantId, IReadOnlyList<string> knowledgeBaseIds, IReadOnlyList<string> chunkIds)
        {
            LastFetchTenantId = tenantId;
            LastFetchKnowledgeBaseIds = knowledgeBaseIds.ToList();
            LastFetchChunkIds = chunkIds.ToList();

            return Task.FromResult(_chunkOntologies
                .Where(item => item.TenantId == tenantId
                    && knowledgeBaseIds.Contains(item.KnowledgeBaseId)
                    && chunkIds.Contains(item.ChunkId))
                .Select(item => new OntologyChunkData(item.TBox, item.Facts, item.ChunkId, item.KnowledgeBaseId))
                .ToList());
        }

        public Task<Dictionary<string, string>> GetCanonicalMap(ulong tenantId, string knowledgeBaseId)
        {
            CanonicalMapCalls.Add((tenantId, knowledgeBaseId));
            return Task.FromResult(_canonicalMapsByKnowledgeBaseId.TryGetValue(knowledgeBaseId, out var canonicalMap)
                ? canonicalMap
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
    }
}
