# Ontology Slice Core MVP: Implementation Spec

**Status:** Approved for implementation
**Scope:** Core MVP (sections 1-11 of the original RFC)
**Deferred:** Expert review (section 12), hybrid reasoning (section 13), TBox governance (section 14), federated SPARQL (section 15) — separate future specs

---

## 1. Overview

### 1.1 What This Adds

A 4th retrieval path — **Ontology Slice** — that gives WeKnora formal reasoning capabilities alongside the existing Vector RAG, Graph RAG (Neo4j), and LLM Wiki paths.

| Query Type | Routed To |
|-----------|-----------|
| Fuzzy semantic / similarity | Vector RAG (existing) |
| Entity-relationship exploration | Graph RAG / Neo4j (existing) |
| Concept browsing / background | LLM Wiki (existing) |
| Transitive relations, type hierarchy, constraint validation, SPARQL | **Ontology Slice (this spec)** |

### 1.2 Core Idea: Slice-Based Ontology

Traditional: `global schema first -> instance fill -> global reasoning`

This design: `each chunk gets a micro-TBox -> retrieve relevant chunks -> assemble query-time ontology -> local reasoning`

Each chunk is both a retrieval unit and a reasoning unit. The ontology is a derived property of the chunk, not global infrastructure.

### 1.3 Key Architectural Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Sidecar technology | .NET 10 + dotNetRDF 3.x | Mature RDFS/N3/SHACL support, no JVM dependency, ~100MB image |
| Instance facts source | PostgreSQL self-contained | No Neo4j dependency for ontology path |
| Sidecar data access | Direct PG connection | Lower latency than Go API proxy; acceptable schema coupling |
| Reasoning timing | Query-time (not ingestion) | Controllable scope; flexible slice combinations |
| Intermediate format | JSON (not OWL/Turtle) | LLM error rate much lower; per-axiom post-validation |
| Storage | JSONB in chunks table | Reuse existing PG; zero extra infrastructure |

---

## 2. Ingestion Pipeline

### 2.1 Insertion Point

New step inserted into `graphBuilder.BuildGraph` (at `internal/application/service/graph.go`), after `buildChunkGraph` (step 7) and **parallel** with the Neo4j write.

### 2.2 Full Pipeline

```
1. Document Upload                         (existing: KnowledgeService)
2. Parse + Chunk Split                     (existing: docreader + ChunkService)
3. ChunkExtractTask enqueued               (existing: asynq queue)
4. Entity Extraction                       (existing: extractEntities, LLM per chunk)
5. Relationship Extraction                 (existing: extractRelationships, LLM batched)
6. PMI + Strength Weighting                (existing: calculateWeights)
7. Chunk Relationship Graph                (existing: buildChunkGraph)
   |
   +-- 8. micro-TBox Extraction  [NEW]     (LLM per chunk, concurrent max 4)
   |      |
   |      +-- 10. PG Write: ontology_json + instance_facts_json  [NEW]
   |      +-- 11. Canonical Map Upsert     [NEW]
   |
   +-- 9. Neo4j Write                      (existing, optional, parallel with 8)
```

### 2.3 micro-TBox Extraction Logic

```go
func (b *graphBuilder) extractMicroTBoxes(ctx context.Context, chunks []*types.Chunk) {
    g, gctx := errgroup.WithContext(ctx)
    g.SetLimit(MaxConcurrentEntityExtractions) // 4

    for _, chunk := range chunks {
        chunk := chunk
        g.Go(func() error {
            chunkEntities := b.entitiesForChunk(chunk.ID)
            chunkRels := b.relationshipsForChunk(chunk.ID)

            // Sparse chunks: not enough data for schema extraction
            if len(chunkEntities) < 2 {
                return nil
            }

            tbox, err := b.callMicroTBoxLLM(gctx, chunk, chunkEntities, chunkRels)
            if err != nil {
                logger.GetLogger(gctx).WithError(err).
                    Warnf("micro-tbox extraction failed for chunk %s", chunk.ID)
                return nil // single chunk failure doesn't block pipeline
            }

            tbox = b.validateEvidence(tbox, chunk.Content)

            if tbox.Confidence < 0.3 {
                return nil
            }

            chunk.OntologyJSON = tbox
            chunk.InstanceFactsJSON = b.buildInstanceFacts(chunkEntities, chunkRels)
            return nil
        })
    }
    g.Wait()
}
```

Key behaviors:
- **Concurrent**: reuses `MaxConcurrentEntityExtractions = 4`
- **Graceful degradation**: single chunk failure is logged and skipped
- **Evidence validation**: each axiom's `evidence` must be a substring of `chunk.Content`; invalid evidence causes the axiom to be dropped
- **Confidence threshold**: `confidence < 0.3` means the entire micro-TBox is discarded
- **Sparse skip**: chunks with `< 2` entities don't get a micro-TBox LLM call

### 2.4 Instance Facts Construction

```go
func (b *graphBuilder) buildInstanceFacts(
    entities []types.Entity,
    rels []types.Relationship,
) []types.Triple {
    var facts []types.Triple

    for _, e := range entities {
        facts = append(facts, types.Triple{
            Subject:   e.Title,
            Predicate: "rdf:type",
            Object:    e.Type,
        })
    }

    for _, r := range rels {
        facts = append(facts, types.Triple{
            Subject:   r.Source,
            Predicate: r.Description,
            Object:    r.Target,
        })
    }

    return facts
}
```

### 2.5 Canonical Map Auto-Population

After micro-TBox extraction, aliases are upserted into `ontology_canonical_map`:

```go
func (b *graphBuilder) upsertCanonicalMap(
    ctx context.Context,
    tenantID int64,
    kbID string,
    tbox *types.MicroTBox,
    chunkID string,
) error {
    for canonicalID, aliases := range tbox.Aliases {
        // Upsert: merge aliases array, append chunkID to source_chunks
        err := b.canonicalMapRepo.Upsert(ctx, tenantID, kbID, canonicalID, aliases, chunkID)
        if err != nil {
            return err
        }
    }
    return nil
}
```

---

## 3. Data Model

### 3.1 New Go Types

New file: `internal/types/micro_tbox.go`

```go
package types

type MicroTBox struct {
    Classes    []ClassDecl         `json:"classes"`
    Properties []PropertyDecl      `json:"properties"`
    Shapes     []ShapeDecl         `json:"shapes"`
    Aliases    map[string][]string `json:"aliases"`
    Axioms     []FreeAxiom         `json:"axioms"`
    Confidence float64             `json:"confidence"`
}

type ClassDecl struct {
    ID           string   `json:"id"`
    Label        string   `json:"label"`
    SubClassOf   *string  `json:"subClassOf"`
    DisjointWith []string `json:"disjointWith"`
    Evidence     string   `json:"evidence"`
}

type PropertyDecl struct {
    ID              string   `json:"id"`
    Label           string   `json:"label"`
    Domain          string   `json:"domain"`
    Range           string   `json:"range"`
    Characteristics []string `json:"characteristics"`
    InverseOf       *string  `json:"inverseOf"`
    Evidence        string   `json:"evidence"`
}

type ShapeDecl struct {
    TargetClass string            `json:"target_class"`
    Constraints []ShapeConstraint `json:"constraints"`
    Evidence    string            `json:"evidence"`
}

type ShapeConstraint struct {
    Property string   `json:"property"`
    MinCount *int     `json:"min_count"`
    MaxCount *int     `json:"max_count"`
    Datatype *string  `json:"datatype"`
    InValues []string `json:"in_values"`
}

type FreeAxiom struct {
    Statement string `json:"statement"`
    Evidence  string `json:"evidence"`
}

type Triple struct {
    Subject   string `json:"s"`
    Predicate string `json:"p"`
    Object    string `json:"o"`
}
```

### 3.2 Chunk Struct Extension

In `internal/types/chunk.go`:

```go
type Chunk struct {
    // ... existing fields ...

    OntologyJSON        *MicroTBox `json:"ontology_json,omitempty"`
    OntologyExtractedAt *time.Time `json:"ontology_extracted_at,omitempty"`
    OntologyConfidence  *float64   `json:"ontology_confidence,omitempty"`
    InstanceFactsJSON   []Triple   `json:"instance_facts_json,omitempty"`
}
```

### 3.3 Database Migrations

**Migration 000052: chunk_ontology.up.sql**

```sql
ALTER TABLE chunks
  ADD COLUMN ontology_json          JSONB       DEFAULT NULL,
  ADD COLUMN ontology_extracted_at  TIMESTAMPTZ DEFAULT NULL,
  ADD COLUMN ontology_confidence    REAL        DEFAULT NULL,
  ADD COLUMN instance_facts_json    JSONB       DEFAULT NULL;

CREATE INDEX idx_chunks_ontology_extracted
  ON chunks (tenant_id, knowledge_base_id)
  WHERE ontology_json IS NOT NULL;

CREATE INDEX idx_chunks_ontology_class_ids
  ON chunks USING GIN ((jsonb_path_query_array(ontology_json, '$.classes[*].id')));
```

**Migration 000053: ontology_canonical_map.up.sql**

```sql
CREATE TABLE ontology_canonical_map (
    id                BIGSERIAL PRIMARY KEY,
    tenant_id         BIGINT NOT NULL,
    knowledge_base_id TEXT NOT NULL,
    kind              TEXT NOT NULL CHECK (kind IN ('class', 'property')),
    canonical_id      TEXT NOT NULL,
    aliases           TEXT[] NOT NULL DEFAULT '{}',
    source_chunks     TEXT[] NOT NULL DEFAULT '{}',
    confidence        REAL NOT NULL DEFAULT 0.5,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, knowledge_base_id, kind, canonical_id)
);

CREATE INDEX idx_canonical_map_aliases
  ON ontology_canonical_map USING GIN (aliases);
```

Execution order: 000052 first, then 000053.

---

## 4. Prompt Design

### 4.1 Template

Added to `config/prompt_templates/graph_extraction.yaml` as `default_extract_micro_tbox`.

**Input:** chunk text + already-extracted entities + relationships
**Output:** single JSON object with `classes`, `properties`, `shapes`, `aliases`, `axioms`, `confidence`

Hard constraints:
1. Every class `id` must match an entity type from the input entity list (or a generalization explicitly named in the text)
2. Every property `id` must correspond to a relationship description from the input
3. Every item must include an `evidence` field — verbatim substring from the source text
4. `id` fields in ASCII (PascalCase for classes, camelCase for properties); `label` fields in `{{language}}`
5. Prefer empty over wrong — returning all empty arrays is the correct answer when no schema-level content exists

The template includes:
- Field specifications for all 6 top-level fields
- Guidance for each property characteristic (transitive, symmetric, etc.)
- Two complete examples: rich extraction (Romeo & Juliet) and sparse extraction (meeting notes → all empty)
- 6 explicit anti-patterns

Full template text: see original RFC (`docs/可推理Ontology切片设计.md`, section 6.1, template id `default_extract_micro_tbox`). The template is unchanged from the RFC and should be copied verbatim into `graph_extraction.yaml`.

### 4.2 Config Integration

```go
// internal/config/config.go
type ConversationConfig struct {
    // ... existing ...
    ExtractMicroTBoxPrompt string `mapstructure:"extract_micro_tbox_prompt"`
}
```

```yaml
# config/config.yaml
conversation:
  extract_micro_tbox_prompt: "default_extract_micro_tbox"
```

### 4.3 Post-Extraction Validation

1. **Parse**: must be valid JSON matching `MicroTBox` struct
2. **Evidence check**: each `evidence` field must be a substring of `chunk.Content`; axioms with invalid evidence are dropped
3. **Confidence filter**: `confidence < 0.3` → entire micro-TBox discarded
4. **Sparse skip**: chunks with `< 2` entities skip the LLM call entirely

---

## 5. Reasoning Sidecar

### 5.1 Technology

- .NET 10 (C#) + ASP.NET Core Minimal API
- dotNetRDF 3.x (dotNetRdf, dotNetRdf.Inferencing, dotNetRdf.Shacl)
- Npgsql 8.x + Dapper 2.x for PG access
- Docker image: `mcr.microsoft.com/dotnet/aspnet:10.0-alpine` (~100MB)

### 5.2 Core Insight

OWL "reasoning" is three different operations. Each maps to a mature dotNetRDF component:

| Operation | Nature | dotNetRDF Component |
|-----------|--------|-------------------|
| Entailment (derive new triples) | Monotonic forward | `SimpleN3RulesReasoner` + auto-generated N3 rules |
| Constraint validation | Negation/cardinality | `Shacl.ShapesGraph` + auto-generated SHACL shapes |
| Type hierarchy transitive closure | RDFS subset | `StaticRdfsReasoner` (built-in) |

### 5.3 N3 Rule Mapping

| micro-TBox Property Characteristic | Generated N3 Rule |
|-----------------------------------|-------------------|
| transitive | `{ ?a :P ?b . ?b :P ?c } => { ?a :P ?c } .` |
| symmetric | `{ ?a :P ?b } => { ?b :P ?a } .` |
| inverseOf Q | `{ ?a :P ?b } => { ?b :Q ?a } .` (+ reverse) |
| domain D | `{ ?x :P ?y } => { ?x a :D } .` |
| range R | `{ ?x :P ?y } => { ?y a :R } .` |
| equivalentClass A↔B | `{ ?x a :A } => { ?x a :B } .` (+ reverse) |

subClassOf and subPropertyOf are handled by `StaticRdfsReasoner` (not N3 rules).

### 5.4 SHACL Constraint Mapping

| OWL Characteristic | SHACL Expression |
|-------------------|-----------------|
| functional | `sh:maxCount 1` on property shape |
| inverseFunctional | SPARQL-based shape |
| asymmetric | SPARQL-based shape (self-check reverse) |
| irreflexive | SPARQL-based shape |
| disjointWith | `sh:not [ sh:class :Other ]` |
| min_count / max_count | `sh:minCount` / `sh:maxCount` |
| datatype | `sh:datatype` |
| in_values | `sh:in` |

### 5.5 Reasoning Profiles

| Profile | Implementation | Latency |
|---------|---------------|---------|
| `rdfs` | `StaticRdfsReasoner` only | < 50ms |
| `n3-extended` (default) | RDFS + `SimpleN3RulesReasoner` + auto-generated rules | < 200ms |
| `shacl` | `Shacl.ShapesGraph` validation | < 100ms |

Default: `n3-extended`. SHACL runs only when `query.type=consistency|shacl` or `query.profile=shacl`.

### 5.6 Fixpoint Reasoning Engine

```csharp
public IGraph Reason(IGraph dataGraph, IGraph schemaGraph,
                     string n3Rules, int maxIterations = 10)
{
    var working = new Graph();
    working.Merge(dataGraph);
    working.Merge(schemaGraph);

    var rdfsReasoner = new StaticRdfsReasoner();
    rdfsReasoner.Initialise(schemaGraph);

    var rulesGraph = new Graph();
    rulesGraph.LoadFromString(n3Rules, new Notation3Parser());
    var n3Reasoner = new SimpleN3RulesReasoner();
    n3Reasoner.Initialise(rulesGraph);

    int prevHash, iter = 0;
    do
    {
        prevHash = ComputeTriplesHash(working);
        rdfsReasoner.Apply(working);
        n3Reasoner.Apply(working);
        if (++iter >= maxIterations) break;
    } while (ComputeTriplesHash(working) != prevHash);

    return working;
}
```

Convergence detection uses triple-set hash comparison (not `Triples.Count`), because in rare cases N3 application can simultaneously add and remove triples.

### 5.7 Safety Guards

- Fixpoint cap: `maxIterations = 10`
- Growth rate: single-round triple growth > 100x → abort
- Total cap: hard limit 100K triples
- SPARQL timeout: ≤ 2000ms, max 1000 result rows
- Rule whitelist: only auto-generated rules from micro-TBox, no runtime injection
- Chunk limit: ≤ 50 chunk_ids per query
- Forbidden: `SERVICE` / `LOAD` / external endpoint access in SPARQL

### 5.8 Project Structure

```
ontology-reasoner-net/
├── WeKnora.OntologyReasoner.Api/
│   ├── Program.cs
│   └── Endpoints/
│       ├── ReasonEndpoint.cs
│       └── ValidateEndpoint.cs
├── WeKnora.OntologyReasoner.Core/
│   ├── Models/MicroTBox.cs
│   ├── Aligner/CanonicalAligner.cs
│   ├── Assembly/SliceAssembler.cs
│   ├── Generators/
│   │   ├── RdfGenerator.cs
│   │   ├── N3RuleGenerator.cs
│   │   └── ShaclGenerator.cs
│   ├── Engine/
│   │   ├── ReasoningEngine.cs
│   │   └── ShaclValidator.cs
│   └── Storage/PostgresOntologyRepo.cs
├── WeKnora.OntologyReasoner.Tests/
├── Dockerfile
└── README.md
```

NuGet dependencies:
- `dotNetRdf` 3.3.*
- `dotNetRdf.Inferencing` 3.3.*
- `dotNetRdf.Shacl` 3.3.*
- `Npgsql` 8.*
- `Dapper` 2.*

### 5.9 Slice Assembly (query path)

The `SliceAssembler` orchestrates the query-time merge:

1. Batch-pull `ontology_json` + `instance_facts_json` from PG by chunk_ids
2. Load `canonical_map` for the given tenant + KB
3. `CanonicalAligner.Align`: rewrite all class/property IDs using alias → canonical_id (case-insensitive, immutable copies)
4. Three-way generation: `RdfGenerator` → schema graph, `N3RuleGenerator` → rules string, `ShaclGenerator` → shapes graph
5. Build data graph from instance facts
6. Conflict detection: SPARQL check for `subClassOf + disjointWith` on same pair → log warning, don't block

Provenance: `Dictionary<Triple, List<string>>` maps each schema triple to its source chunk_ids (a triple may originate from multiple chunks), used after SPARQL query to trace evidence.

### 5.10 Engineering Notes

1. N3 parser: use simplest syntax only (`{ pattern } => { pattern } .`), avoid advanced N3 features
2. String-based rules are simpler to debug than triple-based API construction
3. For > 10K triples: switch from `IGraph.ExecuteQuery` to `InMemoryDataset` + `LeviathanQueryProcessor`
4. AOT: start with ReadyToRun; defer native AOT (dotNetRDF has reflection dependencies)
5. PG connection pool: `Maximum Pool Size=20` for sidecar workload

---

## 6. Agent Tool Integration

### 6.1 New Tool: ontology_reason

New file: `internal/agent/tools/ontology_reason.go`

```go
const ToolOntologyReason = "ontology_reason"

type OntologyReasonInput struct {
    KnowledgeBaseIDs []string `json:"knowledge_base_ids"`
    Query            string   `json:"query"`
    ReasonerProfile  string   `json:"reasoner_profile,omitempty"`
    IncludeEvidence  bool     `json:"include_evidence,omitempty"`
}
```

Tool description guides the Agent on when to use it:
- Transitive queries ("all descendants of X") → ontology_reason
- Subsumption ("is X a kind of Y?") → ontology_reason
- Constraint validation ("does X satisfy the schema?") → ontology_reason
- Negation/disjointness ("can X be both A and B?") → ontology_reason

Registration: add `ToolOntologyReason` constant to `internal/agent/tools/definitions.go`. Register conditionally when `ONTOLOGY_ENABLE=true`.

### 6.2 Tool Workflow

1. Hybrid search (existing) to retrieve candidate chunk_ids
2. HTTP POST to sidecar `/reason` endpoint
3. Format structured result with provenance back to Agent context

### 6.3 Agent Prompt Addition

```
When the user asks structural, constraint-checking, or transitive-relation questions,
prefer ontology_reason. When in doubt, use knowledge_search for retrieval first, then
ontology_reason for reasoning. Avoid using ontology_reason for fuzzy text search.
```

---

## 7. Sidecar API Contract

### 7.1 Endpoint

```
POST /reason
Content-Type: application/json
```

### 7.2 Request

```json
{
  "tenant_id": 123,
  "knowledge_base_ids": ["kb_abc"],
  "chunk_ids": ["chunk_42", "chunk_88", "chunk_153"],
  "instance_facts": [
    {"s": "RomeoMontague", "p": "isMemberOf", "o": "Montague"}
  ],
  "query": {
    "type": "sparql | consistency | entailment | shacl",
    "body": "...",
    "profile": "rdfs | n3-extended | shacl"
  }
}
```

- `instance_facts`: optional. When omitted, sidecar auto-fetches from PG by chunk_ids.
- `query.profile`: default `n3-extended`. Controls entailment reasoning path.
- `query.type`: controls what result is returned.

### 7.3 Response

```json
{
  "status": "ok | violations | error",
  "results": [],
  "inferred_triples": [],
  "data_source": "provided | fetched",
  "evidence_chunks": ["chunk_42"],
  "warnings": [],
  "elapsed_ms": 123
}
```

### 7.4 Execution Contracts

1. `query.type=consistency` **must** execute SHACL validation. If not executed, return `status=error`. Never default to `consistent=true`.
2. Default execution path: `n3-extended` entailment. SHACL only when `type=consistency|shacl` or `profile=shacl`.
3. Missing `instance_facts` → sidecar auto-fetches from PG. Response must include `data_source=fetched`.
4. SPARQL timeout: ≤ 2000ms. Result row limit: ≤ 1000.
5. Forbidden in SPARQL: `SERVICE`, `LOAD`, external endpoint access.
6. Only whitelisted IRI prefixes and aligned vocabulary allowed.

---

## 8. Cross-Slice Alignment

### 8.1 Problem

Same concept gets different IDs across chunks (e.g., `Engineer` vs `软件工程师` vs `SoftwareEngineer`). Without alignment, reasoning can't connect facts across chunks.

### 8.2 Two-Tier Strategy (Core MVP)

| Tier | When | How | Cost |
|------|------|-----|------|
| Tier 1 | LLM extraction | Prompt requires `aliases` field in micro-TBox output | Zero (embedded in existing call) |
| Tier 2 | Sidecar merge time | `CanonicalAligner` uses canonical_map + chunk aliases, case-insensitive | Low (string lookup) |

Tier 3 (offline embedding + LLM alignment job) is deferred to the Extensions spec.

### 8.3 CanonicalAligner Logic

Priority: `global canonical_map > chunk-local aliases > original ID`

- Case-insensitive matching (`StringComparer.OrdinalIgnoreCase`)
- Immutable rewrite: `record with` syntax produces new copies, original micro-TBox unmodified
- Unmatched IDs kept as-is — system accepts partial ambiguity

### 8.4 Canonical Map Auto-Population

During ingestion after micro-TBox extraction:
1. Extract all `aliases` entries from the micro-TBox
2. For each canonical_id → alias list, upsert into `ontology_canonical_map`
3. Append chunk_id to `source_chunks` array (deduplicated)
4. Merge new aliases into existing `aliases` array (union, not replace)
5. Confidence starts at 0.5 (LLM-derived); does not decrease on subsequent upserts

Merge semantics: if canonical_id already exists, aliases are unioned and source_chunks are appended. If two chunks claim different canonical_ids for the same alias, both mappings coexist — the `CanonicalAligner` uses first-match (global canonical_map wins over chunk-local). Conflicts are acceptable in Core MVP; expert review (future spec) resolves them.

In Core MVP without expert review, this is the only population mechanism for the canonical_map.

---

## 9. Deployment & Configuration

### 9.1 Environment Variables

```bash
ONTOLOGY_ENABLE=true
ONTOLOGY_REASONER_URL=http://ontology-reasoner:8090
ONTOLOGY_DEFAULT_PROFILE=n3-extended
ONTOLOGY_CONFIDENCE_THRESHOLD=0.3
ONTOLOGY_EXTRACT_MIN_ENTITIES=2
```

### 9.2 Config File

```yaml
# config/config.yaml
ontology:
  enabled: ${ONTOLOGY_ENABLE:false}
  reasoner_url: ${ONTOLOGY_REASONER_URL:http://ontology-reasoner:8090}
  default_profile: ${ONTOLOGY_DEFAULT_PROFILE:n3-extended}
  confidence_threshold: ${ONTOLOGY_CONFIDENCE_THRESHOLD:0.3}
  extract_min_entities: ${ONTOLOGY_EXTRACT_MIN_ENTITIES:2}
```

### 9.3 Docker Compose

```yaml
services:
  ontology-reasoner:
    build:
      context: ./ontology-reasoner-net
      dockerfile: Dockerfile
    image: weknora/ontology-reasoner:dotnet-10
    ports:
      - "8090:8090"
    environment:
      - ASPNETCORE_URLS=http://+:8090
      - ASPNETCORE_ENVIRONMENT=Production
      - DB_CONNECTION_STRING=Host=postgres;Database=weknora;Username=weknora;Password=${POSTGRES_PASSWORD}
      - REASONER_DEFAULT_PROFILE=n3-extended
      - REASONER_MAX_ITERATIONS=10
    depends_on:
      - postgres
    profiles:
      - ontology
```

Enabled via: `docker-compose --profile ontology up -d`

### 9.4 .env.example

```env
# ===== Ontology Reasoning (Optional, .NET sidecar) =====
ONTOLOGY_ENABLE=false
ONTOLOGY_REASONER_URL=http://ontology-reasoner:8090
ONTOLOGY_DEFAULT_PROFILE=n3-extended
ONTOLOGY_CONFIDENCE_THRESHOLD=0.3
ONTOLOGY_EXTRACT_MIN_ENTITIES=2
```

### 9.5 Feature Flag Behavior

When `ONTOLOGY_ENABLE=false`:
- micro-TBox extraction step skipped in `BuildGraph`
- `ontology_reason` tool not registered in Agent toolbox
- No sidecar container needed
- Zero impact on existing pipeline

### 9.6 Migrations

Execute in order:
1. `migrations/versioned/000052_chunk_ontology.up.sql`
2. `migrations/versioned/000053_ontology_canonical_map.up.sql`

---

## 10. Risks & Trade-offs

### 10.1 Design Trade-offs

| Trade-off | Give Up | Gain |
|-----------|---------|------|
| No global consistency | Cross-chunk reasoning may be incomplete | Incremental-friendly; no global rebuild on new docs |
| JSON intermediate (not OWL) | Extra post-processing step | Lower LLM error rate; per-axiom validation |
| JSONB in chunks table | Query-time deserialization | Reuse existing PG; zero extra infra |
| N3 + SHACL (not OWL DL) | No consistency proofs, complex class expressions, nominals | No JVM; predictable perf; 85-90% RAG coverage |
| Query-time reasoning | Per-query compute cost | Controllable scope; flexible slice combination |
| PG self-contained | Duplicate entity/rel storage | Ontology path fully independent of Neo4j |

### 10.2 Known Risks

| Risk | Impact | Mitigation |
|------|--------|-----------|
| LLM naming drift across chunks | Same concept can't connect across chunks | Tier 1+2 alignment; accept partial ambiguity |
| Single-chunk partial view | Conservative axioms, incomplete schema | By design: prefer empty over wrong |
| Contradictory axioms from different chunks | Inconsistent reasoning possible | Detect conflicts, log warning, don't block |
| LLM fabricates evidence | False axioms enter the system | Post-validation: evidence must be chunk text substring |
| Sidecar latency | Agent response slower | Default n3-extended; timeout → degrade to no-reasoning |
| Axiom space explosion | Large merged graph | Limit ≤ 50 chunks per query; 100K triple hard cap |

### 10.3 What This Design Cannot Do

- True OWL DL consistency proofs
- Equivalent class complete closure reasoning
- Complex class expressions (intersectionOf / unionOf / hasValue)
- Nominal reasoning (owl:oneOf)

Future escape hatch: add a `dl` profile that calls external Stardog/RDFox via HTTP, keeping JVM dependency optional.

---

## 11. Acceptance Criteria

### 11.1 Core MVP Validation

1. Upload 10 technical documents → micro-TBoxes extracted automatically for chunks with ≥ 2 entities
2. Query "all indirect dependencies of X" → correct transitive closure result via reasoner
3. Query "is X a kind of Y?" → correct subsumption answer using class hierarchy
4. SHACL validation query → correctly identifies constraint violations
5. Every reasoning result includes evidence chunk links
6. `ONTOLOGY_ENABLE=false` → zero impact on existing pipeline
7. End-to-end latency (query path): < 500ms for typical 20-chunk slice

### 11.2 Contract Verification

1. `consistency` request without SHACL → returns `status=error` (not `true`)
2. Missing `instance_facts` → sidecar auto-fetches, `data_source=fetched` in response
3. Default profile is `n3-extended` throughout

---

## 12. Future Extensions (Out of Scope)

These are documented in the original RFC (sections 12-15) and will become separate specs:

- **Expert Review & Feedback Loop** (section 12): reviewer UI, canonical_map curation, feedback-to-prompt loop
- **Hybrid Neuro-Symbolic Reasoning** (section 13): NL→SPARQL, soft association, SHACL repair suggestions, reasoning tape
- **TBox Lifecycle Governance** (section 14): KB-global TBox, promotion algorithm, versioning, migration
- **Federated SPARQL** (section 15): cross-KB reasoning, federation protocol, alignment resolution
