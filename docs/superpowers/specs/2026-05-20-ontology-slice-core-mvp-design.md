# 可推理 Ontology 切片 Core MVP：实施规格书

**状态：** 已批准，可进入实施阶段
**范围：** Core MVP（原始 RFC 第 1-11 节）
**延后：** 专家审核（第 12 节）、混合推理（第 13 节）、TBox 治理（第 14 节）、联邦 SPARQL（第 15 节）——各自单独出规格书

---

## 1. 概述

### 1.1 新增能力

在 WeKnora 现有的三条检索路线之外，新增第四条——**Ontology 切片**——为系统提供形式化推理能力。

| 查询类型 | 路由到 |
|---------|--------|
| 模糊语义 / 相似性 | 向量 RAG（已有） |
| 实体-关系探索 | Graph RAG / Neo4j（已有） |
| 概念浏览 / 背景知识 | LLM Wiki（已有） |
| 传递关系、类型层级、约束验证、SPARQL | **Ontology 切片（本规格书）** |

### 1.2 核心思路：切片式 Ontology

传统做法：`全局 schema 先行 → 实例填充 → 全局推理`

本设计：`每个 chunk 自带 micro-TBox → 检索召回相关 chunk → 查询时合成 ontology → 局部推理`

每个 chunk 既是检索单元，也是推理单元。Ontology 是 chunk 的派生属性，而非全局基础设施。

### 1.3 关键架构决策

| 决策 | 选择 | 理由 |
|------|------|------|
| Sidecar 技术栈 | .NET 10 + dotNetRDF 3.x | 成熟的 RDFS/N3/SHACL 支持，无 JVM 依赖，镜像约 100MB |
| 实例事实来源 | PostgreSQL 自包含 | Ontology 路径不依赖 Neo4j |
| Sidecar 数据访问 | 直连 PG | 延迟低于 Go API 代理方案；可接受的 schema 耦合 |
| 推理时机 | 查询时（非入库时） | 推理范围可控；切片可灵活组合 |
| 中间格式 | JSON（非 OWL/Turtle） | LLM 直出 JSON 错误率远低于 OWL；可逐条后验校验 |
| 存储方案 | chunks 表 JSONB 字段 | 复用现有 PG，零额外基础设施 |

---

## 2. 入库管线

### 2.1 插入位置

新步骤插入到 `graphBuilder.BuildGraph`（位于 `internal/application/service/graph.go`），在 `buildChunkGraph`（第 7 步）之后，与 Neo4j 写入**并行**执行。

### 2.2 完整管线

```
1. 文档上传                                 (已有：KnowledgeService)
2. 解析 + Chunk 切分                        (已有：docreader + ChunkService)
3. ChunkExtractTask 入队                    (已有：asynq 队列)
4. 实体抽取                                 (已有：extractEntities, 每 chunk 一次 LLM 调用)
5. 关系抽取                                 (已有：extractRelationships, LLM 分批处理)
6. PMI + 强度加权                           (已有：calculateWeights)
7. Chunk 关系图构建                         (已有：buildChunkGraph)
   |
   +-- 8. micro-TBox 抽取  【新增】          (每 chunk 一次 LLM 调用，并发上限 4)
   |      |
   |      +-- 10. PG 写入：ontology_json + instance_facts_json  【新增】
   |      +-- 11. Canonical Map 更新  【新增】
   |
   +-- 9. Neo4j 写入                        (已有，可选，与步骤 8 并行)
```

### 2.3 micro-TBox 抽取逻辑

```go
func (b *graphBuilder) extractMicroTBoxes(ctx context.Context, chunks []*types.Chunk) {
    g, gctx := errgroup.WithContext(ctx)
    g.SetLimit(MaxConcurrentEntityExtractions) // 4

    for _, chunk := range chunks {
        chunk := chunk
        g.Go(func() error {
            chunkEntities := b.entitiesForChunk(chunk.ID)
            chunkRels := b.relationshipsForChunk(chunk.ID)

            // 稀疏 chunk：实体太少，无法提取 schema 内容
            if len(chunkEntities) < 2 {
                return nil
            }

            tbox, err := b.callMicroTBoxLLM(gctx, chunk, chunkEntities, chunkRels)
            if err != nil {
                logger.GetLogger(gctx).WithError(err).
                    Warnf("micro-tbox extraction failed for chunk %s", chunk.ID)
                return nil // 单 chunk 失败不阻塞整个流程
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

关键行为：
- **并发**：复用 `MaxConcurrentEntityExtractions = 4`
- **优雅降级**：单 chunk 失败仅记日志，不影响其他
- **Evidence 后验校验**：每条公理的 `evidence` 必须是 `chunk.Content` 的子串；不满足则丢弃该公理
- **置信度阈值**：`confidence < 0.3` 整个 micro-TBox 不写库
- **稀疏跳过**：实体 < 2 的 chunk 不发起 micro-TBox LLM 调用

### 2.4 实例事实构造

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

### 2.5 Canonical Map 自动填充

micro-TBox 抽取完成后，将别名自动写入 `ontology_canonical_map`：

```go
func (b *graphBuilder) upsertCanonicalMap(
    ctx context.Context,
    tenantID int64,
    kbID string,
    tbox *types.MicroTBox,
    chunkID string,
) error {
    for canonicalID, aliases := range tbox.Aliases {
        err := b.canonicalMapRepo.Upsert(ctx, tenantID, kbID, canonicalID, aliases, chunkID)
        if err != nil {
            return err
        }
    }
    return nil
}
```

---

## 3. 数据模型

### 3.1 新增 Go 类型

新建文件：`internal/types/micro_tbox.go`

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

### 3.2 Chunk 结构体扩展

在 `internal/types/chunk.go` 中：

```go
type Chunk struct {
    // ... 现有字段 ...

    OntologyJSON        *MicroTBox `json:"ontology_json,omitempty"`
    OntologyExtractedAt *time.Time `json:"ontology_extracted_at,omitempty"`
    OntologyConfidence  *float64   `json:"ontology_confidence,omitempty"`
    InstanceFactsJSON   []Triple   `json:"instance_facts_json,omitempty"`
}
```

### 3.3 数据库迁移

**迁移 000052：chunk_ontology.up.sql**

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

**迁移 000053：ontology_canonical_map.up.sql**

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

执行顺序：先 000052，再 000053。

---

## 4. Prompt 设计

### 4.1 模板

添加到 `config/prompt_templates/graph_extraction.yaml`，模板 ID 为 `default_extract_micro_tbox`。

**输入：** chunk 原文 + 已抽取的实体 + 关系
**输出：** 单个 JSON 对象，包含 `classes`、`properties`、`shapes`、`aliases`、`axioms`、`confidence`

硬约束：
1. 每个 class 的 `id` 必须匹配输入实体列表中的 entity type（或文本中明确提到的泛化概念）
2. 每个 property 的 `id` 必须对应输入关系列表中的 relationship description
3. 每条抽取必须包含 `evidence` 字段——来自原文的逐字引用
4. `id` 字段使用 ASCII（class 用 PascalCase，property 用 camelCase）；`label` 字段使用 `{{language}}`
5. **空优于错**——当文本不包含 schema 级内容时，返回全空数组是正确答案

模板包含：
- 所有 6 个顶层字段的详细规格说明
- 每种 property characteristic（transitive、symmetric 等）的判定指南
- 两个完整示例：富抽取（Romeo & Juliet）和稀疏抽取（会议记录 → 全空）
- 6 条明确的反模式

完整模板文本：参见原始 RFC（`docs/可推理Ontology切片设计.md`，第 6.1 节，模板 ID `default_extract_micro_tbox`）。模板内容无修改，应逐字复制到 `graph_extraction.yaml` 中。

### 4.2 配置接入

```go
// internal/config/config.go
type ConversationConfig struct {
    // ... 现有字段 ...
    ExtractMicroTBoxPrompt string `mapstructure:"extract_micro_tbox_prompt"`
}
```

```yaml
# config/config.yaml
conversation:
  extract_micro_tbox_prompt: "default_extract_micro_tbox"
```

### 4.3 抽取后校验

1. **解析校验**：必须是合法 JSON 且匹配 `MicroTBox` 结构体
2. **Evidence 校验**：每个 `evidence` 字段必须是 `chunk.Content` 的子串；不合格的公理被丢弃
3. **置信度过滤**：`confidence < 0.3` → 整个 micro-TBox 不写库
4. **稀疏跳过**：实体 < 2 的 chunk 直接跳过 LLM 调用

---

## 5. 推理 Sidecar

### 5.1 技术栈

- .NET 10 (C#) + ASP.NET Core Minimal API
- dotNetRDF 3.x（dotNetRdf、dotNetRdf.Inferencing、dotNetRdf.Shacl）
- Npgsql 10.0.2 + Dapper 2.1.79 访问 PG
- Docker 镜像：`mcr.microsoft.com/dotnet/aspnet:10.0-alpine`（约 100MB）

### 5.2 核心洞察

OWL "推理" 实际上是三种不同性质的操作。每种对应一个成熟的 dotNetRDF 组件：

| 操作 | 性质 | dotNetRDF 组件 |
|------|------|---------------|
| 蕴含（推出新三元组） | 单调正向推理 | `SimpleN3RulesReasoner` + 自动生成的 N3 规则 |
| 约束验证（检测违反） | 否定/基数约束 | `Shacl.ShapesGraph` + 自动生成的 SHACL 约束 |
| 类型层级传递闭包 | RDFS 子集 | `StaticRdfsReasoner`（内置） |

### 5.3 N3 规则映射

| micro-TBox Property 特性 | 生成的 N3 规则 |
|-------------------------|---------------|
| transitive | `{ ?a :P ?b . ?b :P ?c } => { ?a :P ?c } .` |
| symmetric | `{ ?a :P ?b } => { ?b :P ?a } .` |
| inverseOf Q | `{ ?a :P ?b } => { ?b :Q ?a } .`（+ 反向） |
| domain D | `{ ?x :P ?y } => { ?x a :D } .` |
| range R | `{ ?x :P ?y } => { ?y a :R } .` |
| equivalentClass A↔B | `{ ?x a :A } => { ?x a :B } .`（+ 反向） |

subClassOf 和 subPropertyOf 由 `StaticRdfsReasoner` 处理（不走 N3 规则）。

### 5.4 SHACL 约束映射

| OWL 特性 | SHACL 表达 |
|---------|----------|
| functional | `sh:maxCount 1`（property shape 上） |
| inverseFunctional | 基于 SPARQL 的自定义 shape |
| asymmetric | 基于 SPARQL 的自定义 shape（自查反向） |
| irreflexive | 基于 SPARQL 的自定义 shape |
| disjointWith | `sh:not [ sh:class :Other ]` |
| min_count / max_count | `sh:minCount` / `sh:maxCount` |
| datatype | `sh:datatype` |
| in_values | `sh:in` |

### 5.5 推理 Profile

| Profile | 实现 | 延迟 |
|---------|------|------|
| `rdfs` | 仅 `StaticRdfsReasoner` | < 50ms |
| `n3-extended`（默认） | RDFS + `SimpleN3RulesReasoner` + 自动生成规则集 | < 200ms |
| `shacl` | `Shacl.ShapesGraph` 校验 | < 100ms |

默认：`n3-extended`。SHACL 仅在 `query.type=consistency|shacl` 或 `query.profile=shacl` 时执行。

### 5.6 Fixpoint 推理引擎

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

收敛检测使用三元组集合 hash 比较（而非 `Triples.Count`），因为极少数情况下 N3 应用可能同时增删三元组。

### 5.7 安全防护

- Fixpoint 上限：`maxIterations = 10`
- 增长率限制：单轮三元组增长 > 100x → 立即中断
- 总量上限：硬限 100K 三元组
- SPARQL 超时：≤ 2000ms，结果行上限 ≤ 1000
- 规则白名单：仅允许从 micro-TBox 自动生成的规则，不接受运行时注入
- Chunk 数量限制：每次查询 ≤ 50 个 chunk_ids
- 禁止：SPARQL 中的 `SERVICE` / `LOAD` / 外部端点访问

### 5.8 项目结构

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

NuGet 依赖：
- `dotNetRdf` 3.3.*
- `dotNetRdf.Inferencing` 3.3.*
- `dotNetRdf.Shacl` 3.3.*
- `Npgsql` 10.0.2
- `Dapper` 2.1.79

### 5.9 切片合成（查询路径）

`SliceAssembler` 编排查询时的合并流程：

1. 批量从 PG 拉取 `ontology_json` + `instance_facts_json`（按 chunk_ids）
2. 加载目标租户 + KB 的 `canonical_map`
3. `CanonicalAligner.Align`：用 alias → canonical_id 重写所有 class/property ID（大小写不敏感，不可变副本）
4. 三路生成：`RdfGenerator` → schema 图、`N3RuleGenerator` → 规则字符串、`ShaclGenerator` → SHACL shapes 图
5. 从实例事实构建数据图
6. 冲突检测：SPARQL 查询同一 class 是否同时存在 `subClassOf + disjointWith` → 记 warning，不阻塞

溯源映射：`Dictionary<Triple, List<string>>` 将每个 schema 三元组映射到其来源 chunk_ids（同一三元组可能来自多个 chunk），SPARQL 查询完成后用于追溯证据。

### 5.10 工程注意事项

1. N3 解析器：仅使用最简语法 `{ pattern } => { pattern } .`，避免高级 N3 特性
2. 字符串拼接方式生成规则比 API 构造三元组更简单易调试
3. 超过 10K 三元组时：从 `IGraph.ExecuteQuery` 切换到 `InMemoryDataset` + `LeviathanQueryProcessor`
4. AOT：先用 ReadyToRun 模式；原生 AOT 留作后期优化（dotNetRDF 有反射依赖）
5. PG 连接池：sidecar 工作负载配置 `Maximum Pool Size=20` 一般足够

---

## 6. Agent 工具集成

### 6.1 新增工具：ontology_reason

新建文件：`internal/agent/tools/ontology_reason.go`

```go
const ToolOntologyReason = "ontology_reason"

type OntologyReasonInput struct {
    KnowledgeBaseIDs []string `json:"knowledge_base_ids"`
    Query            string   `json:"query"`
    ReasonerProfile  string   `json:"reasoner_profile,omitempty"`
    IncludeEvidence  bool     `json:"include_evidence,omitempty"`
}
```

工具描述引导 Agent 判断何时使用：
- 传递查询（"X 的所有间接依赖"）→ ontology_reason
- 包含关系（"X 是 Y 的一种吗？"）→ ontology_reason
- 约束验证（"X 是否满足 schema？"）→ ontology_reason
- 互斥/否定（"X 能同时是 A 和 B 吗？"）→ ontology_reason

注册：在 `internal/agent/tools/definitions.go` 添加 `ToolOntologyReason` 常量。仅在 `ONTOLOGY_ENABLE=true` 时注册。

### 6.2 工具工作流

1. 混合检索（已有）召回候选 chunk_ids
2. HTTP POST 调用 sidecar `/reason` 端点
3. 将结构化结果（含溯源信息）格式化后回注 Agent 上下文

### 6.3 Agent Prompt 补充

```
当用户询问结构化查询、约束检查或传递关系类问题时，优先使用 ontology_reason。
不确定时，先用 knowledge_search 检索，再用 ontology_reason 推理。
不要对模糊文本搜索使用 ontology_reason。
```

---

## 7. Sidecar API 契约

### 7.1 端点

```
POST /reason
Content-Type: application/json
```

### 7.2 请求

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

- `instance_facts`：可选。未提供时，sidecar 按 chunk_ids 自动从 PG 拉取。
- `query.profile`：默认 `n3-extended`。控制蕴含推理路径。
- `query.type`：控制返回哪种结果。

### 7.3 响应

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

### 7.4 执行契约

1. `query.type=consistency` **必须**执行 SHACL 校验。若未执行，返回 `status=error`。禁止默认返回 `consistent=true`。
2. 默认执行路径：`n3-extended` 蕴含推理。SHACL 仅在 `type=consistency|shacl` 或 `profile=shacl` 时执行。
3. 缺省 `instance_facts` → sidecar 自动从 PG 拉取。响应必须包含 `data_source=fetched`。
4. SPARQL 超时：≤ 2000ms。结果行上限：≤ 1000。
5. SPARQL 中禁止：`SERVICE`、`LOAD`、外部端点访问。
6. 仅允许白名单 IRI 前缀和已对齐词表。

---

## 8. 跨切片对齐

### 8.1 问题

同一概念在不同 chunk 中会产生不同 ID（例如 `Engineer` vs `软件工程师` vs `SoftwareEngineer`）。不做对齐，跨 chunk 的事实无法连通推理。

### 8.2 两级对齐策略（Core MVP）

| 级别 | 时机 | 方式 | 代价 |
|------|------|------|------|
| Tier 1 | LLM 抽取时 | Prompt 要求填写 micro-TBox 的 `aliases` 字段 | 零（内嵌在已有调用中） |
| Tier 2 | Sidecar 合并时 | `CanonicalAligner` 使用 canonical_map + chunk 别名，大小写不敏感 | 低（字符串查找） |

Tier 3（离线 embedding + LLM 对齐 job）延后到扩展规格书。

### 8.3 CanonicalAligner 逻辑

优先级：`全局 canonical_map > chunk 本地 aliases > 原始 ID`

- 大小写不敏感匹配（`StringComparer.OrdinalIgnoreCase`）
- 不可变重写：`record with` 语法生成新副本，原始 micro-TBox 不被修改
- 未命中的 ID 保留原样——系统接受局部歧义

### 8.4 Canonical Map 自动填充

入库时 micro-TBox 抽取完成后：
1. 从 micro-TBox 的 `aliases` 字段提取所有条目
2. 对每个 canonical_id → alias 列表，upsert 到 `ontology_canonical_map`
3. 将 chunk_id 追加到 `source_chunks` 数组（去重）
4. 新别名与已有 `aliases` 数组做 union 合并（非替换）
5. 置信度初始值为 0.5（LLM 产出）；后续 upsert 不降低置信度

合并语义：若 canonical_id 已存在，别名取并集，source_chunks 追加。若两个 chunk 为同一别名声明了不同的 canonical_id，两条映射共存——`CanonicalAligner` 使用先匹配原则（全局 canonical_map 优先于 chunk 本地别名）。Core MVP 接受此类冲突；专家审核（未来规格书）负责解决。

Core MVP 阶段，这是 canonical_map 的唯一填充机制。

---

## 9. 部署与配置

### 9.1 环境变量

```bash
ONTOLOGY_ENABLE=true
ONTOLOGY_REASONER_URL=http://ontology-reasoner:8090
ONTOLOGY_DEFAULT_PROFILE=n3-extended
ONTOLOGY_CONFIDENCE_THRESHOLD=0.3
ONTOLOGY_EXTRACT_MIN_ENTITIES=2
```

### 9.2 配置文件

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

启用方式：`docker-compose --profile ontology up -d`

### 9.4 .env.example

```env
# ===== Ontology 推理 (可选, .NET sidecar) =====
ONTOLOGY_ENABLE=false
ONTOLOGY_REASONER_URL=http://ontology-reasoner:8090
ONTOLOGY_DEFAULT_PROFILE=n3-extended
ONTOLOGY_CONFIDENCE_THRESHOLD=0.3
ONTOLOGY_EXTRACT_MIN_ENTITIES=2
```

### 9.5 Feature Flag 行为

当 `ONTOLOGY_ENABLE=false` 时：
- `BuildGraph` 中跳过 micro-TBox 抽取步骤
- `ontology_reason` 工具不注册到 Agent 工具箱
- 无需启动 sidecar 容器
- 对现有管线零影响

### 9.6 迁移执行

按顺序执行：
1. `migrations/versioned/000052_chunk_ontology.up.sql`
2. `migrations/versioned/000053_ontology_canonical_map.up.sql`

---

## 10. 风险与权衡

### 10.1 设计取舍

| 取舍 | 放弃 | 获得 |
|------|------|------|
| 不追求全局一致性 | 跨 chunk 推理可能不完整 | 增量友好；新文档不触发全局重建 |
| JSON 中间格式（非 OWL） | 多一层后处理 | LLM 错误率显著降低；可逐条校验 |
| chunks 表 JSONB（非 triple store） | 查询时需反序列化 | 复用现有 PG；零额外基础设施 |
| N3 + SHACL（非 OWL DL reasoner） | 无完整 OWL DL 推理（一致性证明、复杂类表达式、nominal 推理） | 无 JVM 依赖；性能可预测；覆盖 85-90% RAG 场景 |
| 查询时推理（非入库时） | 每次查询都有计算成本 | 推理范围可控；切片可灵活组合 |
| PG 自包含（非 Neo4j 依赖） | 实体/关系数据重复存储 | Ontology 路径完全独立于 Neo4j |

### 10.2 已知风险

| 风险 | 影响 | 缓解 |
|------|------|------|
| LLM 跨 chunk 命名漂移 | 同一概念无法连通推理 | Tier 1+2 对齐；接受局部歧义 |
| 单 chunk 视角片面 | 公理保守，schema 不完整 | 设计意图：宁少勿错 |
| 不同 chunk 的矛盾公理共存 | 推理结果可能不一致 | 检测冲突、记 warning、不阻塞 |
| LLM 编造 evidence | 假公理混入系统 | 后验校验：evidence 必须是 chunk 原文子串 |
| Sidecar 延迟 | Agent 响应变慢 | 默认 n3-extended profile；超时降级到不推理 |
| 公理空间爆炸 | 合成图过大 | 限制 ≤ 50 个 chunk/查询；100K 三元组硬上限 |

### 10.3 本设计不能做的事

- 真正的 OWL DL 一致性证明
- 等价类完整闭包推理
- 复杂类表达式（intersectionOf / unionOf / hasValue）
- Nominal 推理（owl:oneOf）

未来逃生通道：增加 `dl` profile，通过 HTTP 调外部 Stardog/RDFox，保留 JVM 依赖的可选路径。

---

## 11. 验收标准

### 11.1 Core MVP 验证

1. 上传 10 篇技术文档 → 实体 ≥ 2 的 chunk 自动抽出 micro-TBox
2. 查询"X 的所有间接依赖" → 通过传递推理返回正确结果
3. 查询"X 是 Y 的一种吗？" → 利用类层级返回正确的包含判断
4. SHACL 校验查询 → 正确识别约束违反
5. 每条推理结果都附带 evidence chunk 链接
6. `ONTOLOGY_ENABLE=false` → 对现有管线零影响
7. 端到端延迟（查询路径）：典型 20-chunk 切片 < 500ms

### 11.2 契约验证

1. `consistency` 请求未执行 SHACL → 返回 `status=error`（不是 `true`）
2. 缺省 `instance_facts` → sidecar 自动拉取，响应中 `data_source=fetched`
3. 全链路默认 profile 为 `n3-extended`

---

## 12. 未来扩展（不在本规格书范围内）

以下模块已在原始 RFC（第 12-15 节）中详细设计，将作为独立规格书推进：

- **专家审核与反馈闭环**（第 12 节）：审核 UI、canonical_map 人工校正、审核结果反馈到 prompt
- **混合推理 / Neuro-Symbolic**（第 13 节）：NL→SPARQL、软关联、SHACL 修复建议、Reasoning Tape
- **TBox 演进治理**（第 14 节）：KB 级全局 TBox、晋升算法、版本化、迁移
- **联邦 SPARQL**（第 15 节）：跨 KB 推理、联邦协议、对齐解析
