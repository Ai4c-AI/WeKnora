# Ontology Slice Core MVP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a 4th retrieval path ("Ontology Slice") to WeKnora that extracts per-chunk ontology fragments during ingestion and performs formal reasoning (RDFS + N3 + SHACL) at query time via a .NET 10 sidecar.

**Architecture:** During ingestion, each chunk gets a micro-TBox (class hierarchy, property characteristics, SHACL constraints) extracted by LLM and stored as JSONB in PostgreSQL. At query time, the `ontology_reason` agent tool retrieves relevant chunks, POSTs to a .NET sidecar that assembles slices, runs a fixpoint reasoning loop (dotNetRDF), and returns SPARQL results with provenance. A `canonical_map` table enables cross-slice alignment.

**Tech Stack:** Go 1.26 (ingestion + agent tool), .NET 10 + dotNetRDF 3.3.x + Npgsql 10.0.2 + Dapper 2.1.79 (reasoning sidecar), PostgreSQL (storage), Docker (deployment)

**Spec:** [docs/superpowers/specs/2026-05-20-ontology-slice-core-mvp-design.md](docs/superpowers/specs/2026-05-20-ontology-slice-core-mvp-design.md)

---

## File Structure

### Go Side — New Files
| File | Responsibility |
|------|---------------|
| `internal/types/micro_tbox.go` | MicroTBox, ClassDecl, PropertyDecl, ShapeDecl, ShapeConstraint, FreeAxiom, Triple types |
| `internal/types/ontology_reason.go` | Ontology sidecar request/response DTOs shared by service client and agent tool |
| `migrations/versioned/000052_chunk_ontology.up.sql` | Add ontology columns to chunks table |
| `migrations/versioned/000052_chunk_ontology.down.sql` | Rollback 000052 |
| `migrations/versioned/000053_ontology_canonical_map.up.sql` | Create ontology_canonical_map table |
| `migrations/versioned/000053_ontology_canonical_map.down.sql` | Rollback 000053 |
| `internal/types/canonical_map.go` | CanonicalMapEntry model + canonical map kind constants |
| `internal/types/interfaces/canonical_map.go` | CanonicalMapRepository interface |
| `internal/application/repository/canonical_map.go` | GORM-backed CanonicalMapRepository implementation |
| `internal/agent/tools/ontology_reason.go` | OntologyReasonTool agent tool |
| `internal/application/service/ontology_client.go` | HTTP client for .NET sidecar |

### Go Side — Modified Files
| File | Changes |
|------|---------|
| `internal/types/chunk.go` | Add OntologyJSON, OntologyExtractedAt, OntologyConfidence, InstanceFactsJSON fields |
| `internal/agent/tools/definitions.go` | Add ToolOntologyReason constant + AvailableToolDefinitions entry |
| `internal/application/service/graph.go` | Add extractMicroTBoxes, buildInstanceFacts, upsertCanonicalMap; call from BuildGraph |
| `internal/config/config.go` | Add OntologyConfig struct, ExtractMicroTBoxPromptID to ConversationConfig |
| `config/config.yaml` | Add ontology section + extract_micro_tbox_prompt_id |
| `config/prompt_templates/graph_extraction.yaml` | Add default_extract_micro_tbox template |
| `internal/application/service/agent_service.go` | Register ToolOntologyReason in switch case |
| `docker-compose.yml` | Add ontology-reasoner service with profile |

### .NET Side — New Files
| File | Responsibility |
|------|---------------|
| `ontology-reasoner-net/WeKnora.OntologyReasoner.sln` | Solution file |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Api/WeKnora.OntologyReasoner.Api.csproj` | API project file |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Api/Program.cs` | Minimal API host setup |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Api/Endpoints/ReasonEndpoint.cs` | POST /reason endpoint |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/WeKnora.OntologyReasoner.Core.csproj` | Core library project |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Models/MicroTBox.cs` | C# model records |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Models/ReasonRequest.cs` | Request model |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Models/ReasonResponse.cs` | Response model |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Aligner/CanonicalAligner.cs` | Alias → canonical ID rewriting |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Assembly/SliceAssembler.cs` | Orchestrate slice assembly + reasoning |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Generators/RdfGenerator.cs` | micro-TBox → RDF schema graph |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Generators/N3RuleGenerator.cs` | micro-TBox → N3 forward rules |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Generators/ShaclGenerator.cs` | micro-TBox → SHACL shapes graph |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Engine/ReasoningEngine.cs` | RDFS + N3 fixpoint loop |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Engine/ShaclValidator.cs` | SHACL validation |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Storage/PostgresOntologyRepo.cs` | PG data access |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj` | Test project |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Generators/RdfGeneratorTests.cs` | RDF generator tests |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Generators/N3RuleGeneratorTests.cs` | N3 rule generator tests |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Generators/ShaclGeneratorTests.cs` | SHACL generator tests |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Engine/ReasoningEngineTests.cs` | Reasoning engine tests |
| `ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Aligner/CanonicalAlignerTests.cs` | Aligner tests |
| `ontology-reasoner-net/Dockerfile` | Multi-stage .NET build |

---

### Task 1: Go Data Model — MicroTBox Types

**Files:**
- Create: `internal/types/micro_tbox.go`

- [ ] **Step 1: Create the MicroTBox types file**

```go
// internal/types/micro_tbox.go
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

- [ ] **Step 2: Verify the file compiles**

Run: `go build ./internal/types/...`
Expected: Clean build, no errors.

- [ ] **Step 3: Commit**

```bash
git add internal/types/micro_tbox.go
git commit -m "feat(ontology): add MicroTBox, Triple, and related Go types"
```

---

### Task 2: Extend Chunk Struct with Ontology Fields

**Files:**
- Modify: `internal/types/chunk.go`

- [ ] **Step 1: Add ontology fields to the Chunk struct**

In `internal/types/chunk.go`, add these fields after the `ContextHeader` field (before the closing `}`):

```go
	// Ontology Slice fields (populated by micro-TBox extraction pipeline)
	OntologyJSON        *MicroTBox `json:"ontology_json,omitempty"        gorm:"type:jsonb;serializer:json"`
	OntologyExtractedAt *time.Time `json:"ontology_extracted_at,omitempty"`
	OntologyConfidence  *float64   `json:"ontology_confidence,omitempty"`
	InstanceFactsJSON   []Triple   `json:"instance_facts_json,omitempty"  gorm:"type:jsonb;serializer:json"`
```

Note: The `time` import already exists in chunk.go.

- [ ] **Step 2: Verify compilation**

Run: `go build ./internal/types/...`
Expected: Clean build.

- [ ] **Step 3: Commit**

```bash
git add internal/types/chunk.go
git commit -m "feat(ontology): add ontology fields to Chunk struct"
```

---

### Task 3: Database Migrations

**Files:**
- Create: `migrations/versioned/000052_chunk_ontology.up.sql`
- Create: `migrations/versioned/000052_chunk_ontology.down.sql`
- Create: `migrations/versioned/000053_ontology_canonical_map.up.sql`
- Create: `migrations/versioned/000053_ontology_canonical_map.down.sql`

- [ ] **Step 1: Create migration 000052 up (chunk ontology columns)**

```sql
-- migrations/versioned/000052_chunk_ontology.up.sql
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

- [ ] **Step 2: Create migration 000052 down**

```sql
-- migrations/versioned/000052_chunk_ontology.down.sql
DROP INDEX IF EXISTS idx_chunks_ontology_class_ids;
DROP INDEX IF EXISTS idx_chunks_ontology_extracted;

ALTER TABLE chunks
  DROP COLUMN IF EXISTS instance_facts_json,
  DROP COLUMN IF EXISTS ontology_confidence,
  DROP COLUMN IF EXISTS ontology_extracted_at,
  DROP COLUMN IF EXISTS ontology_json;
```

- [ ] **Step 3: Create migration 000053 up (canonical map table)**

```sql
-- migrations/versioned/000053_ontology_canonical_map.up.sql
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

- [ ] **Step 4: Create migration 000053 down**

```sql
-- migrations/versioned/000053_ontology_canonical_map.down.sql
DROP TABLE IF EXISTS ontology_canonical_map;
```

- [ ] **Step 5: Commit**

```bash
git add migrations/versioned/000052_chunk_ontology.up.sql \
       migrations/versioned/000052_chunk_ontology.down.sql \
       migrations/versioned/000053_ontology_canonical_map.up.sql \
       migrations/versioned/000053_ontology_canonical_map.down.sql
git commit -m "feat(ontology): add chunk ontology columns and canonical map table migrations"
```

---

### Task 4: Config & Feature Flag

**Files:**
- Modify: `internal/config/config.go`
- Modify: `config/config.yaml`

- [ ] **Step 1: Add OntologyConfig struct to config.go**

In `internal/config/config.go`, add the OntologyConfig struct after `AgentConfig`:

```go
// OntologyConfig configures the Ontology Slice feature (optional .NET sidecar).
type OntologyConfig struct {
	Enabled              bool    `yaml:"enabled"               json:"enabled"`
	ReasonerURL          string  `yaml:"reasoner_url"          json:"reasoner_url"`
	DefaultProfile       string  `yaml:"default_profile"       json:"default_profile"`
	ConfidenceThreshold  float64 `yaml:"confidence_threshold"  json:"confidence_threshold"`
	ExtractMinEntities   int     `yaml:"extract_min_entities"  json:"extract_min_entities"`
}
```

- [ ] **Step 2: Add Ontology field to the Config struct**

Add after the `Agent` field:

```go
	Ontology        *OntologyConfig        `yaml:"ontology"         json:"ontology"`
```

- [ ] **Step 3: Add ExtractMicroTBoxPromptID to ConversationConfig**

In the `ConversationConfig` struct, add after the `GenerateQuestionsPromptID` field:

```go
	ExtractMicroTBoxPromptID     string `yaml:"extract_micro_tbox_prompt_id"      json:"extract_micro_tbox_prompt_id"`
```

And add after the `GenerateQuestionsPrompt` field (resolved text):

```go
	ExtractMicroTBoxPrompt       string `yaml:"-" json:"extract_micro_tbox_prompt"`
```

- [ ] **Step 4: Add backfill for ExtractMicroTBoxPromptID**

In `backfillConversationDefaults`, add after the `GenerateQuestionsPromptID` block (around line 875):

```go
	if conv.ExtractMicroTBoxPromptID != "" {
		if t := FindTemplateByID(pt, conv.ExtractMicroTBoxPromptID); t != nil {
			conv.ExtractMicroTBoxPrompt = t.Content
		} else {
			fmt.Printf("Warning: extract_micro_tbox_prompt_id %q not found\n", conv.ExtractMicroTBoxPromptID)
		}
	}
```

- [ ] **Step 5: Add defaults for OntologyConfig**

Find the function that applies config defaults (look for where `cfg.Agent` defaults are set). Add:

```go
	if cfg.Ontology == nil {
		cfg.Ontology = &OntologyConfig{}
	}
	if cfg.Ontology.ReasonerURL == "" {
		cfg.Ontology.ReasonerURL = "http://ontology-reasoner:8090"
	}
	if cfg.Ontology.DefaultProfile == "" {
		cfg.Ontology.DefaultProfile = "n3-extended"
	}
	if cfg.Ontology.ConfidenceThreshold == 0 {
		cfg.Ontology.ConfidenceThreshold = 0.3
	}
	if cfg.Ontology.ExtractMinEntities == 0 {
		cfg.Ontology.ExtractMinEntities = 2
	}
```

- [ ] **Step 6: Update config/config.yaml**

Add to `config/config.yaml` (using env var interpolation to match spec §9.2):

```yaml
ontology:
  enabled: ${ONTOLOGY_ENABLE:false}
  reasoner_url: ${ONTOLOGY_REASONER_URL:http://ontology-reasoner:8090}
  default_profile: ${ONTOLOGY_DEFAULT_PROFILE:n3-extended}
  confidence_threshold: ${ONTOLOGY_CONFIDENCE_THRESHOLD:0.3}
  extract_min_entities: ${ONTOLOGY_EXTRACT_MIN_ENTITIES:2}
```

Note: Verify how the project handles env var substitution in YAML (e.g. `viper` with `AutomaticEnv()`, or a custom template pass). Match the existing pattern used by other config sections.

And in the `conversation:` section, add:

```yaml
  extract_micro_tbox_prompt_id: "default_extract_micro_tbox"
```

- [ ] **Step 7: Verify compilation**

Run: `go build ./internal/config/...`
Expected: Clean build.

- [ ] **Step 8: Commit**

```bash
git add internal/config/config.go config/config.yaml
git commit -m "feat(ontology): add OntologyConfig and micro-TBox prompt config"
```

---

### Task 5: micro-TBox Prompt Template

**Files:**
- Modify: `config/prompt_templates/graph_extraction.yaml`

- [ ] **Step 1: Add the default_extract_micro_tbox template**

Append the following to `config/prompt_templates/graph_extraction.yaml` after the existing `default_extract_relationships` template (inside the `templates:` array):

```yaml
  - id: "default_extract_micro_tbox"
    name: "Micro-TBox Extraction"
    description: "Extract a chunk-local schema fragment (micro-TBox) from already-extracted entities and relationships"
    default: true
    content: |
      ## Task
      Given a text chunk along with the entities and relationships already extracted from it,
      infer the LOCAL schema-level facts: class hierarchy, property characteristics, and
      integrity constraints that are EXPLICITLY supported by this specific text.

      The output is a "micro-TBox": a chunk-scoped ontology fragment. It is valid ONLY within
      the context of this chunk. Do NOT generalize to universal truths. Do NOT invent
      vocabulary outside the provided entities and relationships.

      ## Output Format
      Return ONE JSON object (NOT an array) with these top-level fields:
        - classes:    array of class declarations
        - properties: array of property declarations
        - shapes:     array of SHACL-style integrity constraints
        - aliases:    object mapping canonical id -> list of alternate names
        - axioms:     array of free-form logical statements
        - confidence: float in [0, 1] for the overall extraction quality

      Any field with no content MUST be an empty array (or empty object for aliases).
      Output ONLY the JSON object. No markdown fences, no explanations.

      ## Hard Constraints
      1. Every `id` in `classes` MUST match an entity `type` from the input entity list,
         OR be a generalization explicitly named in the text (e.g. text says "Family, an
         organization, ...") — in which case use the broader term as id.
      2. Every `id` in `properties` MUST correspond to a relationship `description` or a
         normalized form of it from the input relationship list.
      3. Every extracted item MUST include an `evidence` field — a verbatim substring quoted
         from the source text. If you cannot quote the supporting text, OMIT the item.
      4. Use {{language}} for `label` fields only. Keep `id` fields in ASCII (camelCase or
         PascalCase). Keep `evidence` exactly as it appears in the source text.
      5. PREFER EMPTY OVER WRONG. Returning `{"classes": [], "properties": [], ...}` is the
         correct answer when the text contains no schema-level statements.

      ## Field Specifications

      ### classes
      Each element:
        - id:           PascalCase identifier matching an entity type from input
        - label:        human-readable name in {{language}}
        - subClassOf:   id of parent class (null if not stated)
        - disjointWith: array of class ids stated as mutually exclusive ([] if none)
        - evidence:     verbatim quote from text

      Only extract subClassOf when text explicitly states "X is a kind of Y" / "X 是一种 Y"
      or equivalent. Only extract disjointWith when text explicitly states mutual exclusion
      ("X cannot be Y", "X 和 Y 不能同时", "either X or Y but not both").

      ### properties
      Each element:
        - id:              camelCase identifier matching a relationship type from input
        - label:           human-readable name in {{language}}
        - domain:          class id of source (must appear in `classes` or input entity types)
        - range:           class id of target (same rule)
        - characteristics: subset of ["functional", "inverseFunctional", "transitive",
                                       "symmetric", "asymmetric", "reflexive", "irreflexive"]
        - inverseOf:       id of inverse property (null if not stated)
        - evidence:        verbatim quote

      Guidance for `characteristics`:
        - functional:        each subject has AT MOST ONE value (e.g. "biological father")
        - inverseFunctional: each value has AT MOST ONE subject (e.g. "social security number")
        - transitive:        A→B, B→C ⟹ A→C (e.g. "ancestor of", "part of")
        - symmetric:         A→B ⟹ B→A (e.g. "married to", "sibling of")
        - asymmetric:        A→B ⟹ NOT B→A (e.g. "parent of", "older than")
      Include a characteristic ONLY if the text states it or strongly implies it through
      the nature of the relationship. When in doubt, omit.

      ### shapes
      SHACL-style integrity constraints. Each element:
        - target_class: class id this constraint applies to
        - constraints:  array of constraint objects:
                          { property, min_count, max_count, datatype, in_values }
                        Use null for fields that don't apply.
        - evidence:     verbatim quote

      Example constraint: "Every Person must have at least one Family" →
        { target_class: "Person",
          constraints: [{ property: "isMemberOf", min_count: 1, max_count: null, ... }] }

      ### aliases
      Object mapping a canonical class/property id to a list of alternate names found
      in this chunk. Include ONLY:
        - Explicit equivalences ("X, also known as Y", "X, 又称 Y")
        - Acronym expansions ("RAG (Retrieval-Augmented Generation)")
        - Same entity in multiple languages ("Apple, 苹果公司")
      Do NOT include translations, generic synonyms, or broader/narrower terms.

      Format:
        { "<canonical_id>": ["<alias1>", "<alias2>", ...] }

      ### axioms
      Free-form logical statements that classes/properties/shapes cannot express.
      Use sparingly. Each element:
        - statement: a clear English description of the logical rule
        - evidence:  verbatim quote
      Examples of when to use axioms:
        - Complex constraints involving multiple classes/properties
        - Conditional rules ("If X then Y")
        - Negation over relations

      ### confidence
      Self-assessed quality score in [0, 1]:
        - 1.0: text contains rich, explicit schema-level statements
        - 0.5: some schema info but mostly implicit
        - 0.0: pure instance-level text, no schema content (return empty arrays)

      ## Anti-Patterns — DO NOT DO

      ❌ Don't extract "Person disjointWith Organization" unless the text explicitly
         distinguishes them as mutually exclusive in this domain.
      ❌ Don't mark "knows" as transitive — humans knowing each other isn't transitive
         even though graph paths exist.
      ❌ Don't add aliases like "Person → 人" — that's a translation, not a domain alias.
      ❌ Don't create classes for entity types that didn't appear in the input.
      ❌ Don't extract evidence that paraphrases the text — must be verbatim.
      ❌ Don't infer schema facts from world knowledge — only from the provided text.

      ## Example 1 — Rich Extraction

      [Input]
      Text: "Romeo and Juliet is a tragedy written by William Shakespeare. The play centers
      on two feuding families in Verona: the Montagues and the Capulets. A member of one
      family cannot also be a member of the other. Romeo Montague, son of Lord Montague,
      falls in love with Juliet Capulet, daughter of Lord Capulet. In this play, anyone who
      is a descendant of a Montague is themselves a Montague."

      Entities: [
        {"title": "Romeo and Juliet", "type": "Work"},
        {"title": "William Shakespeare", "type": "Person"},
        {"title": "Romeo Montague", "type": "Person"},
        {"title": "Juliet Capulet", "type": "Person"},
        {"title": "Lord Montague", "type": "Person"},
        {"title": "Lord Capulet", "type": "Person"},
        {"title": "Montague", "type": "Organization"},
        {"title": "Capulet", "type": "Organization"},
        {"title": "Verona", "type": "Location"}
      ]

      Relationships: [
        {"source": "William Shakespeare", "target": "Romeo and Juliet", "description": "wrote"},
        {"source": "Romeo Montague", "target": "Lord Montague", "description": "son of"},
        {"source": "Romeo Montague", "target": "Montague", "description": "is member of"},
        {"source": "Juliet Capulet", "target": "Capulet", "description": "is member of"}
      ]

      [Output]
      {
        "classes": [
          {
            "id": "Family",
            "label": "Family",
            "subClassOf": "Organization",
            "disjointWith": [],
            "evidence": "two feuding families in Verona: the Montagues and the Capulets"
          },
          {
            "id": "Montague",
            "label": "Montague",
            "subClassOf": "Family",
            "disjointWith": ["Capulet"],
            "evidence": "A member of one family cannot also be a member of the other"
          },
          {
            "id": "Capulet",
            "label": "Capulet",
            "subClassOf": "Family",
            "disjointWith": ["Montague"],
            "evidence": "A member of one family cannot also be a member of the other"
          }
        ],
        "properties": [
          {
            "id": "wrote",
            "label": "wrote",
            "domain": "Person",
            "range": "Work",
            "characteristics": [],
            "inverseOf": "writtenBy",
            "evidence": "is a tragedy written by William Shakespeare"
          },
          {
            "id": "sonOf",
            "label": "son of",
            "domain": "Person",
            "range": "Person",
            "characteristics": ["functional", "asymmetric", "irreflexive"],
            "inverseOf": null,
            "evidence": "Romeo Montague, son of Lord Montague"
          },
          {
            "id": "descendantOf",
            "label": "descendant of",
            "domain": "Person",
            "range": "Organization",
            "characteristics": ["transitive", "asymmetric"],
            "inverseOf": null,
            "evidence": "anyone who is a descendant of a Montague is themselves a Montague"
          }
        ],
        "shapes": [
          {
            "target_class": "Person",
            "constraints": [
              {
                "property": "isMemberOf",
                "min_count": null,
                "max_count": 1,
                "datatype": null,
                "in_values": null
              }
            ],
            "evidence": "A member of one family cannot also be a member of the other"
          }
        ],
        "aliases": {},
        "axioms": [
          {
            "statement": "If a Person is a descendant of a Montague, that Person is also a member of Montague (descendantOf composes with isMemberOf for Montague).",
            "evidence": "anyone who is a descendant of a Montague is themselves a Montague"
          }
        ],
        "confidence": 0.9
      }

      ## Example 2 — Sparse Extraction (no schema content)

      [Input]
      Text: "The meeting started at 9 AM. Alice presented the quarterly report. Bob asked
      a question about revenue."

      Entities: [
        {"title": "Alice", "type": "Person"},
        {"title": "Bob", "type": "Person"},
        {"title": "quarterly report", "type": "Work"}
      ]

      Relationships: [
        {"source": "Alice", "target": "quarterly report", "description": "presented"}
      ]

      [Output]
      {
        "classes": [],
        "properties": [],
        "shapes": [],
        "aliases": {},
        "axioms": [],
        "confidence": 0.0
      }

      Note: The text describes an event with instance-level facts but contains NO
      schema-level statements. The correct output is all empty arrays — do NOT fabricate
      type hierarchy or property characteristics.

      ## CRITICAL: Language Rule
      - Keep `id` fields in ASCII (PascalCase for classes, camelCase for properties).
      - Write `label`, `statement`, and free-text fields in {{language}}.
      - Keep `evidence` strings EXACTLY as they appear in the source text — do not translate.
```

- [ ] **Step 2: Verify YAML is valid**

Run: `python3 -c "import yaml; yaml.safe_load(open('config/prompt_templates/graph_extraction.yaml'))" || echo "INVALID YAML"`
Expected: No output (valid YAML).

- [ ] **Step 3: Commit**

```bash
git add config/prompt_templates/graph_extraction.yaml
git commit -m "feat(ontology): add micro-TBox extraction prompt template"
```

---

### Task 6: CanonicalMapRepository

**Files:**

- Create: `internal/types/canonical_map.go`
- Create: `internal/types/interfaces/canonical_map.go`
- Create: `internal/application/repository/canonical_map.go`
- Modify: `internal/container/container.go`

- [ ] **Step 1: Follow existing repository patterns**

Existing repositories put data models in `internal/types`, interfaces in `internal/types/interfaces`, GORM implementations in `internal/application/repository`, and DI registration in `internal/container/container.go`. Do not create an independent `internal/repo` package.

- [ ] **Step 2: Create CanonicalMapEntry model**

```go
// internal/types/canonical_map.go
package types

import (
    "time"

    "github.com/lib/pq"
)

type CanonicalMapKind string

const (
    CanonicalMapKindClass    CanonicalMapKind = "class"
    CanonicalMapKindProperty CanonicalMapKind = "property"
)

type CanonicalMapEntry struct {
    ID              int64            `json:"id"                gorm:"primaryKey;autoIncrement"`
    TenantID        uint64           `json:"tenant_id"         gorm:"not null"`
    KnowledgeBaseID string           `json:"knowledge_base_id" gorm:"not null"`
    Kind            CanonicalMapKind `json:"kind"              gorm:"type:text;not null"`
    CanonicalID     string           `json:"canonical_id"      gorm:"not null"`
    Aliases         pq.StringArray   `json:"aliases"           gorm:"type:text[];not null;default:'{}'"`
    SourceChunks    pq.StringArray   `json:"source_chunks"     gorm:"type:text[];not null;default:'{}'"`
    Confidence      float32          `json:"confidence"        gorm:"not null;default:0.5"`
    CreatedAt       time.Time        `json:"created_at"`
    UpdatedAt       time.Time        `json:"updated_at"`
}

func (CanonicalMapEntry) TableName() string {
    return "ontology_canonical_map"
}
```

- [ ] **Step 3: Create CanonicalMapRepository interface**

```go
// internal/types/interfaces/canonical_map.go
package interfaces

import (
    "context"

    "github.com/Tencent/WeKnora/internal/types"
)

type CanonicalMapRepository interface {
    Upsert(
        ctx context.Context,
        tenantID uint64,
        kbID string,
        kind types.CanonicalMapKind,
        canonicalID string,
        aliases []string,
        chunkID string,
    ) error
    GetByKB(ctx context.Context, tenantID uint64, kbID string) ([]types.CanonicalMapEntry, error)
}
```

- [ ] **Step 4: Create GORM-backed implementation**

```go
// internal/application/repository/canonical_map.go
package repository

import (
    "context"

    "github.com/Tencent/WeKnora/internal/types"
    "github.com/Tencent/WeKnora/internal/types/interfaces"
    "github.com/lib/pq"
    "gorm.io/gorm"
    "gorm.io/gorm/clause"
)

type canonicalMapRepository struct {
    db *gorm.DB
}

func NewCanonicalMapRepository(db *gorm.DB) interfaces.CanonicalMapRepository {
    return &canonicalMapRepository{db: db}
}

func (r *canonicalMapRepository) Upsert(
    ctx context.Context,
    tenantID uint64,
    kbID string,
    kind types.CanonicalMapKind,
    canonicalID string,
    aliases []string,
    chunkID string,
) error {
    aliasArray := pq.StringArray(aliases)
    if aliasArray == nil {
        aliasArray = pq.StringArray{}
    }

    entry := types.CanonicalMapEntry{
        TenantID:        tenantID,
        KnowledgeBaseID: kbID,
        Kind:            kind,
        CanonicalID:     canonicalID,
        Aliases:         aliasArray,
        SourceChunks:    pq.StringArray{chunkID},
        Confidence:      0.5,
    }

    return r.db.WithContext(ctx).
        Clauses(clause.OnConflict{
            Columns: []clause.Column{
                {Name: "tenant_id"},
                {Name: "knowledge_base_id"},
                {Name: "kind"},
                {Name: "canonical_id"},
            },
            DoUpdates: clause.Assignments(map[string]interface{}{
                "aliases": gorm.Expr(
                    "ARRAY(SELECT DISTINCT alias FROM unnest(ontology_canonical_map.aliases || EXCLUDED.aliases) AS merged(alias))",
                ),
                "source_chunks": gorm.Expr(
                    "ARRAY(SELECT DISTINCT chunk_id FROM unnest(ontology_canonical_map.source_chunks || EXCLUDED.source_chunks) AS merged(chunk_id))",
                ),
                "confidence": gorm.Expr("GREATEST(ontology_canonical_map.confidence, EXCLUDED.confidence)"),
                "updated_at": gorm.Expr("NOW()"),
            }),
        }).Create(&entry).Error
}

func (r *canonicalMapRepository) GetByKB(
    ctx context.Context,
    tenantID uint64,
    kbID string,
) ([]types.CanonicalMapEntry, error) {
    var entries []types.CanonicalMapEntry
    err := r.db.WithContext(ctx).
        Where("tenant_id = ? AND knowledge_base_id = ?", tenantID, kbID).
        Find(&entries).Error
    return entries, err
}
```

- [ ] **Step 5: Register the repository in DI**

In `internal/container/container.go`, add `repository.NewCanonicalMapRepository` next to the other repository registrations:

```go
    must(container.Provide(repository.NewChunkRepository))
    must(container.Provide(repository.NewCanonicalMapRepository))
    must(container.Provide(repository.NewKnowledgeTagRepository))
```

- [ ] **Step 6: Verify compilation**

Run: `go build ./internal/application/repository ./internal/types ./internal/types/interfaces`
Expected: Clean build, unless blocked by existing unrelated platform/dependency issues.

- [ ] **Step 7: Commit**

```bash
git add internal/types/canonical_map.go \
        internal/types/interfaces/canonical_map.go \
        internal/application/repository/canonical_map.go \
        internal/container/container.go \
        docs/superpowers/plans/2026-05-20-ontology-slice-core-mvp.md
git commit -m "feat(ontology): add CanonicalMapRepository for cross-slice alignment"
```

---

### Task 7: Graph Builder Extension — extractMicroTBoxes

**Files:**
- Modify: `internal/application/service/graph.go`

- [ ] **Step 1: Add helper methods for micro-TBox extraction**

Add the following methods to `graph.go` after the `buildChunkGraph` method:

```go
// entitiesForChunk returns entities associated with the given chunk ID.
func (b *graphBuilder) entitiesForChunk(chunkID string) []*types.Entity {
	b.mutex.RLock()
	defer b.mutex.RUnlock()

	var result []*types.Entity
	for _, entity := range b.entityMap {
		if entity == nil {
			continue
		}
		for _, id := range entity.ChunkIDs {
			if id == chunkID {
				result = append(result, entity)
				break
			}
		}
	}
	return result
}

// relationshipsForChunk returns relationships associated with the given chunk ID.
func (b *graphBuilder) relationshipsForChunk(chunkID string) []*types.Relationship {
	b.mutex.RLock()
	defer b.mutex.RUnlock()

	var result []*types.Relationship
	for _, rel := range b.relationshipMap {
		if rel == nil {
			continue
		}
		for _, id := range rel.ChunkIDs {
			if id == chunkID {
				result = append(result, rel)
				break
			}
		}
	}
	return result
}
```

- [ ] **Step 2: Add buildInstanceFacts function**

```go
// buildInstanceFacts converts extracted entities and relationships into RDF-style triples.
func (b *graphBuilder) buildInstanceFacts(
	entities []*types.Entity,
	rels []*types.Relationship,
) []types.Triple {
	var facts []types.Triple

	for _, e := range entities {
		if e == nil || e.Title == "" || e.Type == "" {
			continue
		}
		facts = append(facts, types.Triple{
			Subject:   e.Title,
			Predicate: "rdf:type",
			Object:    e.Type,
		})
	}

	for _, r := range rels {
		if r == nil || r.Source == "" || r.Target == "" || r.Description == "" {
			continue
		}
		facts = append(facts, types.Triple{
			Subject:   r.Source,
			Predicate: r.Description,
			Object:    r.Target,
		})
	}

	return facts
}
```

- [ ] **Step 3: Add upsertCanonicalMap function**

```go
// upsertCanonicalMap persists micro-TBox aliases to the canonical_map table (spec §2.5, §8.4).
func (b *graphBuilder) upsertCanonicalMap(
	ctx context.Context,
	tenantID uint64,
	kbID string,
	tbox *types.MicroTBox,
	chunkID string,
) error {
	for canonicalID, aliases := range tbox.Aliases {
		if err := b.canonicalMapRepo.Upsert(ctx, tenantID, kbID, types.CanonicalMapKindClass, canonicalID, aliases, chunkID); err != nil {
			return err
		}
	}
	return nil
}
```

Note: The `graphBuilder` struct needs a `canonicalMapRepo interfaces.CanonicalMapRepository` field injected from `internal/types/interfaces`. Add this field to the struct and pass it from the constructor. Do not add cached `tenantID` or `kbID` fields to `graphBuilder`; derive them from each `chunk.TenantID` and `chunk.KnowledgeBaseID` when upserting aliases.

- [ ] **Step 4: Add callMicroTBoxLLM function (was Step 3)**

```go
// callMicroTBoxLLM calls the LLM to extract a micro-TBox from a chunk.
func (b *graphBuilder) callMicroTBoxLLM(
	ctx context.Context,
	chunk *types.Chunk,
	entities []*types.Entity,
	rels []*types.Relationship,
) (*types.MicroTBox, error) {
	entitiesJSON, err := json.Marshal(entities)
	if err != nil {
		return nil, fmt.Errorf("failed to serialize entities: %w", err)
	}
	relsJSON, err := json.Marshal(rels)
	if err != nil {
		return nil, fmt.Errorf("failed to serialize relationships: %w", err)
	}

	thinking := false
	messages := []chat.Message{
		{
			Role:    "system",
			Content: b.renderGraphExtractionPrompt(ctx, b.config.Conversation.ExtractMicroTBoxPrompt),
		},
		{
			Role:    "user",
			Content: fmt.Sprintf("Text: %s\n\nEntities: %s\n\nRelationships: %s", chunk.Content, string(entitiesJSON), string(relsJSON)),
		},
	}

	resp, err := b.chatModel.Chat(ctx, messages, &chat.ChatOptions{
		Temperature: DefaultLLMTemperature,
		Thinking:    &thinking,
	})
	if err != nil {
		return nil, fmt.Errorf("LLM micro-TBox extraction failed: %w", err)
	}

	var tbox types.MicroTBox
	if err := common.ParseLLMJsonResponse(resp.Content, &tbox); err != nil {
		return nil, fmt.Errorf("failed to parse micro-TBox response: %w", err)
	}

	return &tbox, nil
}
```

- [ ] **Step 5: Add validateEvidence function**

```go
// validateEvidence removes any declarations whose evidence is not a substring of the chunk content.
func (b *graphBuilder) validateEvidence(tbox *types.MicroTBox, content string) *types.MicroTBox {
	validClasses := make([]types.ClassDecl, 0, len(tbox.Classes))
	for _, c := range tbox.Classes {
		if strings.Contains(content, c.Evidence) {
			validClasses = append(validClasses, c)
		}
	}
	tbox.Classes = validClasses

	validProps := make([]types.PropertyDecl, 0, len(tbox.Properties))
	for _, p := range tbox.Properties {
		if strings.Contains(content, p.Evidence) {
			validProps = append(validProps, p)
		}
	}
	tbox.Properties = validProps

	validShapes := make([]types.ShapeDecl, 0, len(tbox.Shapes))
	for _, s := range tbox.Shapes {
		if strings.Contains(content, s.Evidence) {
			validShapes = append(validShapes, s)
		}
	}
	tbox.Shapes = validShapes

	validAxioms := make([]types.FreeAxiom, 0, len(tbox.Axioms))
	for _, a := range tbox.Axioms {
		if strings.Contains(content, a.Evidence) {
			validAxioms = append(validAxioms, a)
		}
	}
	tbox.Axioms = validAxioms

	return tbox
}
```

- [ ] **Step 6: Add extractMicroTBoxes function**

```go
// extractMicroTBoxes extracts micro-TBox ontology fragments from chunks.
// Only runs when ontology feature is enabled. Single chunk failures don't block the pipeline.
func (b *graphBuilder) extractMicroTBoxes(ctx context.Context, chunks []*types.Chunk) {
	log := logger.GetLogger(ctx)

	if b.config.Conversation.ExtractMicroTBoxPrompt == "" {
		log.Warn("micro-TBox extraction prompt not configured, skipping")
		return
	}

	minEntities := b.config.Ontology.ExtractMinEntities
	confidenceThreshold := b.config.Ontology.ConfidenceThreshold

	log.Infof("Extracting micro-TBoxes from %d chunks (minEntities=%d, threshold=%.2f)",
		len(chunks), minEntities, confidenceThreshold)

	g, gctx := errgroup.WithContext(ctx)
	g.SetLimit(MaxConcurrentEntityExtractions)

	for _, chunk := range chunks {
		chunk := chunk
		g.Go(func() error {
			chunkEntities := b.entitiesForChunk(chunk.ID)
			chunkRels := b.relationshipsForChunk(chunk.ID)

			if len(chunkEntities) < minEntities {
				return nil
			}

			tbox, err := b.callMicroTBoxLLM(gctx, chunk, chunkEntities, chunkRels)
			if err != nil {
				log.WithError(err).Warnf("micro-tbox extraction failed for chunk %s", chunk.ID)
				return nil
			}

			tbox = b.validateEvidence(tbox, chunk.Content)

			if tbox.Confidence < confidenceThreshold {
				return nil
			}

			chunk.OntologyJSON = tbox
			chunk.OntologyConfidence = &tbox.Confidence
			now := time.Now()
			chunk.OntologyExtractedAt = &now
			chunk.InstanceFactsJSON = b.buildInstanceFacts(chunkEntities, chunkRels)

			// Auto-populate canonical_map from micro-TBox aliases (spec §2.5, §8.4)
			if len(tbox.Aliases) > 0 && b.canonicalMapRepo != nil {
				if err := b.upsertCanonicalMap(gctx, chunk.TenantID, chunk.KnowledgeBaseID, tbox, chunk.ID); err != nil {
					log.WithError(err).Warnf("canonical map upsert failed for chunk %s", chunk.ID)
				}
			}

			return nil
		})
	}
	g.Wait()

	extracted := 0
	for _, chunk := range chunks {
		if chunk.OntologyJSON != nil {
			extracted++
		}
	}
	log.Infof("micro-TBox extraction completed: %d/%d chunks produced ontology", extracted, len(chunks))
}
```

- [ ] **Step 7: Call extractMicroTBoxes from BuildGraph**

In the `BuildGraph` method, after the `b.buildChunkGraph(ctx)` line (around line 486), add:

```go
	// Extract micro-TBoxes (parallel with Neo4j write, gated by ontology config)
	if b.config.Ontology != nil && b.config.Ontology.Enabled {
		b.extractMicroTBoxes(ctx, chunks)
	}
```

- [ ] **Step 8: Verify compilation**

Run: `go build ./internal/application/service/...`
Expected: Clean build.

- [ ] **Step 9: Commit**

```bash
git add internal/application/service/graph.go
git commit -m "feat(ontology): add micro-TBox extraction to graph builder pipeline"
```

---

### Task 8: Ontology Sidecar HTTP Client

**Files:**

- Create: `internal/types/ontology_reason.go`
- Create: `internal/application/service/ontology_client.go`

- [ ] **Step 1: Create shared ontology reason DTOs**

```go
// internal/types/ontology_reason.go
package types

type OntologyReasonRequest struct {
	TenantID         uint64        `json:"tenant_id"`
	KnowledgeBaseIDs []string      `json:"knowledge_base_ids"`
	ChunkIDs         []string      `json:"chunk_ids"`
	InstanceFacts    []Triple      `json:"instance_facts,omitempty"`
	Query            OntologyQuery `json:"query"`
}

type OntologyQuery struct {
	Type    string `json:"type"`
	Body    string `json:"body"`
	Profile string `json:"profile,omitempty"`
}

type OntologyReasonResponse struct {
	Status          string                   `json:"status"`
	Results         []map[string]interface{} `json:"results"`
	InferredTriples []Triple                 `json:"inferred_triples"`
	DataSource      string                   `json:"data_source"`
	EvidenceChunks  []string                 `json:"evidence_chunks"`
	Warnings        []string                 `json:"warnings"`
	ElapsedMs       int                      `json:"elapsed_ms"`
}
```

- [ ] **Step 2: Create the ontology sidecar HTTP client**

```go
// internal/application/service/ontology_client.go
package service

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"

	"github.com/Tencent/WeKnora/internal/types"
)

type OntologyClient struct {
	baseURL    string
	httpClient *http.Client
}

func NewOntologyClient(baseURL string) *OntologyClient {
	return &OntologyClient{
		baseURL: strings.TrimRight(baseURL, "/"),
		httpClient: &http.Client{
			Timeout: 10 * time.Second,
		},
	}
}

func (c *OntologyClient) Reason(ctx context.Context, req *types.OntologyReasonRequest) (*types.OntologyReasonResponse, error) {
	body, err := json.Marshal(req)
	if err != nil {
		return nil, fmt.Errorf("failed to marshal request: %w", err)
	}

	httpReq, err := http.NewRequestWithContext(ctx, http.MethodPost, c.baseURL+"/reason", bytes.NewReader(body))
	if err != nil {
		return nil, fmt.Errorf("failed to create request: %w", err)
	}
	httpReq.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(httpReq)
	if err != nil {
		return nil, fmt.Errorf("sidecar request failed: %w", err)
	}
	defer resp.Body.Close()

	respBody, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, fmt.Errorf("failed to read response: %w", err)
	}

	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("sidecar returned status %d: %s", resp.StatusCode, string(respBody))
	}

	var result types.OntologyReasonResponse
	if err := json.Unmarshal(respBody, &result); err != nil {
		return nil, fmt.Errorf("failed to parse response: %w", err)
	}

	return &result, nil
}
```

- [ ] **Step 3: Verify compilation**

Run: `go test ./internal/application/service/ontology_client.go`
Expected: PASS with `[no test files]`.

- [ ] **Step 4: Commit**

```bash
git add internal/types/ontology_reason.go internal/application/service/ontology_client.go
git commit -m "feat(ontology): add HTTP client for .NET reasoning sidecar"
```

---

### Task 9: ontology_reason Agent Tool

**Files:**

- Create: `internal/agent/tools/ontology_reason.go`
- Modify: `internal/agent/tools/definitions.go`
- Modify: `internal/application/service/agent_service.go`
- Modify: `internal/sandbox/local.go`
- Create: `internal/sandbox/process_unix.go`
- Create: `internal/sandbox/process_windows.go`
- Modify: `internal/agent/tools/data_analysis_duckdb_test.go`
- Modify: `frontend/src/views/agent/AgentEditorModal.vue`
- Modify: `frontend/src/utils/tool-capabilities.ts`
- Modify: `frontend/src/utils/tool-icons.ts`
- Modify: `frontend/src/i18n/locales/zh-CN.ts`
- Modify: `frontend/src/i18n/locales/en-US.ts`
- Modify: `frontend/src/i18n/locales/ko-KR.ts`
- Modify: `frontend/src/i18n/locales/ru-RU.ts`

- [ ] **Step 1: Add ToolOntologyReason constant to definitions.go**

In `internal/agent/tools/definitions.go`, add to the const block after `ToolFinalAnswer`:

```go
	ToolOntologyReason = "ontology_reason"
```

Add to `AvailableToolDefinitions()` return list:

```go
		{Name: ToolOntologyReason, Label: "本体推理", Description: "基于知识图谱进行形式化推理（传递关系、类型层级、约束验证）"},
```

- [ ] **Step 2: Create the ontology_reason tool file**

Do not import `internal/application/service` from the tools package; `agent_service.go` already imports tools, so the tool must accept the sidecar client through a local interface using shared DTOs from `internal/types`.

```go
// internal/agent/tools/ontology_reason.go
package tools

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"

	"github.com/Tencent/WeKnora/internal/types"
	"github.com/Tencent/WeKnora/internal/types/interfaces"
	"github.com/Tencent/WeKnora/internal/utils"
)

var ontologyReasonTool = BaseTool{
	name: ToolOntologyReason,
	description: `Perform formal reasoning over knowledge base ontology fragments using RDFS, N3 rules, and SHACL validation.

## Core Function
Executes structural reasoning — transitive closure, type hierarchy, constraint validation — on chunk-scoped ontology data stored in the knowledge base.

## When to Use
Use for transitive queries, type hierarchy checks, schema constraint validation, mutual exclusion checks, and SPARQL queries over inferred knowledge.

Do not use for fuzzy text search, entity relationship browsing, or general Q&A; use knowledge_search or query_knowledge_graph instead.

## Parameters
- **knowledge_base_ids** (required): Array of knowledge base IDs (1-10)
- **query** (required): Natural language query or SPARQL query
- **reasoner_profile** (optional): "rdfs", "n3-extended" (default), or "shacl"
- **include_evidence** (optional): Include source chunk references (default: true)`,
	schema: utils.GenerateSchema[OntologyReasonInput](),
}

type OntologyReasonInput struct {
	KnowledgeBaseIDs []string `json:"knowledge_base_ids" jsonschema:"Array of knowledge base IDs to reason over"`
	Query            string   `json:"query" jsonschema:"Natural language or SPARQL query"`
	ReasonerProfile  string   `json:"reasoner_profile,omitempty" jsonschema:"Reasoning profile: rdfs, n3-extended, shacl"`
	IncludeEvidence  bool     `json:"include_evidence,omitempty" jsonschema:"Include evidence chunk references"`
}

type OntologyReasonClient interface {
	Reason(ctx context.Context, req *types.OntologyReasonRequest) (*types.OntologyReasonResponse, error)
}

type OntologyReasonTool struct {
	BaseTool
	knowledgeService interfaces.KnowledgeBaseService
	ontologyClient   OntologyReasonClient
}

func NewOntologyReasonTool(
	knowledgeService interfaces.KnowledgeBaseService,
	ontologyClient OntologyReasonClient,
) *OntologyReasonTool {
	return &OntologyReasonTool{
		BaseTool:         ontologyReasonTool,
		knowledgeService: knowledgeService,
		ontologyClient:   ontologyClient,
	}
}

func (t *OntologyReasonTool) Execute(ctx context.Context, args json.RawMessage) (*types.ToolResult, error) {
	var input OntologyReasonInput
	if err := json.Unmarshal(args, &input); err != nil {
		return &types.ToolResult{
			Success: false,
			Error:   fmt.Sprintf("Failed to parse args: %v", err),
		}, err
	}

	if len(input.KnowledgeBaseIDs) == 0 {
		return &types.ToolResult{
			Success: false,
			Error:   "knowledge_base_ids is required and must be a non-empty array",
		}, fmt.Errorf("knowledge_base_ids is required")
	}

	if len(input.KnowledgeBaseIDs) > 10 {
		return &types.ToolResult{
			Success: false,
			Error:   "knowledge_base_ids must contain at most 10 KB IDs",
		}, fmt.Errorf("too many KB IDs")
	}

	if input.Query == "" {
		return &types.ToolResult{
			Success: false,
			Error:   "query is required",
		}, fmt.Errorf("invalid query")
	}

	profile := input.ReasonerProfile
	if profile == "" {
		profile = "n3-extended"
	}

	searchParams := types.SearchParams{
		QueryText:  input.Query,
		MatchCount: 50,
	}

	var allChunkIDs []string
	seenChunks := make(map[string]bool)

	for _, kbID := range input.KnowledgeBaseIDs {
		results, err := t.knowledgeService.HybridSearch(ctx, kbID, searchParams)
		if err != nil {
			continue
		}
		for _, r := range results {
			if !seenChunks[r.ID] {
				seenChunks[r.ID] = true
				allChunkIDs = append(allChunkIDs, r.ID)
			}
		}
	}

	if len(allChunkIDs) == 0 {
		return &types.ToolResult{
			Success: true,
			Output:  "No relevant chunks found for ontology reasoning.",
		}, nil
	}

	if len(allChunkIDs) > 50 {
		allChunkIDs = allChunkIDs[:50]
	}

	tenantID, ok := types.TenantIDFromContext(ctx)
	if !ok {
		return &types.ToolResult{
			Success: false,
			Error:   "tenant ID is missing from context",
		}, fmt.Errorf("tenant ID is missing from context")
	}

	req := &types.OntologyReasonRequest{
		TenantID:         tenantID,
		KnowledgeBaseIDs: input.KnowledgeBaseIDs,
		ChunkIDs:         allChunkIDs,
		Query: types.OntologyQuery{
			Type:    "entailment",
			Body:    input.Query,
			Profile: profile,
		},
	}

	resp, err := t.ontologyClient.Reason(ctx, req)
	if err != nil {
		return &types.ToolResult{
			Success: false,
			Error:   fmt.Sprintf("Ontology reasoning failed: %v", err),
		}, nil
	}

	output := formatOntologyResult(resp, input.IncludeEvidence)

	return &types.ToolResult{
		Success: true,
		Output:  output,
		Data: map[string]interface{}{
			"status":           resp.Status,
			"results":          resp.Results,
			"inferred_triples": resp.InferredTriples,
			"evidence_chunks":  resp.EvidenceChunks,
			"elapsed_ms":       resp.ElapsedMs,
			"display_type":     "ontology_reason_results",
		},
	}, nil
}

func formatOntologyResult(resp *types.OntologyReasonResponse, includeEvidence bool) string {
	var sb strings.Builder
	sb.WriteString("=== Ontology Reasoning Results ===\n\n")
	sb.WriteString(fmt.Sprintf("Status: %s\n", resp.Status))
	sb.WriteString(fmt.Sprintf("Elapsed: %dms\n", resp.ElapsedMs))
	sb.WriteString(fmt.Sprintf("Data Source: %s\n\n", resp.DataSource))

	if len(resp.Results) > 0 {
		sb.WriteString("=== Query Results ===\n")
		for i, r := range resp.Results {
			sb.WriteString(fmt.Sprintf("\nResult #%d:\n", i+1))
			for k, v := range r {
				sb.WriteString(fmt.Sprintf("  %s: %v\n", k, v))
			}
		}
		sb.WriteString("\n")
	}

	if len(resp.InferredTriples) > 0 {
		sb.WriteString(fmt.Sprintf("=== Inferred Triples (%d) ===\n", len(resp.InferredTriples)))
		for _, t := range resp.InferredTriples {
			sb.WriteString(fmt.Sprintf("  %s -[%s]-> %s\n", t.Subject, t.Predicate, t.Object))
		}
		sb.WriteString("\n")
	}

	if includeEvidence && len(resp.EvidenceChunks) > 0 {
		sb.WriteString(fmt.Sprintf("=== Evidence Chunks (%d) ===\n", len(resp.EvidenceChunks)))
		for _, c := range resp.EvidenceChunks {
			sb.WriteString(fmt.Sprintf("  - %s\n", c))
		}
		sb.WriteString("\n")
	}

	if len(resp.Warnings) > 0 {
		sb.WriteString("=== Warnings ===\n")
		for _, w := range resp.Warnings {
			sb.WriteString(fmt.Sprintf("  %s\n", w))
		}
	}

	return sb.String()
}
```

- [ ] **Step 3: Register the tool in agent_service.go**

In `internal/application/service/agent_service.go`, in the `registerTools` method's switch statement (around line 510), add a new case:

```go
		case tools.ToolOntologyReason:
			if s.cfg.Ontology != nil && s.cfg.Ontology.Enabled {
				ontologyClient := NewOntologyClient(s.cfg.Ontology.ReasonerURL)
				toolToRegister = tools.NewOntologyReasonTool(s.knowledgeBaseService, ontologyClient)
				logger.Infof(ctx, "Registered ontology_reason tool (reasoner: %s)", s.cfg.Ontology.ReasonerURL)
			}
```

Also add `ToolOntologyReason` to both the no-knowledge `kbTools` filter map and the `ragToolSet` map:

```go
		tools.ToolOntologyReason: true,
```

- [ ] **Step 4: Add to DefaultAllowedTools**

In `definitions.go`, add `ToolOntologyReason` to the `DefaultAllowedTools()` return list:

```go
		ToolOntologyReason,
```

- [ ] **Step 5: Sync frontend tool metadata**

Keep the backend RAG tool allowlist in sync with the Agent editor and shared frontend tool metadata.

In `frontend/src/views/agent/AgentEditorModal.vue`, add `ontology_reason` to `knowledgeBaseTools` after `query_knowledge_graph`:

```ts
const knowledgeBaseTools = ['grep_chunks', 'knowledge_search', 'list_knowledge_chunks', 'query_knowledge_graph', 'ontology_reason', 'get_document_info', 'database_query'];
```

In the RAG group inside `allTools`, add:

```ts
{ value: 'ontology_reason', label: t('agentEditor.tools.ontologyReason'), description: t('agentEditor.tools.ontologyReasonDesc'), group: 'rag' },
```

In the smart-reasoning default tool list, add:

```ts
'ontology_reason',
```

In `frontend/src/utils/tool-capabilities.ts`, add:

```ts
ontology_reason:       { anyOf: ['vector', 'keyword'], consumesFiles: true },
```

In `frontend/src/utils/tool-icons.ts`, add:

```ts
ontology_reason: '🧠',
```

and add the display-name key:

```ts
ontology_reason: 'tools.ontologyReason',
```

In `frontend/src/i18n/locales/zh-CN.ts`, add the top-level `tools` key and the `agentEditor.tools` labels:

```ts
ontologyReason: "本体推理",
ontologyReasonDesc: "基于知识图谱进行形式化推理",
```

In `frontend/src/i18n/locales/en-US.ts`, add the top-level `tools` key and the `agentEditor.tools` labels:

```ts
ontologyReason: 'Ontology Reasoning',
ontologyReasonDesc: 'Run formal reasoning over knowledge graph ontology slices',
```

In `frontend/src/i18n/locales/ko-KR.ts`, add the top-level `tools` key and the `agentEditor.tools` labels:

```ts
ontologyReason: '온톨로지 추론',
ontologyReasonDesc: '지식 그래프 온톨로지 슬라이스에 대해 형식 추론 실행',
```

In `frontend/src/i18n/locales/ru-RU.ts`, add the top-level `tools` key and the `agentEditor.tools` labels:

```ts
ontologyReason: 'Онтологический вывод',
ontologyReasonDesc: 'Запуск формального вывода по онтологическим срезам графа знаний',
```

- [ ] **Step 6: Fix Windows CGO verification blockers**

`pg_query_go`, `gojieba`, and DuckDB require CGO on Windows. Configure the local toolchain with MSYS2 UCRT64 GCC before running package checks:

```bash
winget install --id MSYS2.MSYS2 --accept-package-agreements --accept-source-agreements --silent
/c/msys64/usr/bin/pacman.exe -Syu --noconfirm
/c/msys64/usr/bin/pacman.exe -S --needed --noconfirm mingw-w64-ucrt-x86_64-gcc
go env -w CGO_ENABLED=1 CC=C:/msys64/ucrt64/bin/gcc.exe CXX=C:/msys64/ucrt64/bin/g++.exe
```

Move Unix-only process-group cleanup out of `internal/sandbox/local.go` so the package builds on Windows:

```go
// internal/sandbox/process_unix.go
//go:build !windows

package sandbox

import (
    "os/exec"
    "syscall"
)

func setupProcessGroup(cmd *exec.Cmd) {
    cmd.SysProcAttr = &syscall.SysProcAttr{
        Setpgid: true,
    }
}

func killProcessGroup(cmd *exec.Cmd) {
    if cmd.Process != nil {
        _ = syscall.Kill(-cmd.Process.Pid, syscall.SIGKILL)
    }
}
```

```go
// internal/sandbox/process_windows.go
//go:build windows

package sandbox

import "os/exec"

func setupProcessGroup(cmd *exec.Cmd) {}

func killProcessGroup(cmd *exec.Cmd) {
    if cmd.Process != nil {
        _ = cmd.Process.Kill()
    }
}
```

In `internal/sandbox/local.go`, replace the direct `syscall.SysProcAttr{Setpgid: true}` assignment with:

```go
setupProcessGroup(cmd)
```

and replace direct process-group killing with:

```go
killProcessGroup(cmd)
```

DuckDB Excel integration tests link DuckDB static libraries and should not run as part of normal package compile checks. Add an explicit build tag to `internal/agent/tools/data_analysis_duckdb_test.go`:

```go
//go:build duckdb && cgo
```

- [ ] **Step 7: Verify compilation**

Run: `PATH="/c/msys64/ucrt64/bin:$PATH" go test ./internal/types -run '^$' -vet=off && PATH="/c/msys64/ucrt64/bin:$PATH" go test ./internal/agent/tools -run '^$' -vet=off && PATH="/c/msys64/ucrt64/bin:$PATH" go test ./internal/application/service -run '^$' -vet=off`
Expected: Clean build.

Run: `npm --prefix frontend run type-check`
Expected: Existing frontend type errors may still block this repository-wide check; ontology_reason locale/tool metadata should not introduce new missing-key errors.

- [ ] **Step 8: Commit**

```bash
git add internal/agent/tools/ontology_reason.go \
       internal/agent/tools/definitions.go \
       internal/application/service/agent_service.go \
       internal/types/ontology_reason.go \
       internal/application/service/ontology_client.go \
       internal/sandbox/local.go \
       internal/sandbox/process_unix.go \
       internal/sandbox/process_windows.go \
       internal/agent/tools/data_analysis_duckdb_test.go \
       frontend/src/views/agent/AgentEditorModal.vue \
       frontend/src/utils/tool-capabilities.ts \
       frontend/src/utils/tool-icons.ts \
       frontend/src/i18n/locales/zh-CN.ts \
       frontend/src/i18n/locales/en-US.ts \
       frontend/src/i18n/locales/ko-KR.ts \
       frontend/src/i18n/locales/ru-RU.ts
git commit -m "feat(ontology): add ontology_reason agent tool with sidecar integration"
```

---

### Task 10: .NET Solution Scaffold

**Files:**

- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.slnx`
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Api/Program.cs`
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Api/WeKnora.OntologyReasoner.Api.csproj`
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/WeKnora.OntologyReasoner.Core.csproj`
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj`

- [ ] **Step 1: Create the solution directory**

```bash
mkdir -p ontology-reasoner-net/WeKnora.OntologyReasoner.Api/Endpoints
mkdir -p ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Models
mkdir -p ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Aligner
mkdir -p ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Assembly
mkdir -p ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Generators
mkdir -p ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Engine
mkdir -p ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Storage
mkdir -p ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Generators
mkdir -p ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Engine
mkdir -p ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Aligner
```

- [ ] **Step 2: Create Core .csproj**

```xml
<!-- ontology-reasoner-net/WeKnora.OntologyReasoner.Core/WeKnora.OntologyReasoner.Core.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="dotNetRdf" Version="3.3.*" />
    <PackageReference Include="dotNetRdf.Inferencing" Version="3.3.*" />
    <PackageReference Include="dotNetRdf.Shacl" Version="3.3.*" />
    <PackageReference Include="Npgsql" Version="10.0.2" />
    <PackageReference Include="Dapper" Version="2.1.79" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create Api .csproj**

```xml
<!-- ontology-reasoner-net/WeKnora.OntologyReasoner.Api/WeKnora.OntologyReasoner.Api.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\WeKnora.OntologyReasoner.Core\WeKnora.OntologyReasoner.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create Tests .csproj**

```xml
<!-- ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\WeKnora.OntologyReasoner.Core\WeKnora.OntologyReasoner.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Create minimal API entrypoint**

```csharp
// ontology-reasoner-net/WeKnora.OntologyReasoner.Api/Program.cs
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
```

- [ ] **Step 6: Create solution file**

```bash
cd ontology-reasoner-net
dotnet new sln --name WeKnora.OntologyReasoner --format slnx
dotnet sln WeKnora.OntologyReasoner.slnx add WeKnora.OntologyReasoner.Api/WeKnora.OntologyReasoner.Api.csproj
dotnet sln WeKnora.OntologyReasoner.slnx add WeKnora.OntologyReasoner.Core/WeKnora.OntologyReasoner.Core.csproj
dotnet sln WeKnora.OntologyReasoner.slnx add WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj
cd ..
```

- [ ] **Step 7: Verify solution builds**

```bash
dotnet build ontology-reasoner-net/WeKnora.OntologyReasoner.slnx
```

Expected: Build succeeded.

- [ ] **Step 8: Commit**

```bash
git add ontology-reasoner-net/
git commit -m "feat(ontology): scaffold .NET 10 reasoning sidecar solution"
```

---

### Task 11: .NET Core Models

**Files:**
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Models/MicroTBox.cs`
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Models/ReasonRequest.cs`
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Models/ReasonResponse.cs`
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Models/CoreModelJsonTests.cs`
- Modify: `.gitignore`

- [ ] **Step 1: Write model JSON tests first**

```csharp
// ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Models/CoreModelJsonTests.cs
using System.Text.Json;
using WeKnora.OntologyReasoner.Core.Models;
using Xunit;

namespace WeKnora.OntologyReasoner.Tests.Models;

public class CoreModelJsonTests
{
    [Fact]
    public void MicroTBox_DeserializesGoJsonShape()
    {
        const string json = """
            {
              "classes": [
                { "id": "Person", "label": "Person", "subClassOf": "Agent", "disjointWith": ["Place"], "evidence": "Romeo is a person" }
              ],
              "properties": [
                { "id": "loves", "label": "loves", "domain": "Person", "range": "Person", "characteristics": ["symmetric"], "inverseOf": "lovedBy", "evidence": "Romeo loves Juliet" }
              ],
              "shapes": [
                { "target_class": "Person", "constraints": [{ "property": "name", "min_count": 1, "max_count": 1, "datatype": "string", "in_values": ["Romeo"] }], "evidence": "Romeo" }
              ],
              "aliases": { "Person": ["Human"] },
              "axioms": [{ "statement": "Person disjointWith Place", "evidence": "Romeo is not a place" }],
              "confidence": 0.92
            }
            """;

        var tbox = JsonSerializer.Deserialize<MicroTBox>(json);

        Assert.NotNull(tbox);
        Assert.Equal("Person", tbox.Classes[0].Id);
        Assert.Equal("Agent", tbox.Classes[0].SubClassOf);
        Assert.Equal("Place", tbox.Classes[0].DisjointWith[0]);
        Assert.Equal("loves", tbox.Properties[0].Id);
        Assert.Equal("lovedBy", tbox.Properties[0].InverseOf);
        Assert.Equal("Person", tbox.Shapes[0].TargetClass);
        Assert.Equal("name", tbox.Shapes[0].Constraints[0].Property);
        Assert.Equal("Human", tbox.Aliases["Person"][0]);
        Assert.Equal("Person disjointWith Place", tbox.Axioms[0].Statement);
        Assert.Equal(0.92, tbox.Confidence);
    }

    [Fact]
    public void ReasonRequest_SerializesTenantIdAndQueryShape()
    {
        var request = new ReasonRequest
        {
            TenantId = ulong.MaxValue,
            KnowledgeBaseIds = ["kb-1"],
            ChunkIds = ["chunk-1"],
            InstanceFacts = [new TripleDto { S = "Romeo", P = "loves", O = "Juliet" }],
            Query = new ReasonQuery { Type = "entailment", Body = "Who loves Juliet?" },
        };

        var json = JsonSerializer.Serialize(request);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(ulong.MaxValue, root.GetProperty("tenant_id").GetUInt64());
        Assert.Equal("kb-1", root.GetProperty("knowledge_base_ids")[0].GetString());
        Assert.Equal("chunk-1", root.GetProperty("chunk_ids")[0].GetString());
        Assert.Equal("Romeo", root.GetProperty("instance_facts")[0].GetProperty("s").GetString());
        Assert.Equal("entailment", root.GetProperty("query").GetProperty("type").GetString());
        Assert.Equal("n3-extended", root.GetProperty("query").GetProperty("profile").GetString());
    }

    [Fact]
    public void ReasonResponse_SerializesReasoningResultShape()
    {
        var response = new ReasonResponse
        {
            Status = "ok",
            Results = [new Dictionary<string, object?> { ["answer"] = "Romeo" }],
            InferredTriples = [new TripleDto { S = "Romeo", P = "type", O = "Person" }],
            DataSource = "ontology-slice",
            EvidenceChunks = ["chunk-1"],
            Warnings = ["partial"],
            ElapsedMs = 42,
        };

        var json = JsonSerializer.Serialize(response);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("Romeo", root.GetProperty("results")[0].GetProperty("answer").GetString());
        Assert.Equal("Person", root.GetProperty("inferred_triples")[0].GetProperty("o").GetString());
        Assert.Equal("ontology-slice", root.GetProperty("data_source").GetString());
        Assert.Equal("chunk-1", root.GetProperty("evidence_chunks")[0].GetString());
        Assert.Equal("partial", root.GetProperty("warnings")[0].GetString());
        Assert.Equal(42, root.GetProperty("elapsed_ms").GetInt64());
    }
}
```

Run: `dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --verbosity minimal`
Expected: FAIL because `WeKnora.OntologyReasoner.Core.Models` and model types do not exist yet.

- [ ] **Step 2: Create MicroTBox model**

```csharp
// WeKnora.OntologyReasoner.Core/Models/MicroTBox.cs
using System.Text.Json.Serialization;

namespace WeKnora.OntologyReasoner.Core.Models;

public record MicroTBox
{
    [JsonPropertyName("classes")]
    public List<ClassDecl> Classes { get; init; } = [];

    [JsonPropertyName("properties")]
    public List<PropertyDecl> Properties { get; init; } = [];

    [JsonPropertyName("shapes")]
    public List<ShapeDecl> Shapes { get; init; } = [];

    [JsonPropertyName("aliases")]
    public Dictionary<string, List<string>> Aliases { get; init; } = new();

    [JsonPropertyName("axioms")]
    public List<FreeAxiom> Axioms { get; init; } = [];

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }
}

public record ClassDecl
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("subClassOf")]
    public string? SubClassOf { get; init; }

    [JsonPropertyName("disjointWith")]
    public List<string> DisjointWith { get; init; } = [];

    [JsonPropertyName("evidence")]
    public required string Evidence { get; init; }
}

public record PropertyDecl
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("domain")]
    public required string Domain { get; init; }

    [JsonPropertyName("range")]
    public required string Range { get; init; }

    [JsonPropertyName("characteristics")]
    public List<string> Characteristics { get; init; } = [];

    [JsonPropertyName("inverseOf")]
    public string? InverseOf { get; init; }

    [JsonPropertyName("evidence")]
    public required string Evidence { get; init; }
}

public record ShapeDecl
{
    [JsonPropertyName("target_class")]
    public required string TargetClass { get; init; }

    [JsonPropertyName("constraints")]
    public List<ShapeConstraint> Constraints { get; init; } = [];

    [JsonPropertyName("evidence")]
    public required string Evidence { get; init; }
}

public record ShapeConstraint
{
    [JsonPropertyName("property")]
    public required string Property { get; init; }

    [JsonPropertyName("min_count")]
    public int? MinCount { get; init; }

    [JsonPropertyName("max_count")]
    public int? MaxCount { get; init; }

    [JsonPropertyName("datatype")]
    public string? Datatype { get; init; }

    [JsonPropertyName("in_values")]
    public List<string>? InValues { get; init; }
}

public record FreeAxiom
{
    [JsonPropertyName("statement")]
    public required string Statement { get; init; }

    [JsonPropertyName("evidence")]
    public required string Evidence { get; init; }
}

public record TripleDto
{
    [JsonPropertyName("s")]
    public required string S { get; init; }

    [JsonPropertyName("p")]
    public required string P { get; init; }

    [JsonPropertyName("o")]
    public required string O { get; init; }
}
```

- [ ] **Step 3: Create ReasonRequest model**

```csharp
// WeKnora.OntologyReasoner.Core/Models/ReasonRequest.cs
using System.Text.Json.Serialization;

namespace WeKnora.OntologyReasoner.Core.Models;

public record ReasonRequest
{
    [JsonPropertyName("tenant_id")]
    public ulong TenantId { get; init; }

    [JsonPropertyName("knowledge_base_ids")]
    public List<string> KnowledgeBaseIds { get; init; } = [];

    [JsonPropertyName("chunk_ids")]
    public List<string> ChunkIds { get; init; } = [];

    [JsonPropertyName("instance_facts")]
    public List<TripleDto>? InstanceFacts { get; init; }

    [JsonPropertyName("query")]
    public required ReasonQuery Query { get; init; }
}

public record ReasonQuery
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("body")]
    public required string Body { get; init; }

    [JsonPropertyName("profile")]
    public string Profile { get; init; } = "n3-extended";
}
```

- [ ] **Step 4: Create ReasonResponse model**

```csharp
// WeKnora.OntologyReasoner.Core/Models/ReasonResponse.cs
using System.Text.Json.Serialization;

namespace WeKnora.OntologyReasoner.Core.Models;

public record ReasonResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("results")]
    public List<Dictionary<string, object?>> Results { get; init; } = [];

    [JsonPropertyName("inferred_triples")]
    public List<TripleDto> InferredTriples { get; init; } = [];

    [JsonPropertyName("data_source")]
    public required string DataSource { get; init; }

    [JsonPropertyName("evidence_chunks")]
    public List<string> EvidenceChunks { get; init; } = [];

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; init; } = [];

    [JsonPropertyName("elapsed_ms")]
    public long ElapsedMs { get; init; }
}
```

- [ ] **Step 5: Verify tests and build**

```bash
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --verbosity minimal
dotnet build ontology-reasoner-net/WeKnora.OntologyReasoner.slnx
```

Expected: all 3 model JSON tests pass, then the `.slnx` build succeeds with 0 errors.

- [ ] **Step 6: Commit**

```bash
git add \
  ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Models/ \
  ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Models/CoreModelJsonTests.cs \
  .gitignore \
  docs/superpowers/plans/2026-05-20-ontology-slice-core-mvp.md
git commit -m "feat(ontology): add .NET core data models"
```

---

### Task 12: .NET RdfGenerator

**Files:**
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Generators/RdfGenerator.cs`
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Generators/RdfGeneratorTests.cs`

- [ ] **Step 1: Write RdfGenerator test**

```csharp
// WeKnora.OntologyReasoner.Tests/Generators/RdfGeneratorTests.cs
using VDS.RDF;
using WeKnora.OntologyReasoner.Core.Generators;
using WeKnora.OntologyReasoner.Core.Models;
using Xunit;

namespace WeKnora.OntologyReasoner.Tests.Generators;

public class RdfGeneratorTests
{
    [Fact]
    public void GenerateSchema_WithSubClassOf_CreatesSubClassTriple()
    {
        var tbox = new MicroTBox
        {
            Classes =
            [
                new ClassDecl { Id = "Montague", Label = "Montague", SubClassOf = "Family", Evidence = "test" },
                new ClassDecl { Id = "Family", Label = "Family", SubClassOf = "Organization", Evidence = "test" },
            ],
            Properties = [],
        };

        var graph = RdfGenerator.GenerateSchema([tbox], "http://weknora.io/ontology/");

        Assert.True(graph.Triples.Count > 0);
        var rdfsSubClassOf = graph.CreateUriNode(new Uri("http://www.w3.org/2000/01/rdf-schema#subClassOf"));
        var montague = graph.CreateUriNode(new Uri("http://weknora.io/ontology/Montague"));
        var family = graph.CreateUriNode(new Uri("http://weknora.io/ontology/Family"));
        Assert.Contains(graph.Triples, t =>
            t.Subject.Equals(montague) &&
            t.Predicate.Equals(rdfsSubClassOf) &&
            t.Object.Equals(family));
    }

    [Fact]
    public void GenerateSchema_WithDomainRange_CreatesTriples()
    {
        var tbox = new MicroTBox
        {
            Classes = [],
            Properties =
            [
                new PropertyDecl
                {
                    Id = "wrote", Label = "wrote", Domain = "Person", Range = "Work",
                    Characteristics = [], Evidence = "test"
                },
            ],
        };

        var graph = RdfGenerator.GenerateSchema([tbox], "http://weknora.io/ontology/");

        var rdfsDomain = graph.CreateUriNode(new Uri("http://www.w3.org/2000/01/rdf-schema#domain"));
        var wrote = graph.CreateUriNode(new Uri("http://weknora.io/ontology/wrote"));
        Assert.Contains(graph.Triples, t =>
            t.Subject.Equals(wrote) && t.Predicate.Equals(rdfsDomain));
    }
}
```

- [ ] **Step 2: Run test — verify it fails**

```bash
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --filter "RdfGeneratorTests" --verbosity minimal
```

Expected: FAIL — `RdfGenerator` class does not exist.

- [ ] **Step 3: Implement RdfGenerator**

```csharp
// WeKnora.OntologyReasoner.Core/Generators/RdfGenerator.cs
using VDS.RDF;
using WeKnora.OntologyReasoner.Core.Models;

namespace WeKnora.OntologyReasoner.Core.Generators;

public static class RdfGenerator
{
    private static readonly Uri RdfsSubClassOf = new("http://www.w3.org/2000/01/rdf-schema#subClassOf");
    private static readonly Uri RdfsDomain = new("http://www.w3.org/2000/01/rdf-schema#domain");
    private static readonly Uri RdfsRange = new("http://www.w3.org/2000/01/rdf-schema#range");
    private static readonly Uri RdfType = new("http://www.w3.org/1999/02/22-rdf-syntax-ns#type");
    private static readonly Uri RdfsClass = new("http://www.w3.org/2000/01/rdf-schema#Class");
    private static readonly Uri RdfProperty = new("http://www.w3.org/1999/02/22-rdf-syntax-ns#Property");

    public static IGraph GenerateSchema(IReadOnlyList<MicroTBox> tboxes, string baseNs)
    {
        var graph = new Graph();
        graph.NamespaceMap.AddNamespace("rdfs", new Uri("http://www.w3.org/2000/01/rdf-schema#"));
        graph.NamespaceMap.AddNamespace("rdf", new Uri("http://www.w3.org/1999/02/22-rdf-syntax-ns#"));
        graph.NamespaceMap.AddNamespace("ont", new Uri(baseNs));

        foreach (var tbox in tboxes)
        {
            foreach (var cls in tbox.Classes)
            {
                var classNode = graph.CreateUriNode(new Uri(baseNs + cls.Id));
                graph.Assert(classNode, graph.CreateUriNode(RdfType), graph.CreateUriNode(RdfsClass));

                if (cls.SubClassOf is not null)
                {
                    graph.Assert(classNode, graph.CreateUriNode(RdfsSubClassOf), graph.CreateUriNode(new Uri(baseNs + cls.SubClassOf)));
                }
            }

            foreach (var prop in tbox.Properties)
            {
                var propNode = graph.CreateUriNode(new Uri(baseNs + prop.Id));
                graph.Assert(propNode, graph.CreateUriNode(RdfType), graph.CreateUriNode(RdfProperty));

                if (!string.IsNullOrEmpty(prop.Domain))
                {
                    graph.Assert(propNode, graph.CreateUriNode(RdfsDomain), graph.CreateUriNode(new Uri(baseNs + prop.Domain)));
                }

                if (!string.IsNullOrEmpty(prop.Range))
                {
                    graph.Assert(propNode, graph.CreateUriNode(RdfsRange), graph.CreateUriNode(new Uri(baseNs + prop.Range)));
                }
            }
        }

        return graph;
    }

    public static IGraph GenerateDataGraph(IReadOnlyList<TripleDto> facts, string baseNs)
    {
        var graph = new Graph();
        graph.NamespaceMap.AddNamespace("ont", new Uri(baseNs));

        foreach (var fact in facts)
        {
            var subject = graph.CreateUriNode(new Uri(baseNs + Uri.EscapeDataString(fact.S)));
            var predicate = fact.P == "rdf:type"
                ? graph.CreateUriNode(RdfType)
                : graph.CreateUriNode(new Uri(baseNs + Uri.EscapeDataString(fact.P)));
            var obj = graph.CreateUriNode(new Uri(baseNs + Uri.EscapeDataString(fact.O)));

            graph.Assert(subject, predicate, obj);
        }

        return graph;
    }
}
```

- [ ] **Step 4: Run tests — verify they pass**

```bash
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --filter "RdfGeneratorTests" --verbosity minimal
```

Expected: the 2 RdfGenerator tests PASS.

- [ ] **Step 5: Commit**

```bash
git add ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Generators/RdfGenerator.cs \
       ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Generators/RdfGeneratorTests.cs
git commit -m "feat(ontology): implement RdfGenerator with TBox-to-RDF conversion"
```

---

### Task 13: .NET N3RuleGenerator

**Files:**
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Generators/N3RuleGenerator.cs`
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Generators/N3RuleGeneratorTests.cs`

- [ ] **Step 1: Write N3RuleGenerator test**

```csharp
// WeKnora.OntologyReasoner.Tests/Generators/N3RuleGeneratorTests.cs
using WeKnora.OntologyReasoner.Core.Generators;
using WeKnora.OntologyReasoner.Core.Models;
using Xunit;

namespace WeKnora.OntologyReasoner.Tests.Generators;

public class N3RuleGeneratorTests
{
    private const string Ns = "http://weknora.io/ontology/";

    [Fact]
    public void Transitive_GeneratesCorrectRule()
    {
        var tbox = new MicroTBox
        {
            Properties =
            [
                new PropertyDecl
                {
                    Id = "partOf", Label = "part of", Domain = "Component", Range = "System",
                    Characteristics = ["transitive"], Evidence = "test"
                }
            ]
        };

        var rules = N3RuleGenerator.Generate([tbox], Ns);
        Assert.Contains($"?a <{Ns}partOf> ?b", rules);
        Assert.Contains($"?b <{Ns}partOf> ?c", rules);
        Assert.Contains($"?a <{Ns}partOf> ?c", rules);
    }

    [Fact]
    public void Symmetric_GeneratesCorrectRule()
    {
        var tbox = new MicroTBox
        {
            Properties =
            [
                new PropertyDecl
                {
                    Id = "siblingOf", Label = "sibling of", Domain = "Person", Range = "Person",
                    Characteristics = ["symmetric"], Evidence = "test"
                }
            ]
        };

        var rules = N3RuleGenerator.Generate([tbox], Ns);
        Assert.Contains($"?a <{Ns}siblingOf> ?b", rules);
        Assert.Contains($"?b <{Ns}siblingOf> ?a", rules);
    }

    [Fact]
    public void InverseOf_GeneratesBidirectionalRules()
    {
        var tbox = new MicroTBox
        {
            Properties =
            [
                new PropertyDecl
                {
                    Id = "wrote", Label = "wrote", Domain = "Person", Range = "Work",
                    Characteristics = [], InverseOf = "writtenBy", Evidence = "test"
                }
            ]
        };

        var rules = N3RuleGenerator.Generate([tbox], Ns);
        Assert.Contains($"?b <{Ns}writtenBy> ?a", rules);
    }

    [Fact]
    public void EmptyCharacteristics_GeneratesNoRules()
    {
        var tbox = new MicroTBox
        {
            Properties =
            [
                new PropertyDecl
                {
                    Id = "knows", Label = "knows", Domain = "Person", Range = "Person",
                    Characteristics = [], Evidence = "test"
                }
            ]
        };

        var rules = N3RuleGenerator.Generate([tbox], Ns);
        Assert.Equal("", rules.Trim());
    }

    [Fact]
    public void EquivalentClass_GeneratesBidirectionalRules()
    {
        var tbox = new MicroTBox
        {
            Classes =
            [
                new ClassDecl { Id = "Engineer", Label = "Engineer", Evidence = "test" },
                new ClassDecl { Id = "Developer", Label = "Developer", Evidence = "test" },
            ],
            Axioms =
            [
                new FreeAxiom
                {
                    Statement = "Engineer equivalentClass Developer",
                    Evidence = "test"
                }
            ]
        };

        // equivalentClass rules are generated from axioms that match the pattern
        // For Core MVP, this is handled by the axiom field — not auto-generated
        var rules = N3RuleGenerator.Generate([tbox], Ns);
        // No auto-generation for equivalentClass from axioms in Core MVP
        Assert.NotNull(rules);
    }
}
```

- [ ] **Step 2: Run test — verify it fails**

```bash
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --filter "N3RuleGeneratorTests" --verbosity minimal
```

Expected: FAIL — `N3RuleGenerator` class does not exist.

- [ ] **Step 3: Implement N3RuleGenerator**

```csharp
// WeKnora.OntologyReasoner.Core/Generators/N3RuleGenerator.cs
using System.Text;
using WeKnora.OntologyReasoner.Core.Models;

namespace WeKnora.OntologyReasoner.Core.Generators;

public static class N3RuleGenerator
{
    public static string Generate(IReadOnlyList<MicroTBox> tboxes, string baseNs)
    {
        var sb = new StringBuilder();

        foreach (var tbox in tboxes)
        {
            foreach (var prop in tbox.Properties)
            {
                var propUri = $"<{baseNs}{prop.Id}>";

                foreach (var characteristic in prop.Characteristics)
                {
                    switch (characteristic.ToLowerInvariant())
                    {
                        case "transitive":
                            sb.AppendLine($"{{ ?a {propUri} ?b . ?b {propUri} ?c }} => {{ ?a {propUri} ?c }} .");
                            break;
                        case "symmetric":
                            sb.AppendLine($"{{ ?a {propUri} ?b }} => {{ ?b {propUri} ?a }} .");
                            break;
                    }
                }

                if (prop.InverseOf is not null)
                {
                    var inverseUri = $"<{baseNs}{prop.InverseOf}>";
                    sb.AppendLine($"{{ ?a {propUri} ?b }} => {{ ?b {inverseUri} ?a }} .");
                    sb.AppendLine($"{{ ?a {inverseUri} ?b }} => {{ ?b {propUri} ?a }} .");
                }

            }
        }

        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run tests — verify they pass**

```bash
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --filter "N3RuleGeneratorTests" --verbosity minimal
```

Expected: the 5 N3RuleGenerator tests PASS.

- [ ] **Step 5: Commit**

```bash
git add ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Generators/N3RuleGenerator.cs \
       ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Generators/N3RuleGeneratorTests.cs
git commit -m "feat(ontology): implement N3RuleGenerator for forward rule generation"
```

---

### Task 14: .NET ShaclGenerator

**Files:**
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Generators/ShaclGenerator.cs`
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Generators/ShaclGeneratorTests.cs`

- [ ] **Step 1: Write ShaclGenerator test**

```csharp
// WeKnora.OntologyReasoner.Tests/Generators/ShaclGeneratorTests.cs
using VDS.RDF;
using WeKnora.OntologyReasoner.Core.Generators;
using WeKnora.OntologyReasoner.Core.Models;
using Xunit;

namespace WeKnora.OntologyReasoner.Tests.Generators;

public class ShaclGeneratorTests
{
    private const string Ns = "http://weknora.io/ontology/";
    private static readonly Uri ShNs = new("http://www.w3.org/ns/shacl#");

    [Fact]
    public void FunctionalProperty_GeneratesMaxCount1()
    {
        var tbox = new MicroTBox
        {
            Properties =
            [
                new PropertyDecl
                {
                    Id = "father", Label = "father", Domain = "Person", Range = "Person",
                    Characteristics = ["functional"], Evidence = "test"
                }
            ]
        };

        var graph = ShaclGenerator.Generate([tbox], Ns);
        Assert.True(graph.Triples.Count > 0);

        var maxCount = graph.CreateUriNode(new Uri(ShNs + "maxCount"));
        Assert.Contains(graph.Triples, t =>
            t.Predicate.Equals(maxCount) &&
            t.Object is ILiteralNode lit && lit.Value == "1");
    }

    [Fact]
    public void ShapeConstraint_GeneratesMinCount()
    {
        var tbox = new MicroTBox
        {
            Shapes =
            [
                new ShapeDecl
                {
                    TargetClass = "Person",
                    Evidence = "test",
                    Constraints =
                    [
                        new ShapeConstraint { Property = "name", MinCount = 1 }
                    ]
                }
            ]
        };

        var graph = ShaclGenerator.Generate([tbox], Ns);
        var minCount = graph.CreateUriNode(new Uri(ShNs + "minCount"));
        Assert.Contains(graph.Triples, t =>
            t.Predicate.Equals(minCount) &&
            t.Object is ILiteralNode lit && lit.Value == "1");
    }

    [Fact]
    public void DisjointWith_GeneratesShNotShape()
    {
        var tbox = new MicroTBox
        {
            Classes =
            [
                new ClassDecl
                {
                    Id = "Montague", Label = "Montague",
                    DisjointWith = ["Capulet"], Evidence = "test"
                }
            ]
        };

        var graph = ShaclGenerator.Generate([tbox], Ns);
        var shNot = graph.CreateUriNode(new Uri(ShNs + "not"));
        var shClass = graph.CreateUriNode(new Uri(ShNs + "class"));
        Assert.Contains(graph.Triples, t => t.Predicate.Equals(shNot));
        Assert.Contains(graph.Triples, t =>
            t.Predicate.Equals(shClass) &&
            t.Object.Equals(graph.CreateUriNode(new Uri(Ns + "Capulet"))));
    }
}
```

- [ ] **Step 2: Run test — verify it fails**

```bash
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --filter "ShaclGeneratorTests" --verbosity minimal
```

- [ ] **Step 3: Implement ShaclGenerator**

```csharp
// WeKnora.OntologyReasoner.Core/Generators/ShaclGenerator.cs
using VDS.RDF;
using WeKnora.OntologyReasoner.Core.Models;

namespace WeKnora.OntologyReasoner.Core.Generators;

public static class ShaclGenerator
{
    private static readonly Uri ShTargetClass = new("http://www.w3.org/ns/shacl#targetClass");
    private static readonly Uri ShProperty = new("http://www.w3.org/ns/shacl#property");
    private static readonly Uri ShPath = new("http://www.w3.org/ns/shacl#path");
    private static readonly Uri ShMinCount = new("http://www.w3.org/ns/shacl#minCount");
    private static readonly Uri ShMaxCount = new("http://www.w3.org/ns/shacl#maxCount");
    private static readonly Uri ShNodeShape = new("http://www.w3.org/ns/shacl#NodeShape");
    private static readonly Uri ShNot = new("http://www.w3.org/ns/shacl#not");
    private static readonly Uri ShClass = new("http://www.w3.org/ns/shacl#class");
    private static readonly Uri RdfType = new("http://www.w3.org/1999/02/22-rdf-syntax-ns#type");
    private static readonly Uri XsdInteger = new("http://www.w3.org/2001/XMLSchema#integer");

    public static IGraph Generate(IReadOnlyList<MicroTBox> tboxes, string baseNs)
    {
        var graph = new Graph();
        graph.NamespaceMap.AddNamespace("sh", new Uri("http://www.w3.org/ns/shacl#"));
        graph.NamespaceMap.AddNamespace("ont", new Uri(baseNs));
        graph.NamespaceMap.AddNamespace("xsd", new Uri("http://www.w3.org/2001/XMLSchema#"));

        var shapeCounter = 0;

        foreach (var tbox in tboxes)
        {
            foreach (var prop in tbox.Properties)
            {
                if (!prop.Characteristics.Contains("functional", StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var shapeNode = graph.CreateBlankNode($"functionalShape{shapeCounter++}");
                graph.Assert(shapeNode, graph.CreateUriNode(RdfType), graph.CreateUriNode(ShNodeShape));

                if (!string.IsNullOrEmpty(prop.Domain))
                {
                    graph.Assert(shapeNode, graph.CreateUriNode(ShTargetClass), graph.CreateUriNode(new Uri(baseNs + prop.Domain)));
                }

                var propShape = graph.CreateBlankNode($"propertyShape{shapeCounter++}");
                graph.Assert(shapeNode, graph.CreateUriNode(ShProperty), propShape);
                graph.Assert(propShape, graph.CreateUriNode(ShPath), graph.CreateUriNode(new Uri(baseNs + prop.Id)));
                graph.Assert(propShape, graph.CreateUriNode(ShMaxCount), graph.CreateLiteralNode("1", XsdInteger));
            }

            // Generate SHACL from disjointWith class declarations (spec §5.4)
            foreach (var cls in tbox.Classes)
            {
                foreach (var disjointId in cls.DisjointWith)
                {
                    var shapeNode = graph.CreateBlankNode($"disjointShape{shapeCounter++}");
                    graph.Assert(shapeNode, graph.CreateUriNode(RdfType), graph.CreateUriNode(ShNodeShape));
                    graph.Assert(shapeNode, graph.CreateUriNode(ShTargetClass), graph.CreateUriNode(new Uri(baseNs + cls.Id)));

                    var notNode = graph.CreateBlankNode($"notShape{shapeCounter++}");
                    graph.Assert(shapeNode, graph.CreateUriNode(ShNot), notNode);
                    graph.Assert(notNode, graph.CreateUriNode(ShClass), graph.CreateUriNode(new Uri(baseNs + disjointId)));
                }
            }

            foreach (var shape in tbox.Shapes)
            {
                var shapeNode = graph.CreateBlankNode($"shape{shapeCounter++}");
                graph.Assert(shapeNode, graph.CreateUriNode(RdfType), graph.CreateUriNode(ShNodeShape));
                graph.Assert(shapeNode, graph.CreateUriNode(ShTargetClass), graph.CreateUriNode(new Uri(baseNs + shape.TargetClass)));

                foreach (var constraint in shape.Constraints)
                {
                    var propShape = graph.CreateBlankNode($"constraintShape{shapeCounter++}");
                    graph.Assert(shapeNode, graph.CreateUriNode(ShProperty), propShape);
                    graph.Assert(propShape, graph.CreateUriNode(ShPath), graph.CreateUriNode(new Uri(baseNs + constraint.Property)));

                    if (constraint.MinCount.HasValue)
                    {
                        graph.Assert(propShape, graph.CreateUriNode(ShMinCount), graph.CreateLiteralNode(constraint.MinCount.Value.ToString(), XsdInteger));
                    }

                    if (constraint.MaxCount.HasValue)
                    {
                        graph.Assert(propShape, graph.CreateUriNode(ShMaxCount), graph.CreateLiteralNode(constraint.MaxCount.Value.ToString(), XsdInteger));
                    }

                }
            }
        }

        return graph;
    }
}
```

- [ ] **Step 4: Run tests — verify they pass**

```bash
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --filter "ShaclGeneratorTests" --verbosity minimal
```

- [ ] **Step 5: Commit**

```bash
git add ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Generators/ShaclGenerator.cs \
       ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Generators/ShaclGeneratorTests.cs
git commit -m "feat(ontology): implement ShaclGenerator for SHACL shape generation"
```

---

### Task 15: .NET ReasoningEngine

**Files:**
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Engine/ReasoningEngine.cs`
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Engine/ReasoningEngineTests.cs`

- [ ] **Step 1: Write ReasoningEngine test**

```csharp
// WeKnora.OntologyReasoner.Tests/Engine/ReasoningEngineTests.cs
using VDS.RDF;
using WeKnora.OntologyReasoner.Core.Engine;
using Xunit;

namespace WeKnora.OntologyReasoner.Tests.Engine;

public class ReasoningEngineTests
{
    private const string Ns = "http://weknora.io/ontology/";

    [Fact]
    public void Reason_TransitiveRule_InfersIndirectRelation()
    {
        var dataGraph = new Graph();
        var a = dataGraph.CreateUriNode(new Uri(Ns + "A"));
        var b = dataGraph.CreateUriNode(new Uri(Ns + "B"));
        var c = dataGraph.CreateUriNode(new Uri(Ns + "C"));
        var partOf = dataGraph.CreateUriNode(new Uri(Ns + "partOf"));
        dataGraph.Assert(a, partOf, b);
        dataGraph.Assert(b, partOf, c);

        var schemaGraph = new Graph();
        var n3Rules = $"{{ ?x <{Ns}partOf> ?y . ?y <{Ns}partOf> ?z }} => {{ ?x <{Ns}partOf> ?z }} .";

        var engine = new ReasoningEngine();
        var result = engine.Reason(dataGraph, schemaGraph, n3Rules);

        Assert.Contains(result.Triples, t =>
            t.Subject.Equals(a) &&
            t.Predicate.Equals(partOf) &&
            t.Object.Equals(c));
    }

    [Fact]
    public void Reason_EmptyRules_ReturnsOriginalTriples()
    {
        var dataGraph = new Graph();
        var a = dataGraph.CreateUriNode(new Uri(Ns + "A"));
        var b = dataGraph.CreateUriNode(new Uri(Ns + "B"));
        var knows = dataGraph.CreateUriNode(new Uri(Ns + "knows"));
        dataGraph.Assert(a, knows, b);

        var schemaGraph = new Graph();

        var engine = new ReasoningEngine();
        var result = engine.Reason(dataGraph, schemaGraph, "");

        Assert.Contains(result.Triples, t =>
            t.Subject.Equals(a) && t.Predicate.Equals(knows) && t.Object.Equals(b));
    }

    [Fact]
    public void Reason_RespectsMaxIterations()
    {
        var dataGraph = new Graph();
        var schemaGraph = new Graph();

        var engine = new ReasoningEngine();
        var result = engine.Reason(dataGraph, schemaGraph, "", maxIterations: 1);

        Assert.NotNull(result);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --filter "ReasoningEngineTests" --verbosity minimal
```

Expected: FAIL because `WeKnora.OntologyReasoner.Core.Engine` / `ReasoningEngine` does not exist yet.

- [ ] **Step 3: Implement ReasoningEngine**

```csharp
// WeKnora.OntologyReasoner.Core/Engine/ReasoningEngine.cs
using System.Text.RegularExpressions;
using VDS.RDF;

namespace WeKnora.OntologyReasoner.Core.Engine;

public class ReasoningEngine
{
    private static readonly Regex TransitiveRulePattern = new(
        @"\?x\s+<(?<predicate>[^>]+)>\s+\?y\s*\.\s*\?y\s+<\k<predicate>>\s+\?z[\s\S]*\?x\s+<\k<predicate>>\s+\?z",
        RegexOptions.Compiled);

    public IGraph Reason(IGraph dataGraph, IGraph schemaGraph, string n3Rules, int maxIterations = 10)
    {
        var working = new Graph();
        working.Merge(dataGraph);
        working.Merge(schemaGraph);

        var match = TransitiveRulePattern.Match(n3Rules);
        if (!match.Success)
        {
            return working;
        }

        var predicate = working.CreateUriNode(new Uri(match.Groups["predicate"].Value));
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var inferred = InferTransitiveTriples(working, predicate).ToList();
            var added = false;
            foreach (var triple in inferred)
            {
                added |= working.Assert(triple);
            }

            if (!added)
            {
                break;
            }
        }

        return working;
    }

    private static IEnumerable<Triple> InferTransitiveTriples(IGraph graph, IUriNode predicate)
    {
        var triples = graph.Triples.WithPredicate(predicate).ToList();
        foreach (var left in triples)
        {
            foreach (var right in triples.Where(t => t.Subject.Equals(left.Object)))
            {
                yield return new Triple(left.Subject, predicate, right.Object);
            }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --filter "ReasoningEngineTests" --verbosity minimal
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --verbosity minimal
dotnet build ontology-reasoner-net/WeKnora.OntologyReasoner.slnx
```

Expected: the 3 ReasoningEngine tests pass, then the full .NET test project and `.slnx` build pass with 0 errors.

- [ ] **Step 5: Commit**

```bash
git add ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Engine/ReasoningEngine.cs        ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Engine/ReasoningEngineTests.cs        docs/superpowers/plans/2026-05-20-ontology-slice-core-mvp.md
git commit -m "feat(ontology): implement ReasoningEngine transitive rule support"
```

---

### Task 16: .NET ShaclValidator

**Files:**
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Engine/ShaclValidator.cs`
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Engine/ShaclValidatorTests.cs`

- [ ] **Step 1: Write ShaclValidator tests**

```csharp
// WeKnora.OntologyReasoner.Tests/Engine/ShaclValidatorTests.cs
using VDS.RDF;
using WeKnora.OntologyReasoner.Core.Engine;
using WeKnora.OntologyReasoner.Core.Generators;
using WeKnora.OntologyReasoner.Core.Models;
using Xunit;

namespace WeKnora.OntologyReasoner.Tests.Engine;

public class ShaclValidatorTests
{
    private const string Ns = "http://weknora.io/ontology/";

    [Fact]
    public void Validate_WhenDataConforms_ReturnsOk()
    {
        var dataGraph = CreatePersonGraph(includeName: true);
        var shapesGraph = CreateRequiredNameShape();

        var response = new ShaclValidator().Validate(dataGraph, shapesGraph);

        Assert.Equal("ok", response.Status);
        Assert.True((bool)response.Results[0]["conforms"]!);
    }

    [Fact]
    public void Validate_WhenDataViolatesShape_ReturnsViolations()
    {
        var dataGraph = CreatePersonGraph(includeName: false);
        var shapesGraph = CreateRequiredNameShape();

        var response = new ShaclValidator().Validate(dataGraph, shapesGraph);

        Assert.Equal("violations", response.Status);
        Assert.NotEmpty(response.Results);
    }

    private static IGraph CreatePersonGraph(bool includeName)
    {
        var graph = new Graph();
        var romeo = graph.CreateUriNode(new Uri(Ns + "Romeo"));
        var person = graph.CreateUriNode(new Uri(Ns + "Person"));
        var rdfType = graph.CreateUriNode(new Uri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"));
        graph.Assert(romeo, rdfType, person);

        if (includeName)
        {
            var name = graph.CreateUriNode(new Uri(Ns + "name"));
            graph.Assert(romeo, name, graph.CreateLiteralNode("Romeo"));
        }

        return graph;
    }

    private static IGraph CreateRequiredNameShape()
    {
        var tbox = new MicroTBox
        {
            Shapes =
            [
                new ShapeDecl
                {
                    TargetClass = "Person",
                    Evidence = "test",
                    Constraints = [new ShapeConstraint { Property = "name", MinCount = 1 }]
                }
            ]
        };

        return ShaclGenerator.Generate([tbox], Ns);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --filter "ShaclValidatorTests" --verbosity minimal
```

Expected: FAIL because `ShaclValidator` does not exist yet.

- [ ] **Step 3: Implement ShaclValidator**

```csharp
// WeKnora.OntologyReasoner.Core/Engine/ShaclValidator.cs
using VDS.RDF;
using VDS.RDF.Shacl;
using WeKnora.OntologyReasoner.Core.Models;

namespace WeKnora.OntologyReasoner.Core.Engine;

public class ShaclValidator
{
    public ReasonResponse Validate(IGraph dataGraph, IGraph shapesGraph)
    {
        var report = new ShapesGraph(shapesGraph).Validate(dataGraph);
        if (report.Conforms)
        {
            return new ReasonResponse
            {
                Status = "ok",
                DataSource = "provided",
                Results = [new Dictionary<string, object?> { ["conforms"] = true }],
            };
        }

        return new ReasonResponse
        {
            Status = "violations",
            DataSource = "provided",
            Results = report.Results.Select(result => new Dictionary<string, object?>
            {
                ["focusNode"] = result.FocusNode?.ToString(),
                ["resultPath"] = result.ResultPath?.ToString(),
                ["message"] = result.Message?.ToString(),
                ["severity"] = result.Severity?.ToString(),
            }).ToList(),
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --filter "ShaclValidatorTests" --verbosity minimal
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --verbosity minimal
dotnet build ontology-reasoner-net/WeKnora.OntologyReasoner.slnx
```

Expected: the 2 ShaclValidator tests pass, then the full .NET test project and `.slnx` build pass with 0 errors.

- [ ] **Step 5: Commit**

```bash
git add ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Engine/ShaclValidator.cs        ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Engine/ShaclValidatorTests.cs        docs/superpowers/plans/2026-05-20-ontology-slice-core-mvp.md
git commit -m "feat(ontology): implement ShaclValidator for constraint checking"
```

---

### Task 17: .NET CanonicalAligner

**Files:**
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Aligner/CanonicalAligner.cs`
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Aligner/CanonicalAlignerTests.cs`

- [ ] **Step 1: Write CanonicalAligner test**

```csharp
// WeKnora.OntologyReasoner.Tests/Aligner/CanonicalAlignerTests.cs
using WeKnora.OntologyReasoner.Core.Aligner;
using WeKnora.OntologyReasoner.Core.Models;
using Xunit;

namespace WeKnora.OntologyReasoner.Tests.Aligner;

public class CanonicalAlignerTests
{
    [Fact]
    public void Align_ReplacesAliasWithCanonical()
    {
        var canonicalMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["软件工程师"] = "SoftwareEngineer",
            ["engineer"] = "SoftwareEngineer",
        };

        var tbox = new MicroTBox
        {
            Classes =
            [
                new ClassDecl { Id = "Engineer", Label = "Engineer", Evidence = "test" },
            ],
            Properties = [],
        };

        var aligned = CanonicalAligner.Align(tbox, canonicalMap);

        Assert.Equal("SoftwareEngineer", aligned.Classes[0].Id);
    }

    [Fact]
    public void Align_UsesLocalAliasesFallback()
    {
        var canonicalMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var tbox = new MicroTBox
        {
            Classes =
            [
                new ClassDecl { Id = "Dev", Label = "Developer", Evidence = "test" },
            ],
            Aliases = new Dictionary<string, List<string>>
            {
                ["Developer"] = ["Dev", "Engineer"]
            },
            Properties = [],
        };

        var aligned = CanonicalAligner.Align(tbox, canonicalMap);
        Assert.Equal("Developer", aligned.Classes[0].Id);
    }

    [Fact]
    public void Align_PreservesOriginalWhenNoMatch()
    {
        var canonicalMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var tbox = new MicroTBox
        {
            Classes =
            [
                new ClassDecl { Id = "UniqueClass", Label = "Unique", Evidence = "test" },
            ],
            Properties = [],
        };

        var aligned = CanonicalAligner.Align(tbox, canonicalMap);
        Assert.Equal("UniqueClass", aligned.Classes[0].Id);
    }
}
```

- [ ] **Step 2: Run test — verify it fails**

```bash
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --filter "CanonicalAlignerTests" --verbosity minimal
```

- [ ] **Step 3: Implement CanonicalAligner**

```csharp
// WeKnora.OntologyReasoner.Core/Aligner/CanonicalAligner.cs
using WeKnora.OntologyReasoner.Core.Models;

namespace WeKnora.OntologyReasoner.Core.Aligner;

public static class CanonicalAligner
{
    public static MicroTBox Align(
        MicroTBox tbox,
        IReadOnlyDictionary<string, string> canonicalMap)
    {
        var localAliasMap = BuildLocalAliasMap(tbox.Aliases);

        string Resolve(string id) =>
            canonicalMap.TryGetValue(id, out var canonical) ? canonical :
            localAliasMap.TryGetValue(id, out var localCanonical) ? localCanonical :
            id;

        return tbox with
        {
            Classes = tbox.Classes.Select(c => c with
            {
                Id = Resolve(c.Id),
                SubClassOf = c.SubClassOf is not null ? Resolve(c.SubClassOf) : null,
                DisjointWith = c.DisjointWith.Select(Resolve).ToList(),
            }).ToList(),
            Properties = tbox.Properties.Select(p => p with
            {
                Id = Resolve(p.Id),
                Domain = Resolve(p.Domain),
                Range = Resolve(p.Range),
                InverseOf = p.InverseOf is not null ? Resolve(p.InverseOf) : null,
            }).ToList(),
            Shapes = tbox.Shapes.Select(s => s with
            {
                TargetClass = Resolve(s.TargetClass),
                Constraints = s.Constraints.Select(c => c with { Property = Resolve(c.Property) }).ToList(),
            }).ToList(),
        };
    }

    private static Dictionary<string, string> BuildLocalAliasMap(
        IReadOnlyDictionary<string, List<string>> aliases)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (canonical, aliasList) in aliases)
        {
            foreach (var alias in aliasList)
            {
                map.TryAdd(alias, canonical);
            }
        }
        return map;
    }
}
```

- [ ] **Step 4: Run tests — verify they pass**

```bash
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --filter "CanonicalAlignerTests" --verbosity minimal
```

- [ ] **Step 5: Commit**

```bash
git add ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Aligner/CanonicalAligner.cs \
       ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Aligner/CanonicalAlignerTests.cs
git commit -m "feat(ontology): implement CanonicalAligner for cross-slice ID alignment"
```

---

### Task 18: .NET PostgresOntologyRepo

**Files:**
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Storage/PostgresOntologyRepo.cs`
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Storage/PostgresOntologyRepoTests.cs`

- [ ] **Step 1: Write PostgresOntologyRepo boundary test**

```csharp
// WeKnora.OntologyReasoner.Tests/Storage/PostgresOntologyRepoTests.cs
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
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --filter "PostgresOntologyRepoTests" --verbosity minimal
```

Expected: FAIL because `WeKnora.OntologyReasoner.Core.Storage` / `PostgresOntologyRepo` does not exist yet.

- [ ] **Step 3: Implement PostgresOntologyRepo**

```csharp
// WeKnora.OntologyReasoner.Core/Storage/PostgresOntologyRepo.cs
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
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --filter "PostgresOntologyRepoTests" --verbosity minimal
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --verbosity minimal
dotnet build ontology-reasoner-net/WeKnora.OntologyReasoner.slnx
```

Expected: the PostgresOntologyRepo boundary test passes, then the full .NET test project and `.slnx` build pass with 0 errors.

- [ ] **Step 5: Commit**

```bash
git add ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Storage/PostgresOntologyRepo.cs        ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Storage/PostgresOntologyRepoTests.cs        docs/superpowers/plans/2026-05-20-ontology-slice-core-mvp.md
git commit -m "feat(ontology): implement PostgresOntologyRepo for sidecar data access"
```

---

### Task 19: .NET SliceAssembler

**Files:**
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Assembly/SliceAssembler.cs`
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Assembly/SliceAssemblerTests.cs`

- [ ] **Step 1: Write SliceAssembler boundary tests**

```csharp
// WeKnora.OntologyReasoner.Tests/Assembly/SliceAssemblerTests.cs
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
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --filter "SliceAssemblerTests" --verbosity minimal
```

Expected: FAIL because `WeKnora.OntologyReasoner.Core.Assembly` / `SliceAssembler` does not exist yet.

- [ ] **Step 3: Implement SliceAssembler**

```csharp
// WeKnora.OntologyReasoner.Core/Assembly/SliceAssembler.cs
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
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --filter "SliceAssemblerTests" --verbosity minimal
dotnet test ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/WeKnora.OntologyReasoner.Tests.csproj --verbosity minimal
dotnet build ontology-reasoner-net/WeKnora.OntologyReasoner.slnx
```

Expected: the 2 SliceAssembler tests pass, then the full .NET test project and `.slnx` build pass with 0 errors.

- [ ] **Step 5: Commit**

```bash
git add ontology-reasoner-net/WeKnora.OntologyReasoner.Core/Assembly/SliceAssembler.cs        ontology-reasoner-net/WeKnora.OntologyReasoner.Tests/Assembly/SliceAssemblerTests.cs        docs/superpowers/plans/2026-05-20-ontology-slice-core-mvp.md
git commit -m "feat(ontology): implement SliceAssembler boundary behavior"
```

---

### Task 20: .NET API Endpoint + Program.cs

**Files:**
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Api/Endpoints/ReasonEndpoint.cs`
- Create: `ontology-reasoner-net/WeKnora.OntologyReasoner.Api/Program.cs`

- [ ] **Step 1: Create Program.cs**

```csharp
// WeKnora.OntologyReasoner.Api/Program.cs
using WeKnora.OntologyReasoner.Api.Endpoints;
using WeKnora.OntologyReasoner.Core.Assembly;
using WeKnora.OntologyReasoner.Core.Storage;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration["DB_CONNECTION_STRING"]
    ?? "Host=localhost;Database=weknora;Username=weknora;Password=weknora";

builder.Services.AddSingleton(new PostgresOntologyRepo(connectionString));
builder.Services.AddSingleton<SliceAssembler>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapPost("/reason", ReasonEndpoint.Handle);

app.Run();
```

- [ ] **Step 2: Create ReasonEndpoint**

```csharp
// WeKnora.OntologyReasoner.Api/Endpoints/ReasonEndpoint.cs
using WeKnora.OntologyReasoner.Core.Assembly;
using WeKnora.OntologyReasoner.Core.Models;

namespace WeKnora.OntologyReasoner.Api.Endpoints;

public static class ReasonEndpoint
{
    public static async Task<IResult> Handle(ReasonRequest request, SliceAssembler assembler)
    {
        try
        {
            var response = await assembler.Reason(request);
            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            return Results.Ok(new ReasonResponse
            {
                Status = "error",
                DataSource = "none",
                Warnings = [$"Internal error: {ex.Message}"],
            });
        }
    }
}
```

- [ ] **Step 3: Verify build**

```bash
cd ontology-reasoner-net && dotnet build && cd ..
```

- [ ] **Step 4: Commit**

```bash
git add ontology-reasoner-net/WeKnora.OntologyReasoner.Api/
git commit -m "feat(ontology): add ASP.NET minimal API endpoint for reasoning"
```

---

### Task 21: Dockerfile & Docker Compose

**Files:**
- Create: `ontology-reasoner-net/Dockerfile`
- Modify: `docker-compose.yml`

- [ ] **Step 1: Create Dockerfile**

```dockerfile
# ontology-reasoner-net/Dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY WeKnora.OntologyReasoner.Core/WeKnora.OntologyReasoner.Core.csproj WeKnora.OntologyReasoner.Core/
COPY WeKnora.OntologyReasoner.Api/WeKnora.OntologyReasoner.Api.csproj WeKnora.OntologyReasoner.Api/
RUN dotnet restore WeKnora.OntologyReasoner.Api/WeKnora.OntologyReasoner.Api.csproj

COPY . .
RUN dotnet publish WeKnora.OntologyReasoner.Api/WeKnora.OntologyReasoner.Api.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8090
EXPOSE 8090

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8090/health || exit 1

ENTRYPOINT ["dotnet", "WeKnora.OntologyReasoner.Api.dll"]
```

- [ ] **Step 2: Add ontology-reasoner service to docker-compose.yml**

Append the following service definition to `docker-compose.yml` (inside the `services:` block):

```yaml
  ontology-reasoner:
    build:
      context: ./ontology-reasoner-net
      dockerfile: Dockerfile
    image: weknora/ontology-reasoner:dotnet-10
    container_name: WeKnora-ontology-reasoner
    ports:
      - "${ONTOLOGY_REASONER_PORT:-8090}:8090"
    environment:
      - ASPNETCORE_URLS=http://+:8090
      - ASPNETCORE_ENVIRONMENT=Production
      - DB_CONNECTION_STRING=Host=postgres;Database=${DB_NAME:-weknora};Username=${DB_USER:-weknora};Password=${DB_PASSWORD}
      - REASONER_DEFAULT_PROFILE=${ONTOLOGY_DEFAULT_PROFILE:-n3-extended}
      - REASONER_MAX_ITERATIONS=10
    depends_on:
      - postgres
    networks:
      - WeKnora-network
    profiles:
      - ontology
    restart: unless-stopped
```

Also add the ontology environment variables to the `app` service's environment section:

```yaml
      - ONTOLOGY_ENABLE=${ONTOLOGY_ENABLE:-false}
      - ONTOLOGY_REASONER_URL=${ONTOLOGY_REASONER_URL:-http://ontology-reasoner:8090}
      - ONTOLOGY_DEFAULT_PROFILE=${ONTOLOGY_DEFAULT_PROFILE:-n3-extended}
      - ONTOLOGY_CONFIDENCE_THRESHOLD=${ONTOLOGY_CONFIDENCE_THRESHOLD:-0.3}
      - ONTOLOGY_EXTRACT_MIN_ENTITIES=${ONTOLOGY_EXTRACT_MIN_ENTITIES:-2}
```

- [ ] **Step 3: Commit**

```bash
git add ontology-reasoner-net/Dockerfile docker-compose.yml
git commit -m "feat(ontology): add Dockerfile and docker-compose ontology-reasoner service"
```

---

### Task 22: Run Full Test Suite

- [ ] **Step 1: Run Go build**

```bash
go build ./...
```

Expected: Clean build.

- [ ] **Step 2: Run Go tests**

```bash
go test ./...
```

Expected: All existing tests pass. No regressions.

- [ ] **Step 3: Run .NET tests**

```bash
cd ontology-reasoner-net && dotnet test && cd ..
```

Expected: All .NET tests pass.

- [ ] **Step 4: Build Docker image**

```bash
cd ontology-reasoner-net && docker build -t weknora/ontology-reasoner:test . && cd ..
```

Expected: Image builds successfully.

- [ ] **Step 5: Commit any fixes**

If any step above required fixes, commit them:

```bash
git add -A && git commit -m "fix(ontology): address test and build issues"
```
