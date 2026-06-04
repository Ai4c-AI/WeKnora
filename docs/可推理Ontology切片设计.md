---
title: 可推理 Ontology 切片：技术设计文档
tags: [设计文档, 知识图谱, Ontology, 推理, RAG, dotNetRDF, .NET]
status: 提案 (RFC)
---

# 可推理 Ontology 切片：技术设计文档

## 0. 文档定位

本文档是一份**技术设计提案**，描述如何在 WeKnora 现有知识库文档上传管线之上，增量构建**可推理的 Ontology 切片**（micro-TBox），使系统在保持现有向量检索 + Graph RAG 能力的同时，获得有限但**真正可形式化推理**的能力。

文档中：
- **【已有】** 标记的部分对应仓库中现存代码，给出实际文件路径
- **【新增】** 标记的部分是本提案要新增的工件
- **【改造】** 标记的部分是对现有代码的扩展

**技术栈说明**：推理 Sidecar 采用 **.NET 10 (C#) + dotNetRDF 3.x**，通过 N3 前向规则 + SHACL 约束 + RDFS 推理的组合覆盖 RAG 场景的推理需求，**不引入 JVM 依赖**。详见 §7。

---

## 1. 概述

### 1.1 设计目标

为 WeKnora 增加一条**轻量的可推理语义层**，满足以下需求：

1. **完全复用**现有的"文档上传 → chunk 抽取 → 实体/关系提取"主干，不另起 ingestion 管线
2. **不要求**全局一致的本体 schema——避免本体工程的高门槛
3. **支持局部推理**：传递闭包、类型层级、约束验证、对称/反对称推理
4. **可解释**：每条公理可追溯到原文证据
5. **与现有 Agent 工具体系并列**：作为新的检索/推理工具被 Agent 调用

### 1.2 核心思路

传统知识图谱 + 本体的做法是：
```
全局 schema 先行 → 实例填充 → 全局推理
```
这对 LLM 抽取场景不友好，schema 漂移会导致系统级失败。

本设计采用**切片式 Ontology**：
```
每 chunk 自带 micro-TBox → 检索定位相关 chunk → 切片合成 query-time ontology → 局部推理
```

每个 chunk 既是检索单元，也是推理单元。Ontology 是 chunk 的派生属性，而非全局基础设施。

### 1.3 与现有架构的关系

```
WeKnora 现有三条平行路线：

  路线一: Vector RAG       (语义检索, 已有)
  路线二: Graph RAG/Neo4j  (实体-关系图, 已有)
  路线三: LLM Wiki         (人/Agent 浏览, 已有)

本设计新增第四条:

  路线四: Ontology 切片     (可推理语义层, 本文档)
```

四条路线共享同一份 chunk 数据源，通过 Agent 工具体系做**查询路由**：

| 查询类型 | 路由到 |
|---------|--------|
| 模糊语义、相似性 | 路线一（vector） |
| 实体关系探索 | 路线二（Neo4j） |
| 概念浏览、背景知识 | 路线三（Wiki） |
| 结构化查询、传递关系、约束验证 | **路线四（本设计）** |

---

## 2. 现有架构回顾

### 2.1 Graph RAG 路线【已有】

入口：[internal/application/service/extract.go:83](internal/application/service/extract.go#L83) `NewChunkExtractTask`

异步处理：[internal/application/service/extract.go:177](internal/application/service/extract.go#L177) `ChunkExtractService.Handle`

核心抽取：[internal/application/service/graph.go:375](internal/application/service/graph.go#L375) `graphBuilder.BuildGraph`
- [extractEntities](internal/application/service/graph.go#L98)：LLM 抽实体
- [extractRelationships](internal/application/service/graph.go#L193)：LLM 抽关系
- [calculateWeights](internal/application/service/graph.go#L501)：PMI + Strength 加权
- [buildChunkGraph](internal/application/service/graph.go#L632)：传导到 chunk 间

存储：[internal/application/repository/retriever/neo4j/repository.go:46](internal/application/repository/retriever/neo4j/repository.go#L46) `Neo4jRepository.AddGraph`，通过 APOC 写入 Neo4j

Prompt：[config/prompt_templates/graph_extraction.yaml](config/prompt_templates/graph_extraction.yaml) 两个模板
- `default_extract_entities`：11 类固定实体类型
- `default_extract_relationships`：关系 + 强度 5-10

Agent 工具：[internal/agent/tools/query_knowledge_graph.go](internal/agent/tools/query_knowledge_graph.go)

启用开关：环境变量 `NEO4J_ENABLE=true`

### 2.2 LLM Wiki 路线【已有】

Prompt：[internal/agent/prompts_wiki.go](internal/agent/prompts_wiki.go)
- `WikiSummaryPrompt`：生成 Markdown 摘要页
- `WikiKnowledgeExtractPrompt`：同时抽 entities + concepts

特点：
- slug 命名空间 `entity/xxx`、`concept/xxx`
- **slug 复用机制**保证跨次更新的稳定性
- 输出 Markdown wiki 页 + 双向链接 `[[slug|display]]`

### 2.3 现状的局限

| 能力 | Graph RAG | LLM Wiki | 缺口 |
|------|-----------|----------|------|
| 实体识别 | ✓ | ✓ | - |
| 关系识别 | ✓ | △ | - |
| 类型层级（subClassOf） | ✗ | ✗ | **缺** |
| 属性公理（transitive 等） | ✗ | ✗ | **缺** |
| 约束验证（SHACL） | ✗ | ✗ | **缺** |
| 可解释证据 | △ | ✓ | - |
| 形式化推理 | ✗ | ✗ | **缺** |

本设计补齐"缺口"列。

---

## 3. 切片式 Ontology 设计

### 3.1 核心概念：micro-TBox

**micro-TBox**：一个 chunk 范围内的本体片段，包含：
- 该 chunk 涉及的 class 声明（subClassOf、disjointWith）
- 该 chunk 涉及的 property 声明（domain、range、characteristics）
- SHACL 式完整性约束
- 跨 chunk 对齐用的 aliases
- 自由形式公理（兜底）

micro-TBox 是 **chunk 的派生属性**，作为 JSON-LD 存在 chunk 表的字段里。

### 3.2 设计原则

| 原则 | 含义 | 工程后果 |
|------|------|---------|
| **局部性** | 每个 micro-TBox 只在自己 chunk 范围内有效 | 不追求全局一致；不同 chunk 的 micro-TBox 可以矛盾 |
| **派生性** | micro-TBox 跟着 chunk 走 | chunk 重抽取时 micro-TBox 失效重建，无独立版本管理 |
| **证据强制** | 每条公理必带 `evidence` 字段 | 反幻觉的最强约束，可解释 |
| **词汇锁定** | class/property id 限制在已抽取的实体/关系类型中 | 避免 LLM 自创词汇导致对齐失败 |
| **空优于错** | 不确定就跳过 | 大多数 chunk 的 micro-TBox 是稀疏的，正常 |
| **查询时合成** | 推理在查询路径上，不在入库路径上 | 推理范围 = 检索召回范围，避免全图爆炸 |

### 3.3 数据流总览

```
┌─────────────────────────────────────────────────────────┐
│  入库路径（异步, 复用现有 asynq 队列）                   │
├─────────────────────────────────────────────────────────┤
│  1. 文档上传                                              │
│     └→ 现有 KnowledgeService                             │
│  2. 解析 + Chunk 切分                                    │
│     └→ 现有 docreader sidecar + ChunkService             │
│  3. ChunkExtractTask 入队                                │
│     └→ 现有 NewChunkExtractTask (extract.go)             │
│  4. 实体抽取                                              │
│     └→ 现有 graphBuilder.extractEntities                 │
│  5. 关系抽取                                              │
│     └→ 现有 graphBuilder.extractRelationships            │
│  6. micro-TBox 抽取  【新增】                            │
│     └→ graphBuilder.extractMicroTBoxes                   │
│  7. 落库                                                  │
│     ├→ 现有 Neo4jRepository.AddGraph (实例图)           │
│     └→ 新增 chunk.ontology_json 持久化                  │
├─────────────────────────────────────────────────────────┤
│  查询路径（同步, Agent 工具触发）                         │
├─────────────────────────────────────────────────────────┤
│  1. Agent 决策                                            │
│     └→ 现有 Agent + 新增 ontology_reason 工具           │
│  2. 候选 chunk 召回                                       │
│     └→ 现有 HybridSearch                                 │
│  3. micro-TBox 切片拉取  【新增】                        │
│     └→ chunk.ontology_json 反序列化                     │
│  4. 命名对齐 + ontology 合成  【新增】                   │
│     └→ canonical_map 表 + reasoner sidecar              │
│  5. 推理执行  【新增】                                    │
│     └→ .NET sidecar (dotNetRDF: RDFS + N3 + SHACL)      │
│  6. 结果回注 LLM  【已有】                                │
│     └→ 现有 Agent 上下文注入                             │
└─────────────────────────────────────────────────────────┘
```

---

## 4. 全流程 Pipeline

### 4.1 阶段 0：文档上传【已有】

入口在 [internal/handler/knowledge.go](internal/handler/knowledge.go) 系列 handler，最终落到 KnowledgeService。无需改造。

### 4.2 阶段 1：Chunk 切分与解析【已有】

参考 [docs/CHUNKING.md](docs/CHUNKING.md)。docreader sidecar 负责 PDF/Word/Markdown 等解析，ChunkService 负责切分入库。无需改造。

### 4.3 阶段 2：实体与关系抽取【已有】

完全复用 [graphBuilder.BuildGraph](internal/application/service/graph.go#L375) 流程：

1. 并发抽取 entities（每个 chunk 一次 LLM 调用）
2. 批量并发抽取 relationships（每 5 个 chunk 一批）
3. PMI + Strength 加权
4. 计算 entity degree
5. 构建 chunk 关系图
6. 在同一 graph builder 中抽取 micro-TBox，并把 alias 写入 canonical_map
7. 生成可视化/诊断信息

### 4.4 阶段 3：micro-TBox 抽取【新增】

**插入位置**：在 [graphBuilder.BuildGraph](internal/application/service/graph.go#L375) 现有步骤 5（buildChunkGraph）之后、第 6（generateKnowledgeGraphDiagram）之前。

**核心逻辑**：对每个 chunk 单独发起一次 LLM 调用，将该 chunk 的 entities + relationships 作为输入，让 LLM 把实例级事实"提升"为 schema 级公理。实现必须贴合现有 Go 分层：canonical map 模型放 `internal/types`，接口放 `internal/types/interfaces`，GORM 实现放 `internal/application/repository`，由 `internal/container` 注册后注入 graph builder；不要新增独立 `internal/repo` 包。`tenantID` 使用现有 `uint64`，从 chunk 输入推导，`kbID` 使用 chunk 的 `KnowledgeBaseID`。

```go
// 伪代码：插入到 BuildGraph 末尾
func (b *graphBuilder) extractMicroTBoxes(ctx context.Context, chunks []*types.Chunk) {
    g, gctx := errgroup.WithContext(ctx)
    g.SetLimit(MaxConcurrentEntityExtractions)

    for _, chunk := range chunks {
        chunk := chunk
        g.Go(func() error {
            chunkEntities := b.entitiesForChunk(chunk.ID)
            chunkRels := b.relationshipsForChunk(chunk.ID)

            // 稀疏 chunk 跳过：实体太少不可能有 schema 内容
            if len(chunkEntities) < 2 {
                return nil
            }

            tbox, err := b.callMicroTBoxLLM(gctx, chunk, chunkEntities, chunkRels)
            if err != nil {
                logger.GetLogger(gctx).WithError(err).
                    Warnf("micro-tbox extraction failed for chunk %s", chunk.ID)
                return nil  // 单 chunk 失败不阻塞流程
            }

            // 后验校验：evidence 必须是 chunk 原文子串
            tbox = b.validateEvidence(tbox, chunk.Content)

            // 低置信度丢弃
            if tbox.Confidence < 0.3 {
                return nil
            }

            chunk.OntologyJSON = tbox
            chunk.OntologyConfidence = &tbox.Confidence
            chunk.InstanceFactsJSON = b.buildInstanceFacts(chunkEntities, chunkRels)

            if len(tbox.Aliases) > 0 && b.canonicalMapRepo != nil {
                if err := b.upsertCanonicalMap(gctx, chunk.TenantID, chunk.KnowledgeBaseID, tbox, chunk.ID); err != nil {
                    logger.GetLogger(gctx).WithError(err).
                        Warnf("canonical map upsert failed for chunk %s", chunk.ID)
                }
            }
            return nil
        })
    }
    g.Wait()

    // 持久化由调用方负责（ChunkRepository.UpdateChunk）
}
```

**关键设计点**：
1. **并发数复用** `MaxConcurrentEntityExtractions = 4`
2. **失败优雅降级**：单个 chunk 失败不影响其他
3. **后验校验 evidence**：每条公理的 `evidence` 字段必须是 chunk 原文的子串，否则丢弃这条公理
4. **置信度阈值**：整体 `confidence < 0.3` 整个 micro-TBox 不写库
5. **稀疏跳过**：实体 < 2 的 chunk 不抽 micro-TBox
6. **canonical_map 写入**：复用 `interfaces.CanonicalMapRepository`，对 `tbox.Aliases` 做 upsert；别名和 source chunk 均做去重合并

### 4.5 阶段 4：micro-TBox 存储与索引【新增】

存储位置：直接挂在 `chunks` 表上，不另起新表。

```sql
-- migrations/versioned/000057_chunk_ontology.up.sql
ALTER TABLE chunks
  ADD COLUMN ontology_json JSONB DEFAULT NULL,
  ADD COLUMN ontology_extracted_at TIMESTAMPTZ DEFAULT NULL,
  ADD COLUMN ontology_confidence REAL DEFAULT NULL;

-- 按租户 + 知识库的查询索引
CREATE INDEX idx_chunks_ontology_extracted
  ON chunks (tenant_id, knowledge_base_id)
  WHERE ontology_json IS NOT NULL;

-- 按 class id 反查的 GIN 索引（推理 sidecar 用）
-- 索引表达式直接提取 classes[*].id，避免只索引整个 classes 数组导致命中不稳定
CREATE INDEX idx_chunks_ontology_class_ids
  ON chunks USING GIN ((jsonb_path_query_array(ontology_json, '$.classes[*].id')));
```

理由：
- micro-TBox 是 chunk 的派生属性，逻辑上属于 chunk
- JSON-LD 文本，PostgreSQL JSONB 足够
- Neo4j 是 LPG，表达不了 OWL 公理，强行存进去得不偿失

### 4.6 阶段 5：查询触发【改造】

现有 Agent 工具集合（[internal/agent/tools/](internal/agent/tools/)）新增一项 `ontology_reason`，与现有的 `knowledge_search`、`query_knowledge_graph` 并列。

工具定义参考 [query_knowledge_graph.go](internal/agent/tools/query_knowledge_graph.go) 的模式。详见第 8 节。

### 4.7 阶段 6：切片合成【新增】

由推理 sidecar 负责。流程：

```
1. 接收来自 Agent 工具的请求：
   - knowledge_base_ids: 命名空间限定
   - chunk_ids: 检索阶段命中的 chunk 列表
   - query: SPARQL / SHACL / 自然语言

2. 批量拉取这些 chunk 的 ontology_json

3. 命名对齐:
   - 拉取全局 canonical_map（参见第 9 节）
   - 用 alias → canonical_id 映射归一所有 class/property id
   - 未命中的保留原 id（接受局部歧义）

4. 物理合并到一张 dotNetRDF `Graph`:
   - IRI 前缀按 chunk 隔离（保留来源）
   - 公理跨 chunk 共存（同名归一后自然 union）
   - 冲突公理（如同一 class 既 disjoint 又 subClass）打 warning，二者并存
```

**C# 实现（核心代码）：**

主编排器 `SliceAssembler` ——把上面 4 步串起来：

```csharp
public class SliceAssembler
{
    private readonly PostgresOntologyRepo _repo;
    private readonly CanonicalAligner _aligner;
    private readonly RdfGenerator _rdfGen;
    private readonly ILogger<SliceAssembler> _logger;

    public SliceAssembler(
        PostgresOntologyRepo repo,
        CanonicalAligner aligner,
        RdfGenerator rdfGen,
        ILogger<SliceAssembler> logger)
        => (_repo, _aligner, _rdfGen, _logger) = (repo, aligner, rdfGen, logger);

    public async Task<AssembledSlice> AssembleAsync(
        long tenantId,
        string[] knowledgeBaseIds,
        string[] chunkIds,
        CancellationToken ct = default)
    {
        // 步骤 1: 批量拉取 micro-TBox
        var tboxes = await _repo.GetMicroTBoxes(tenantId, chunkIds, ct);
        if (tboxes.Count == 0)
        {
            _logger.LogWarning("No micro-TBox found for chunks {ChunkIds}", chunkIds);
            return AssembledSlice.Empty(chunkIds);
        }

        // 步骤 2: 加载全局 canonical_map（KB 级 alias → canonical_id）
        var canonicalMap = await _repo.GetCanonicalMap(tenantId, knowledgeBaseIds, ct);

        // 步骤 3: 命名对齐
        var aligned = _aligner.Align(tboxes, canonicalMap);

        // 步骤 4: 物理合并为 dotNetRDF Graph + 冲突检测
        var schemaGraph = _rdfGen.Generate(aligned, out var provenance);
        var conflicts = DetectConflicts(schemaGraph);
        foreach (var c in conflicts)
            _logger.LogWarning("Schema conflict (not blocking): {Conflict}", c);

        return new AssembledSlice(
            SchemaGraph:    schemaGraph,
            AlignedTBoxes:  aligned,
            CanonicalMap:   canonicalMap,
            Provenance:     provenance,
            Conflicts:      conflicts,
            SourceChunkIds: chunkIds);
    }

    /// <summary>
    /// 检测同一 class 既 subClassOf 又 disjointWith 同一目标的矛盾。
    /// 注意：IGraph.ExecuteQuery 仅支持简单 SPARQL；若后续查询变复杂，
    /// 需改用 SparqlQueryParser + LeviathanQueryProcessor（基于 InMemoryDataset）。
    /// </summary>
    private List<string> DetectConflicts(IGraph g)
    {
        var conflicts = new List<string>();
        var sparql = @"
            PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>
            PREFIX owl:  <http://www.w3.org/2002/07/owl#>
            SELECT ?a ?b WHERE {
                ?a rdfs:subClassOf ?b .
                ?a owl:disjointWith ?b .
            }";
        var results = (SparqlResultSet)g.ExecuteQuery(sparql);
        foreach (var row in results)
            conflicts.Add($"{row["a"]} both subClassOf and disjointWith {row["b"]}");
        return conflicts;
    }
}

public record AssembledSlice(
    IGraph SchemaGraph,
    List<MicroTBox> AlignedTBoxes,
    Dictionary<string, string> CanonicalMap,
    Dictionary<Triple, string> Provenance,   // triple → chunk_id, 用于推理后追溯证据
    List<string> Conflicts,
    string[] SourceChunkIds)
{
    public static AssembledSlice Empty(string[] chunkIds) => new(
        SchemaGraph: new Graph(),
  AlignedTBoxes: new List<MicroTBox>(),
  CanonicalMap: new Dictionary<string, string>(),
  Provenance: new Dictionary<Triple, string>(),
  Conflicts: new List<string>(),
        SourceChunkIds: chunkIds);
}
```

命名对齐器 `CanonicalAligner` ——核心是 alias → canonical_id 的查找表 + 不可变重写：

```csharp
public class CanonicalAligner
{
    /// <summary>
    /// 把每个 micro-TBox 里的 class/property id 全部归一到 canonical_id。
    /// 优先级: 全局 canonical_map > chunk 自身 aliases > 原 id（接受局部歧义）
    /// </summary>
    public List<MicroTBox> Align(
        List<MicroTBox> tboxes,
        IReadOnlyDictionary<string, string> globalCanonicalMap)
    {
        // 合并全局表 + 每个 chunk 自身的 aliases，构建大小写不敏感的查找表
        var lookup = new Dictionary<string, string>(
            globalCanonicalMap, StringComparer.OrdinalIgnoreCase);
        foreach (var tbox in tboxes)
        foreach (var (canonical, aliasList) in tbox.Aliases)
        foreach (var alias in aliasList)
            lookup.TryAdd(alias, canonical);

        // 在拷贝上重写所有 id（不污染原 tbox）
        return tboxes.Select(t => RewriteIds(t, lookup)).ToList();
    }

    private MicroTBox RewriteIds(MicroTBox t, IReadOnlyDictionary<string, string> map)
    {
        string Canon(string id) => map.GetValueOrDefault(id, id);

        return t with
        {
            Classes = t.Classes.Select(c => c with
            {
                Id           = Canon(c.Id),
                SubClassOf   = c.SubClassOf is null ? null : Canon(c.SubClassOf),
                DisjointWith = c.DisjointWith.Select(Canon).ToList()
            }).ToList(),
            Properties = t.Properties.Select(p => p with
            {
                Id        = Canon(p.Id),
                Domain    = Canon(p.Domain),
                Range     = Canon(p.Range),
                InverseOf = p.InverseOf is null ? null : Canon(p.InverseOf)
            }).ToList(),
            Shapes = t.Shapes.Select(s => s with
            {
                TargetClass = Canon(s.TargetClass),
                Constraints = s.Constraints.Select(cn => cn with
                {
                    Property = Canon(cn.Property)
                }).ToList()
            }).ToList()
        };
    }
}
```

RDF 生成器 `RdfGenerator` ——合并为 `Graph`，同时通过 `out` 参数返回 triple → chunk_id 的 provenance 映射（推理后用于证据追溯）：

```csharp
public class RdfGenerator
{
    public IGraph Generate(
        List<MicroTBox> alignedTBoxes,
        out Dictionary<Triple, string> provenance)
    {
        var g = new Graph();
      g.NamespaceMap.AddNamespace("rdf",  new Uri("http://www.w3.org/1999/02/22-rdf-syntax-ns#"));
        g.NamespaceMap.AddNamespace("rdfs", new Uri("http://www.w3.org/2000/01/rdf-schema#"));
        g.NamespaceMap.AddNamespace("owl",  new Uri("http://www.w3.org/2002/07/owl#"));
      provenance = new Dictionary<Triple, string>();

        foreach (var tbox in alignedTBoxes)
        {
            // 该 chunk 产出的所有 triples 都打上来源标签
            foreach (var triple in EmitTBoxToGraph(g, tbox))
                provenance[triple] = tbox.ChunkId;
        }

        return g;
    }

    /// <summary>示意：写入 ClassDecl 的 schema triples。完整逻辑见 §7.6。</summary>
    private IEnumerable<Triple> EmitTBoxToGraph(IGraph g, MicroTBox tbox)
    {
        foreach (var cls in tbox.Classes)
        {
            var clsNode = g.CreateUriNode(new Uri($"urn:weknora:class/{cls.Id}"));

            var typeT = new Triple(clsNode,
              g.CreateUriNode(new Uri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type")),
              g.CreateUriNode(new Uri("http://www.w3.org/2002/07/owl#Class")));
            g.Assert(typeT);
            yield return typeT;

            if (cls.SubClassOf is not null)
            {
                var parent = g.CreateUriNode(new Uri($"urn:weknora:class/{cls.SubClassOf}"));
                var subT = new Triple(clsNode, g.CreateUriNode("rdfs:subClassOf"), parent);
                g.Assert(subT);
                yield return subT;
            }
            // disjointWith / properties / shapes 同理，省略
        }
    }
}
```

**几个关键设计点：**

1. **不可变重写**：`CanonicalAligner` 用 record `with` 语法生成新副本，原 `MicroTBox` 不被污染。便于调试和并发安全
2. **大小写不敏感对齐**：alias 查找用 `StringComparer.OrdinalIgnoreCase`，覆盖 "RAG" / "rag" / "Rag" 等大小写漂移
3. **chunk 自身 aliases 兜底**：即使全局 `canonical_map` 没有某条 alias，也能用 chunk 内 `aliases` 字段做最后一道归一
4. **冲突不阻塞**：检测到 `subClassOf + disjointWith` 同时存在等矛盾时只 warning，不抛异常——这是 by-design 接受局部歧义
5. **Provenance 独立存储**：triple → chunk_id 映射用 `Dictionary<Triple, string>` 而非 RDF reification，避免污染推理图。SPARQL 查询完后查这张表回溯证据
6. **`TripleStore` 还是 `Graph`**：本阶段返回单一 `IGraph` 因为下游 reasoner 接受该接口；若需保留 named graph 隔离，可以同时维护一个 `TripleStore` 旁路

### 4.8 阶段 7：推理执行【新增】

详见第 7 节"推理 Sidecar 设计"。

### 4.9 阶段 8：结果回注 LLM【已有】

复用现有 Agent 工具结果注入机制。推理结果以结构化文本回传给 Agent：

```
=== Ontology Reasoning Result ===
Query: "工程师 A 管理的所有人都向谁汇报？"
Reasoning chain:
  1. transitivity of `manages` (chunk_42)
  2. inverse property `manages` ⇔ `reportsTo` (chunk_88)
Inferred triples:
  - A manages B (asserted)
  - A manages C (asserted)
  - B reportsTo A (inferred via inverse)
  - C reportsTo A (inferred via inverse)
Evidence chunks: [chunk_42, chunk_88]
```

---

## 5. 数据模型

### 5.1 现有数据模型【已有】

- [internal/types/graph.go](internal/types/graph.go)：`Entity`、`Relationship`、`GraphBuilder` 接口
- [internal/types/extract_graph.go](internal/types/extract_graph.go)：`GraphNode`、`GraphRelation`、`GraphData`、`PromptTemplateStructured`、`NameSpace`
- [internal/types/knowledgebase.go](internal/types/knowledgebase.go)：包含 `ExtractConfig`（KB 级抽取配置）

### 5.2 新增类型【新增】

新建文件 `internal/types/micro_tbox.go`：

```go
package types

// MicroTBox represents a chunk-scoped ontology fragment.
// All ids are local to the chunk; cross-chunk alignment happens at query time.
type MicroTBox struct {
    Classes    []ClassDecl     `json:"classes"`
    Properties []PropertyDecl  `json:"properties"`
    Shapes     []ShapeDecl     `json:"shapes"`
    Aliases    map[string][]string `json:"aliases"`
    Axioms     []FreeAxiom     `json:"axioms"`
    Confidence float64         `json:"confidence"`
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
    Characteristics []string `json:"characteristics"` // functional/transitive/symmetric/...
    InverseOf       *string  `json:"inverseOf"`
    Evidence        string   `json:"evidence"`
}

type ShapeDecl struct {
    TargetClass string             `json:"target_class"`
    Constraints []ShapeConstraint  `json:"constraints"`
    Evidence    string             `json:"evidence"`
}

type ShapeConstraint struct {
    Property  string      `json:"property"`
    MinCount  *int        `json:"min_count"`
    MaxCount  *int        `json:"max_count"`
    Datatype  *string     `json:"datatype"`
    InValues  []string    `json:"in_values"`
}

type FreeAxiom struct {
    Statement string `json:"statement"`
    Evidence  string `json:"evidence"`
}
```

### 5.3 Chunk 表字段扩展【新增】

[internal/types/chunk.go](internal/types/chunk.go) 的 `Chunk` 结构体增加：

```go
type Chunk struct {
    // ... 现有字段 ...

    OntologyJSON         *MicroTBox `json:"ontology_json,omitempty"`
    OntologyExtractedAt  *time.Time `json:"ontology_extracted_at,omitempty"`
    OntologyConfidence   *float64   `json:"ontology_confidence,omitempty"`
}
```

### 5.4 跨切片对齐表【新增】

```sql
-- migrations/versioned/000058_ontology_canonical_map.up.sql
CREATE TABLE ontology_canonical_map (
    id              BIGSERIAL PRIMARY KEY,
    tenant_id       BIGINT NOT NULL,
    knowledge_base_id TEXT NOT NULL,
    kind            TEXT NOT NULL CHECK (kind IN ('class', 'property')),
    canonical_id    TEXT NOT NULL,
    aliases         TEXT[] NOT NULL DEFAULT '{}',
    source_chunks   TEXT[] NOT NULL DEFAULT '{}',
    confidence      REAL NOT NULL DEFAULT 0.5,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, knowledge_base_id, kind, canonical_id)
);

CREATE INDEX idx_canonical_map_aliases
  ON ontology_canonical_map USING GIN (aliases);
```

---

## 6. Prompt 设计

### 6.1 新增 prompt 模板

追加到 [config/prompt_templates/graph_extraction.yaml](config/prompt_templates/graph_extraction.yaml)：

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

### 6.2 关键设计决策

| 决策 | 理由 |
|------|------|
| 输出 JSON 而非 OWL/Turtle | LLM 直接吐 OWL 语法错误率高，由后处理转 RDF |
| 第三步而非第一步 | entities + relationships 作为输入，让 LLM "提升"而非"创造" |
| Evidence 强制 verbatim | 反幻觉的最强约束，且后处理可校验 |
| 词汇表锁定到已抽取实体类型 | 避免 LLM 自创词汇导致跨 chunk 对齐崩盘 |
| 两个例子（一富一贫） | 教会 LLM "空也是正确答案" |
| 置信度自评 | 提供下游过滤信号 |

### 6.3 配置接入

[internal/config/config.go](internal/config/config.go) 的 `Conversation` 配置区扩展：

```go
type ConversationConfig struct {
    // ... 现有字段 ...
    ExtractEntitiesPrompt       string `mapstructure:"extract_entities_prompt"`
    ExtractRelationshipsPrompt  string `mapstructure:"extract_relationships_prompt"`
    ExtractMicroTBoxPrompt      string `mapstructure:"extract_micro_tbox_prompt"`  // 新增
}
```

[config/config.yaml](config/config.yaml) 增加映射：
```yaml
conversation:
  extract_micro_tbox_prompt: "default_extract_micro_tbox"
```

---

## 7. 推理 Sidecar 设计

### 7.1 技术选型与核心思路

**实现栈**：.NET 10 (C#) + ASP.NET Core Minimal API + **dotNetRDF 3.x**

**核心权衡**：.NET 生态没有成熟的纯 OWL DL reasoner（dotNetRDF 官方文档明确说明 `IOwlReasoner` 接口为 incomplete 状态，`PelletReasoner` 类仅是远程 Pellet Server 的客户端）。但 RAG 场景**也不需要**完整 OWL DL——可以用 dotNetRDF 内置的成熟组件覆盖 85-90% 推理需求，**且完全摆脱 JVM 依赖**。

**核心洞察**：OWL "推理"不是单一操作，是**三种性质混合**的操作组合：

| 操作 | 性质 | 落点（dotNetRDF 组件） |
|------|------|---------------------|
| 蕴含（推出新三元组） | 单调正向 | `SimpleN3RulesReasoner` + 自动生成的 N3 规则集 |
| 约束验证（检测违反） | 否定性质 | `Shacl.ShapesGraph` + 自动生成的 SHACL 约束 |
| 类层级传递 | RDFS 子集 | `StaticRdfsReasoner`（内置） |

micro-TBox 的每个公理按"操作性质"**自动分发**到三套引擎，每套都是 dotNetRDF 已有的成熟组件。

### 7.2 部署形态【新增】

仿照现有 [docreader/](docreader/) 的 sidecar 模式：

```
ontology-reasoner-net/
├── WeKnora.OntologyReasoner.Api/        # ASP.NET Core 入口
│   ├── Program.cs
│   ├── Endpoints/
│   │   ├── ReasonEndpoint.cs
│   │   └── ValidateEndpoint.cs
│   └── WeKnora.OntologyReasoner.Api.csproj
├── WeKnora.OntologyReasoner.Core/       # 核心库
│   ├── Models/MicroTBox.cs
│   ├── Aligner/CanonicalAligner.cs
│   ├── Generators/
│   │   ├── RdfGenerator.cs              # TBox → RDF schema 三元组
│   │   ├── N3RuleGenerator.cs           # TBox → N3 前向规则
│   │   └── ShaclGenerator.cs            # TBox → SHACL 约束
│   ├── Engine/
│   │   ├── ReasoningEngine.cs           # 编排 RDFS + N3 reasoner
│   │   └── ShaclValidator.cs
│   └── Storage/PostgresOntologyRepo.cs
├── WeKnora.OntologyReasoner.Tests/
├── Dockerfile
└── README.md
```

依赖（`*.csproj`）：

```xml
<PackageReference Include="dotNetRdf" Version="3.3.*" />
<PackageReference Include="dotNetRdf.Inferencing" Version="3.3.*" />
<PackageReference Include="dotNetRdf.Shacl" Version="3.3.*" />
<PackageReference Include="Npgsql" Version="8.*" />
<PackageReference Include="Dapper" Version="2.*" />
```

镜像基础：`mcr.microsoft.com/dotnet/aspnet:10.0-alpine`，约 100MB；启用 AOT 后启动 < 300ms。

### 7.3 micro-TBox → N3 规则映射表【核心规约】

每个 micro-TBox 字段按下表映射为前向 N3 规则。

**Property characteristics**

| 特性 | N3 规则模板 |
|------|------------|
| `transitive` | `{ ?a :P ?b. ?b :P ?c } => { ?a :P ?c } .` |
| `symmetric` | `{ ?a :P ?b } => { ?b :P ?a } .` |
| `inverseOf Q` | 双向：`{ ?a :P ?b } => { ?b :Q ?a } .` 和反向 |
| `reflexive`（带 domain D） | `{ ?a a :D } => { ?a :P ?a } .` |

**Property domain / range**

| 公理 | N3 规则 |
|------|--------|
| `domain D` | `{ ?x :P ?y } => { ?x a :D } .` |
| `range R` | `{ ?x :P ?y } => { ?y a :R } .` |

**类层级**（不进 N3，走 RDFS reasoner）

| 公理 | 处理 |
|------|------|
| `subClassOf` | 内置 `StaticRdfsReasoner` 处理传递闭包 |
| `equivalentClass` | 双向 N3 规则：`{ ?x a :A } => { ?x a :B } .` 与反向 |
| `subPropertyOf` | RDFS reasoner |

### 7.4 micro-TBox → SHACL 约束映射表【核心规约】

下列 OWL 特性涉及否定/约束性质，N3 单调正向推理表达不了，统一走 SHACL：

| 特性 | SHACL 表达 |
|------|----------|
| `functional` | `sh:maxCount 1` on property shape |
| `inverseFunctional` | SPARQL-based shape |
| `asymmetric` | SPARQL-based shape（自查反向） |
| `irreflexive` | SPARQL-based shape |
| `disjointWith` | `sh:not [ sh:class :Other ]` |
| `min_count` / `max_count` | `sh:minCount` / `sh:maxCount` |
| `datatype` | `sh:datatype` |
| `in_values` | `sh:in` |

### 7.5 三路生成器架构

```
            MicroTBox JSON (from PostgreSQL)
                       ↓
           ┌───────────┴───────────┐
           ↓                        ↓
   ┌──────────────┐        ┌──────────────┐
   │ Canonical    │←───────│ canonical_map│
   │ Aligner      │        │ (PostgreSQL) │
   └──────┬───────┘        └──────────────┘
          ↓
   Aligned MicroTBoxes
          ↓
   ┌──────┼──────────────┬─────────────┐
   ↓      ↓              ↓             ↓
 RdfGen  N3RuleGen   ShaclGen    (instance facts)
   ↓      ↓              ↓             ↓
 Schema   N3 Rules    SHACL Shapes  Data Graph
 Graph    string      Graph
   │       │              │             │
   └───┬───┴───┬──────────┴─────┬───────┘
       ↓       ↓                ↓
  ┌──────────────────┐    ┌──────────────┐
  │ ReasoningEngine  │    │ SHACL        │
  │ - RDFS reasoner  │    │ Validator    │
  │ - N3 reasoner    │    └──────┬───────┘
  │ - Fixpoint loop  │           │
  └────────┬─────────┘           │
           ↓                     ↓
   Inferred Graph        Validation Report
           ↓
   SPARQL Engine (Leviathan)
           ↓
   Query Result + Provenance
```

### 7.6 关键代码骨架

**N3 规则生成器**（最核心模块）：

```csharp
public class N3RuleGenerator
{
    public string Generate(IEnumerable<MicroTBox> tboxes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@prefix : <urn:weknora:> .");
        sb.AppendLine();

        foreach (var tbox in tboxes)
        foreach (var prop in tbox.Properties)
            EmitPropertyRules(sb, prop);

        return sb.ToString();
    }

    private void EmitPropertyRules(StringBuilder sb, PropertyDecl prop)
    {
        var p = $"<urn:weknora:prop/{prop.Id}>";

        // Domain / Range
        if (!string.IsNullOrEmpty(prop.Domain))
            sb.AppendLine($"{{ ?x {p} ?y }} => {{ ?x a <urn:weknora:class/{prop.Domain}> }} .");
        if (!string.IsNullOrEmpty(prop.Range))
            sb.AppendLine($"{{ ?x {p} ?y }} => {{ ?y a <urn:weknora:class/{prop.Range}> }} .");

        // Characteristics
        foreach (var ch in prop.Characteristics ?? Enumerable.Empty<string>())
        {
            switch (ch.ToLowerInvariant())
            {
                case "transitive":
                    sb.AppendLine($"{{ ?a {p} ?b . ?b {p} ?c }} => {{ ?a {p} ?c }} .");
                    break;
                case "symmetric":
                    sb.AppendLine($"{{ ?a {p} ?b }} => {{ ?b {p} ?a }} .");
                    break;
                // functional / asymmetric / irreflexive → 走 ShaclGenerator
            }
        }

        // InverseOf
        if (!string.IsNullOrEmpty(prop.InverseOf))
        {
            var inv = $"<urn:weknora:prop/{prop.InverseOf}>";
            sb.AppendLine($"{{ ?a {p} ?b }} => {{ ?b {inv} ?a }} .");
            sb.AppendLine($"{{ ?a {inv} ?b }} => {{ ?b {p} ?a }} .");
        }
    }
}
```

**Fixpoint 推理引擎**：

```csharp
public class ReasoningEngine
{
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

        // 用三元组集合 hash 判定收敛（而非 Triples.Count，
        // 因为极少数情况下 N3 应用可能同时增删三元组）
        int prevHash, iter = 0;
        do
        {
            prevHash = ComputeTriplesHash(working);
            rdfsReasoner.Apply(working);
            n3Reasoner.Apply(working);
            if (++iter >= maxIterations) break;  // 死循环防护
        } while (ComputeTriplesHash(working) != prevHash);

        return working;
    }

    private static int ComputeTriplesHash(IGraph g)
    {
        var hash = new HashCode();
        foreach (var t in g.Triples.OrderBy(t => t.ToString()))
            hash.Add(t);
        return hash.ToHashCode();
    }
}
```

**SHACL 验证**：

```csharp
public class ShaclValidator
{
    public ValidationReport Validate(IGraph data, IGraph shapes)
    {
        var shapesGraph = new ShapesGraph(shapes);
        return shapesGraph.Validate(data);
    }
}
```

**API endpoint**（ASP.NET Core Minimal API）：

```csharp
app.MapPost("/reason", async (
    ReasonRequest req,
    PostgresOntologyRepo repo,
    CanonicalAligner aligner,
    RdfGenerator rdfGen,
    N3RuleGenerator n3Gen,
    ShaclGenerator shGen,
    ReasoningEngine engine,
    ShaclValidator validator) =>
{
    var tboxes = await repo.GetMicroTBoxes(req.TenantId, req.ChunkIds);
    var canonicalMap = await repo.GetCanonicalMap(req.TenantId, req.KnowledgeBaseIds);
    var aligned = aligner.Align(tboxes, canonicalMap);

    var schemaGraph = rdfGen.Generate(aligned, out var provenance);
    var rules = n3Gen.Generate(aligned);
    var shapesGraph = shGen.Generate(aligned);

    var dataFacts = (req.InstanceFacts is { Count: > 0 })
      ? req.InstanceFacts
      : await repo.GetInstanceFacts(req.TenantId, req.ChunkIds); // 缺省回填：按 chunk_ids 拉实例事实
    var dataGraph = BuildDataGraph(dataFacts);
    var profile = string.IsNullOrWhiteSpace(req.Query.Profile)
      ? "n3-extended"
      : req.Query.Profile.ToLowerInvariant();

    var inferred = profile switch
    {
      "rdfs" => engine.ReasonRdfsOnly(dataGraph, schemaGraph),
      "shacl" => engine.ReasonRdfsOnly(dataGraph, schemaGraph),
      _ => engine.Reason(dataGraph, schemaGraph, rules)
    };

    // SHACL 执行语义：
    // 1) query.type 为 consistency 或 shacl 时强制执行
    // 2) profile 为 shacl 时也执行（显式校验模式）
    var mustValidate = req.Query.Type is "consistency" or "shacl"
      || profile == "shacl";
    var validation = mustValidate
      ? validator.Validate(inferred, shapesGraph)
      : null;

    object result = req.Query.Type switch
    {
      "sparql" => RunSparqlSafe(inferred, req.Query.Body, timeoutMs: 2000, maxRows: 1000),
        "consistency" => validation is null
          ? new { error = "consistency check requires SHACL validation" }
          : new { consistent = validation.Conforms },
      "entailment" => CheckEntailment(inferred, req.Query.Body),
        "shacl" => validation ?? new { conforms = true, results = Array.Empty<object>() },
        _ => null
    };

    return Results.Ok(new
    {
        status = (validation?.Conforms ?? true) ? "ok" : "violations",
        results = result,
        inferred_triples = DiffTriples(dataGraph, inferred),
        violations = validation?.Results ?? Array.Empty<object>(),
        evidence_chunks = BuildEvidenceChunks(DiffTriples(dataGraph, inferred), provenance)
    });
});
```

说明：`query.profile` 只决定**蕴含推理路径**（RDFS / N3），`query.type` 决定返回哪类结果；其中 `consistency` 与 `shacl` 请求会**强制执行 SHACL 校验**。

### 7.7 推理能力分级与覆盖度

| Profile | 实现 | 覆盖能力 | 延迟 |
|---------|------|---------|------|
| `rdfs` | dotNetRDF `StaticRdfsReasoner` | subClass / subProperty / domain / range 传递 | < 50ms |
| `n3-extended`（默认） | RDFS + `SimpleN3RulesReasoner` + 自动生成规则集 | + transitive / symmetric / inverse / equivalentClass | < 200ms |
| `shacl` | dotNetRDF `Shacl.ShapesGraph` | functional / disjointness / cardinality 违反检测 | < 100ms |

**默认执行 `n3-extended` 蕴含推理；当 `query.type=consistency|shacl`（或 `profile=shacl`）时同时执行 SHACL 校验**，覆盖 RAG 场景 85-90% 推理需求。

**能做**（务实清单）：
- ✅ 类型层级传递（RDFS）
- ✅ Transitive / Symmetric / Inverse property 推理
- ✅ Domain / Range 类型推断
- ✅ Functional / disjointness / cardinality 约束**违反检测**
- ✅ SPARQL 1.1 查询（含 property paths）
- ✅ 自定义 N3 规则（业务逻辑扩展）

**不能做**（诚实声明）：
- ❌ 真正的逻辑一致性证明（OWL DL consistency）
- ❌ 等价类完整闭包推理
- ❌ 复杂类表达式（intersectionOf / unionOf / hasValue）
- ❌ Nominal 推理（owl:oneOf）

如果未来确实需要 OWL DL 能力，加 `dl` profile 通过 HTTP 调外部 Stardog/RDFox（保留 JVM 依赖的可选路径），**不影响主路径设计**。

### 7.8 API 规约

```
POST /reason
Content-Type: application/json

Request:
{
  "tenant_id": 123,
  "knowledge_base_ids": ["kb_abc"],
  "chunk_ids": ["chunk_42", "chunk_88", "chunk_153"],
  "instance_facts": [                  // 可选；缺省时 sidecar 会按 chunk_ids 自动回填
    {"s": "RomeoMontague", "p": "isMemberOf", "o": "Montague"}
  ],
  "query": {
    "type": "sparql" | "consistency" | "entailment" | "shacl",
    "body": "...",
    "profile": "rdfs" | "n3-extended" | "shacl"  // 默认 "n3-extended"
  }
}

Response:
{
  "status": "ok" | "violations" | "error",
  "results": [...],                    // SPARQL 结果集 / SHACL 报告
  "inferred_triples": [...],           // 推理出的新三元组
  "data_source": "provided" | "fetched", // 实例事实来源
  "evidence_chunks": ["chunk_42", ...],
  "warnings": [...],
  "elapsed_ms": 123
}
```

执行约束（生产建议）：
- SPARQL 查询超时：`<= 2000ms`
- SPARQL 结果行上限：`<= 1000`
- `consistency` 请求必须执行 SHACL；若未执行，返回 `status=error`（禁止默认 `true`）
- 禁止 `SERVICE` / `LOAD` / 外部端点访问
- 仅允许白名单前缀与已对齐词表 IRI

**Go 主项目调用契约与 sidecar 实现语言无关**——Agent 工具 [ontology_reason.go](internal/agent/tools/) 通过 HTTP 调用此 API，未来更换实现语言不影响主路径。

### 7.9 性能与扩展性

| 操作 | 复杂度 | 典型耗时 |
|------|--------|---------|
| N3 单轮应用 | O(rules × triples) | 数十毫秒 |
| Fixpoint 收敛 | 通常 2-5 轮 | < 100ms |
| SHACL 验证 | O(shapes × instances) | < 50ms |
| SPARQL（推理后图） | dotNetRDF Leviathan 优化 | 毫秒级 |

**典型负载**（50 chunks × 500 instances × 30 rules）端到端 < 300ms。

**死循环防护**：
1. `maxIterations = 10` 硬上限
2. 单轮三元组增长率 > 100x 立即中断
3. 总三元组数硬上限 100K
4. N3 规则白名单：仅允许从 micro-TBox 自动生成的规则，不接受运行时注入

**缓存策略**：
- N3 规则字符串：按 `chunk_ids` 排序后的 hash 缓存
- 推理结果图：按 `(chunks_hash, instance_facts_hash)` 缓存，TTL 5-15 分钟
- canonical_map：sidecar 启动时全表缓存，订阅 PostgreSQL LISTEN/NOTIFY 增量更新

### 7.10 docker-compose 集成

[docker-compose.yml](docker-compose.yml) 新增服务（仿 docreader 配置）：

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

启用方式：`docker-compose --profile neo4j --profile ontology up -d`

### 7.11 工程注意事项

1. **N3 解析有边界 case**：dotNetRDF 的 `Notation3Parser` 对嵌套图模式支持有限。**生成时只用最简语法** `{ pattern } => { pattern } .`，避免高级 N3 特性
2. **String-based rules 优于 Triple-based**：用字符串拼 N3 规则比用 API 构造规则三元组简单一个量级，调试也容易（直接看生成的规则文本）
3. **`InMemoryDataset` 比 `Graph` 性能更好**：超过 10K 三元组时改用 `InMemoryDataset` + `LeviathanQueryProcessor`
4. **Fixpoint 不要靠 `Triples.Count`**：极少数情况下 N3 应用会同时增删，用三元组集合的 hash 比较更稳
5. **AOT 与反射**：`System.Text.Json` AOT 模式需要 source generators；dotNetRDF 自身有反射依赖，建议先用 ReadyToRun 模式过渡，AOT 留作后期优化
6. **PostgreSQL 连接池**：Npgsql 默认连接池适合 sidecar 长驻场景，配置 `Maximum Pool Size=20` 一般够

---

## 8. Agent 工具集成

### 8.1 新增 `ontology_reason` 工具【新增】

新建 `internal/agent/tools/ontology_reason.go`，仿照 [query_knowledge_graph.go](internal/agent/tools/query_knowledge_graph.go) 的结构：

```go
var ontologyReasonTool = BaseTool{
    name: ToolOntologyReason,
    description: `Perform logical reasoning over a slice of the knowledge graph.

## Core Function
Combines local ontology fragments (micro-TBoxes) from retrieved chunks and runs
a reasoner to derive logically entailed facts.

## When to Use
✅ Use for:
- Transitive queries ("all descendants of X", "all parts of Y")
- Subsumption queries ("is X a kind of Y?")
- Constraint validation ("does instance X satisfy the schema?")
- Negation/disjointness queries ("can X be both A and B?")

❌ Don't use for:
- Fuzzy semantic search → use knowledge_search
- Simple entity lookup → use query_knowledge_graph
- Free-text exploration → use knowledge_search

## Parameters
- knowledge_base_ids (required): Target KB IDs
- query (required): Natural language question OR SPARQL query
- reasoner_profile (optional): "rdfs" | "n3-extended" | "shacl", default "n3-extended"
- include_evidence (optional): bool, return source chunk evidence

## Workflow
1. Hybrid retrieval pre-filter (chunk召回)
2. Pull micro-TBoxes from retrieved chunks
3. Canonical name alignment
4. Reasoner sidecar call
5. Format result with provenance`,
    schema: utils.GenerateSchema[OntologyReasonInput](),
}

type OntologyReasonInput struct {
    KnowledgeBaseIDs []string `json:"knowledge_base_ids"`
    Query            string   `json:"query"`
    ReasonerProfile  string   `json:"reasoner_profile,omitempty"` // rdfs | n3-extended | shacl
    IncludeEvidence  bool     `json:"include_evidence,omitempty"`
}
```

工具注册：[internal/agent/tools/definitions.go](internal/agent/tools/definitions.go) 添加 `ToolOntologyReason` 常量。

### 8.2 工具路由策略

Agent 在 [internal/agent/prompts.go](internal/agent/) 系列的 system prompt 里加一节工具选择指南：

```
When the user asks structural, constraint-checking, or transitive-relation questions,
prefer ontology_reason. When in doubt, use knowledge_search for retrieval first, then
ontology_reason for reasoning. Avoid using ontology_reason for fuzzy text search.
```

由于工具描述足够清晰，可以让 Agent 自主路由，无需硬编码规则。

---

## 9. 跨切片对齐

### 9.1 问题

切片式 ontology 的代价：同一概念在不同 chunk 中被命名成不同 IRI。

例：
- chunk_42 抽出 `Engineer`
- chunk_88 抽出 `软件工程师`
- chunk_153 抽出 `SoftwareEngineer`

实际上是同一个 class。如果不对齐，推理时这三个 chunk 的事实无法连通。

### 9.2 三级对齐策略

| 级别 | 时机 | 代价 | 准确率 |
|------|------|------|--------|
| Tier 1 | LLM 抽取时（已在 prompt 中要求） | 0（内嵌） | 中 |
| Tier 2 | 推理 sidecar merger 阶段 | 低（字符串归一 + alias 索引） | 中高 |
| Tier 3 | 离线对齐 job（可选） | 高（embedding + LLM 判定） | 高 |

**Tier 1**：LLM 抽取 micro-TBox 时已要求填 `aliases` 字段（参见第 6 节 prompt）。这是基础。

**Tier 2**：sidecar 加载 chunk 时，把所有 chunk 的 `aliases` 汇总，构建 alias → canonical_id 反向索引。merge 阶段所有 class/property id 先过这张索引归一。

**Tier 3**（可选）：定期跑离线对齐 job：
```
1. 拉取近期所有新 chunk 的 class/property id
2. 计算 embedding（复用现有 EmbeddingService）
3. 聚类：相似度 > 阈值的归为候选同义组
4. LLM 二次判定（"X 和 Y 在领域 D 中是否指代同一概念？"）
5. 高置信度组写入 ontology_canonical_map 表
```

### 9.3 canonical_map 维护

存储：第 5.4 节定义的 `ontology_canonical_map` 表。

更新策略：

- 入库时：graph builder 通过注入的 `interfaces.CanonicalMapRepository` upsert 新 chunk 的 alias 到表，`aliases` 和 `source_chunks` 都做 union 去重；现有置信度不被更低值覆盖
- 查询时：sidecar 启动时缓存全表（每 KB 通常 < 10K 条，内存够）
- 离线对齐：Tier 3 job 写入，覆盖或合并 alias 字段（Core MVP 不实现）

---

## 10. 部署与配置

### 10.1 环境变量

```bash
# 现有
NEO4J_ENABLE=true
NEO4J_URI=bolt://neo4j:7687
NEO4J_USERNAME=neo4j
NEO4J_PASSWORD=password

# 新增
ONTOLOGY_ENABLE=true
ONTOLOGY_REASONER_URL=http://ontology-reasoner:8090
ONTOLOGY_DEFAULT_PROFILE=n3-extended
ONTOLOGY_CONFIDENCE_THRESHOLD=0.3
ONTOLOGY_EXTRACT_MIN_ENTITIES=2
```

### 10.2 .env.example 扩展

[.env.example](.env.example) 增加段落（仿照现有 Neo4j 段）：

```env
# ===== Ontology Reasoning (Optional, .NET sidecar) =====
# Enable chunk-level micro-TBox extraction and reasoning sidecar
ONTOLOGY_ENABLE=false
ONTOLOGY_REASONER_URL=http://ontology-reasoner:8090
ONTOLOGY_DEFAULT_PROFILE=n3-extended
ONTOLOGY_CONFIDENCE_THRESHOLD=0.3
ONTOLOGY_EXTRACT_MIN_ENTITIES=2
```

### 10.3 数据迁移

新增三个迁移文件：
```
migrations/versioned/
  000057_chunk_ontology.up.sql        # chunk 表加字段
  000057_chunk_ontology.down.sql
  000058_ontology_canonical_map.up.sql
  000058_ontology_canonical_map.down.sql
```

执行顺序（必须）：
1. 先执行 000057_chunk_ontology（新增 chunks 承载字段）
2. 再执行 000058_ontology_canonical_map（新增跨切片对齐表）

### 10.4 配置文件

[config/config.yaml](config/config.yaml) 新增段落：

```yaml
ontology:
  enabled: ${ONTOLOGY_ENABLE:false}
  reasoner_url: ${ONTOLOGY_REASONER_URL:http://ontology-reasoner:8090}
  default_profile: ${ONTOLOGY_DEFAULT_PROFILE:n3-extended}
  confidence_threshold: ${ONTOLOGY_CONFIDENCE_THRESHOLD:0.3}
  extract_min_entities: ${ONTOLOGY_EXTRACT_MIN_ENTITIES:2}
```

---

## 11. 风险与权衡

### 11.1 已知风险

| 风险 | 影响 | 缓解 |
|------|------|------|
| LLM 抽 micro-TBox 时跨 chunk 命名漂移 | 同概念无法连通推理 | Tier 1+2 对齐；接受局部歧义 |
| 单 chunk 视角片面，公理不全 | 推理结果保守 | 这是 by-design：宁少勿错 |
| 切片合成时矛盾公理共存 | 推理可能不一致 | 检测到 inconsistency 返回 warning，不阻塞 |
| LLM 编造 evidence 字段 | 假公理混入 | 后验校验：evidence 必须是 chunk 原文子串 |
| Tier 3 对齐质量差 | 错误归一污染 | 高置信度阈值；保留原始 chunk 级 id 兜底 |
| 推理 sidecar 延迟 | Agent 响应慢 | 默认 n3-extended profile；超时降级到不推理 |
| 公理空间爆炸 | 切片合成时图太大 | 限制 chunk_ids 数量 ≤ 50 |

### 11.2 设计取舍

**取舍 1：放弃全局一致性，换取增量友好**
- 代价：跨 chunk 推理可能不完整
- 收益：新文档不需要触发全局 schema 重建

**取舍 2：JSON 中间表示而非直接 OWL**
- 代价：多一层后处理
- 收益：LLM 错误率显著降低；可逐条校验

**取舍 3：JSONB 存而非独立 triple store**
- 代价：查询时需反序列化
- 收益：复用现有 PostgreSQL；无额外运维成本
- 备选：未来可加 Jena Fuseki 作为只读副本，把 JSONB 物化进去

**取舍 4：N3 规则 + SHACL 而非 OWL DL reasoner**
- 代价：放弃完整 OWL DL 推理（一致性证明、复杂类表达式、nominal 推理）
- 收益：**完全摆脱 JVM 依赖**；性能可预测；用 dotNetRDF 成熟组件覆盖 85-90% RAG 场景
- 备选：未来通过 `dl` profile + Stardog/RDFox HTTP 客户端补足

**取舍 5：推理在查询时而非入库时**
- 代价：每次查询都要算
- 收益：推理范围可控；切片可灵活组合

---

## 12. 专家审核与反馈闭环【新增设计】

### 12.1 为什么纳入正式设计

切片式 ontology + LLM 抽取的方案在 §11.1 列出的三个风险——**LLM 跨 chunk 命名漂移**、**单 chunk 视角片面**、**LLM 编造 evidence**——共同点是**无法仅靠算法手段消除**，需要领域专家在循环中持续修正。没有审核闭环，ontology 质量会随知识库增长而单调下降。

本节描述一个**最小可用**的专家审核子系统设计，目标不是替代专家，而是让审核成本可控、把每次审核动作转化为**可复用的反馈信号**。

### 12.2 审核对象与操作矩阵

| 审核对象 | 接受 | 拒绝 | 编辑 | 合并 |
|---------|------|------|------|------|
| classes（含 subClassOf, disjointWith） | ✅ | ✅ | ✅ | ✅ 合并到 canonical id |
| properties（含 characteristics, domain/range） | ✅ | ✅ | ✅ | ✅ |
| shapes（SHACL 约束） | ✅ | ✅ | ✅ | — |
| aliases | ✅ | ✅ | ✅ | ✅ 灌入 canonical_map |
| axioms（自由形式） | ✅ | ✅ | ✅ | — |

**"合并" 是最有价值的操作**——把跨 chunk 的同义 class/property 收敛到一个 canonical id，**直接反哺 canonical_map**（§9.3），未来抽取自动复用。

### 12.3 数据流

```
[现有] 文档上传 → chunk 抽取 → chunk.ontology_json (raw LLM output)
                                       │
                                       ▼
                  ┌────────────────────────────────────┐
                  │  审核队列 (review_queue 表)        │
                  │  - 按优先级算法排序                │
                  │  - 高频实体 / 低置信度 / 冲突优先  │
                  └────────────────┬───────────────────┘
                                   │
                                   ▼
                  ┌────────────────────────────────────┐
                  │  专家审核 UI (Vue 前端)            │
                  │  - 单 chunk 视图 + evidence 高亮  │
                  │  - 跨 chunk 同义合并候选          │
                  │  - 批量操作                        │
                  └────────────────┬───────────────────┘
                                   │
                  ┌────────────────┼────────────────┐
                  ▼                ▼                ▼
        chunk.ontology_      canonical_map     audit_log
        json_reviewed        （主要反馈产物）  （追溯）
                  │                │
                  ▼                ▼
        优先用于推理         下次抽取作为
        （回退到 raw）       prompt 上下文
```

**关键设计**：raw LLM 输出**永不覆盖**，curated 版本独立存储。这样可以：
- 回滚错误的审核决策
- 对比 LLM vs 专家差异，沉淀为训练样本（§12.9）
- 切换默认源（raw / reviewed）做 A/B 实验

### 12.4 数据模型扩展【新增】

**chunks 表**（在 §4.5 字段基础上加）：

```sql
ALTER TABLE chunks
  ADD COLUMN ontology_json_reviewed   JSONB         DEFAULT NULL,
  ADD COLUMN ontology_review_status   TEXT          DEFAULT 'pending'
      CHECK (ontology_review_status IN
             ('pending', 'in_review', 'approved', 'rejected', 'no_review')),
  ADD COLUMN ontology_reviewed_by     BIGINT        REFERENCES users(id),
  ADD COLUMN ontology_reviewed_at     TIMESTAMPTZ;
```

**审核队列表**：

```sql
CREATE TABLE ontology_review_queue (
    id                BIGSERIAL PRIMARY KEY,
    tenant_id         BIGINT NOT NULL,
    knowledge_base_id TEXT NOT NULL,
    chunk_id          TEXT NOT NULL,
    priority          INT NOT NULL DEFAULT 50,   -- 0-100, 越大越优先
    priority_reason   TEXT,                       -- e.g. "high-freq entity: Engineer x17"
    assigned_to       BIGINT REFERENCES users(id),
    status            TEXT NOT NULL DEFAULT 'pending',
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, chunk_id)
);

CREATE INDEX idx_review_queue_prio ON ontology_review_queue
    (tenant_id, knowledge_base_id, status, priority DESC);
```

**审计日志**：

```sql
CREATE TABLE ontology_review_audit (
    id            BIGSERIAL PRIMARY KEY,
    tenant_id     BIGINT NOT NULL,
    chunk_id      TEXT NOT NULL,
    reviewer_id   BIGINT NOT NULL,
    action        TEXT NOT NULL,    -- accept / reject / edit / merge
    target_kind   TEXT NOT NULL,    -- class / property / shape / alias / axiom
    target_id     TEXT NOT NULL,
    before_value  JSONB,
    after_value   JSONB,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

### 12.5 优先级算法【新增】

审核成本可控的关键是**优先级排序**。所有 chunk 入队后按下列因子综合打分（0-100）：

| 因子 | 权重 | 计算方式 |
|------|------|---------|
| 实体频率 | 40 | log(被多少 chunk 引用同名 entity) × 系数 |
| 公理复杂度 | 20 | (classes + properties + axioms) 总数归一化 |
| LLM 置信度反向 | 20 | (1 - confidence) × 20 |
| 冲突检测命中 | 15 | §4.7 切片合成阶段触发 warning 的 chunk 优先 |
| 用户查询命中 | 5 | 最近 7 天被 Agent 检索命中的 chunk |

> 实现位置：[internal/application/service/ontology_review_priority.go](internal/application/service/ontology_review_priority.go)（新增）

**经验规律**：审核 top 5% 高优先级 chunk 通常能消除 60-70% 的 ontology 质量问题（80/20 原则的具体表现）。

### 12.6 UI 关键页面（前端）

**页面 1：审核队列**（reviewer dashboard）

```
┌────────────────────────────────────────────────────────────┐
│ Ontology Review Queue       Tenant: ACME       [Refresh]    │
├────────────────────────────────────────────────────────────┤
│ Priority │ Chunk      │ Why                       │ Action │
│   95     │ chunk_42   │ High-freq: Engineer × 17  │ Review │
│   82     │ chunk_88   │ Conflict warning          │ Review │
│   71     │ chunk_153  │ Low confidence 0.42       │ Review │
│   ...                                                        │
└────────────────────────────────────────────────────────────┘
```

**页面 2：单 chunk 审核**（reviewer 主工作区）

```
┌──────────────────────────────────────────────────────────────┐
│ Chunk_42  │  Source: docs/k8s-guide.md  │  [Approve All]    │
├──────────────────────────────────────────────────────────────┤
│ Original Text (evidence 高亮):                                │
│   "Kubernetes orchestrates [containers]. A [Pod] is a        │
│    group of one or more [containers]. Pods are the           │
│    smallest deployable units in Kubernetes."                 │
├──────────────────────────────────────────────────────────────┤
│ Extracted Classes:                                            │
│ ☑ Pod (subClassOf: DeployableUnit)         [✓][✗][Edit]     │
│ ☑ Container                                [✓][✗][Edit]     │
│ ⚠ DeployableUnit (only mentioned once)     [✓][✗][Edit]     │
├──────────────────────────────────────────────────────────────┤
│ Extracted Properties:                                         │
│ ☑ contains (Pod → Container, transitive: NO)  [✓][✗][E]    │
│   ⚠ Conflict: chunk_88 marks as transitive=YES               │
│   [Merge with chunk_88's view] [Keep both]                   │
├──────────────────────────────────────────────────────────────┤
│ Alias Suggestions:                                            │
│ • "K8s" → "Kubernetes"   [Add to canonical_map]              │
│ • "容器" → "Container"    [Add to canonical_map]              │
└──────────────────────────────────────────────────────────────┘
```

**页面 3：跨 chunk 合并候选**（最高 ROI 的页面）

```
┌──────────────────────────────────────────────────────────────┐
│ Concept Merge Candidates                                       │
├──────────────────────────────────────────────────────────────┤
│ Candidate group #1 (similarity 0.94):                         │
│   • Engineer          (chunks: 42, 88, 153, ...)              │
│   • 工程师             (chunks: 67, 91)                        │
│   • SoftwareEngineer  (chunks: 201)                           │
│   Choose canonical: [Engineer ▾]  [Merge All]                 │
└──────────────────────────────────────────────────────────────┘
```

候选组由 §9.2 Tier 3 离线对齐 job 自动生成，UI 只做最后裁定。

### 12.7 后端接口（Go）【新增】

新建 [internal/handler/ontology_review.go](internal/handler/ontology_review.go)：

```go
// 获取审核队列（按优先级排序）
GET  /api/v1/tenants/{tid}/ontology/review/queue?status=pending&limit=20

// 获取单 chunk 的完整 micro-TBox + evidence 高亮范围
GET  /api/v1/tenants/{tid}/ontology/review/chunks/{chunkId}

// 提交单条审核动作（accept/reject/edit）
POST /api/v1/tenants/{tid}/ontology/review/chunks/{chunkId}/actions

// 跨 chunk 合并：写 canonical_map
POST /api/v1/tenants/{tid}/ontology/review/merge
{
  "canonical_id": "Engineer",
  "alias_ids":    ["软件工程师", "SoftwareEngineer"],
  "kind":         "class"   // or "property"
}

// 批量 approve 整个 chunk 的全部抽取
POST /api/v1/tenants/{tid}/ontology/review/chunks/{chunkId}/approve_all
```

权限：复用现有 [internal/middleware/rbac.go](internal/middleware/rbac.go)，新增三个权限点：
- `ontology:review` —— 单 chunk 审核
- `ontology:merge` —— 跨 chunk 合并（更高权限）
- `canonical_map:write` —— 直接编辑全局对齐表（管理员）

### 12.8 前端集成

参考 [frontend/src/views/knowledge/wiki/WikiBrowser.vue](frontend/src/views/knowledge/wiki/WikiBrowser.vue) 的模式新建：

```
frontend/src/views/knowledge/ontology/
├── OntologyReviewQueue.vue        # 审核队列页
├── OntologyChunkReview.vue        # 单 chunk 审核
├── OntologyMergeCandidates.vue    # 跨 chunk 合并
└── components/
    ├── EvidenceHighlight.vue      # 原文 + evidence 高亮组件
    ├── AxiomCard.vue               # 单条公理卡片（accept/reject/edit）
    └── MergeDialog.vue
```

路由：`/knowledge/{kbId}/ontology/review`

### 12.9 反馈给抽取管线（关键闭环）

审核结果有两条反馈路径，**短期路径必须实现**，长期路径作为数据资产沉淀：

**路径 A：直接喂 prompt（短期，必做）**

参考现有 [WikiKnowledgeExtractPrompt 的 slug 复用机制](internal/agent/prompts_wiki.go)，在 §6.1 的 micro-TBox prompt 里新增一段：

```
<previous_canonical_ids>
{{.PreviousCanonicalIds}}   <!-- 来自 canonical_map -->
</previous_canonical_ids>

<instructions>
... existing instructions ...

If a class/property in the current text matches one of the previous canonical
ids (or its known aliases), **reuse that exact id**. Do NOT generate a new id
for an existing concept.
</instructions>
```

这把审核结果**直接反哺到下一次抽取**，避免重复犯同一个命名漂移错误——本系统能否长期工作的关键正反馈环。

**路径 B：训练数据沉淀（长期，可选）**

审核动作（accept/reject/edit/merge）天然就是**高质量标注样本**，未来可用于：
- 微调 entity / relation 抽取专用模型
- 评估 LLM 抽取质量的回归测试集
- prompt engineering 的 A/B 实验 ground truth

存储位置：[ontology_review_audit](#124-数据模型扩展新增) 表已经记录所有动作，按需 ETL 即可。

### 12.10 KPI 与可观测性

| 指标 | 目标 | 计算方式 |
|------|------|---------|
| 审核覆盖率 | top 5% 优先级 chunk 全覆盖 | 已审核 chunk 数 / 入队 chunk 数 |
| 接受率 | 70% < x < 95% | accept 动作 / 总动作 |
| 平均审核时长 | < 90s / chunk | UI 埋点 |
| canonical_map 增长率 | 前 3 月 > 5%/周，之后稳定 | new canonical_id / 周 |
| 跨 chunk 命名漂移率 | 单调下降 | 同概念不同 id 的频率 |

**两端报警**：
- 接受率 < 50% → LLM prompt 需要迭代
- 接受率 > 95% → 审核可能在走过场，需要抽样人工复核
- canonical_map 增长率 0 → 审核员只在做单 chunk 接受，不做合并（最有价值的动作被忽略）

### 12.11 演进路径

| 阶段 | 时长 | 内容 |
|------|------|------|
| MVP | 2 周 | 单 chunk 审核（accept/reject/edit），不做合并；验证流程能跑通 |
| Phase 2 | 1 月 | 加跨 chunk 合并 UI + canonical_map 反馈到 prompt（路径 A） |
| Phase 3 | 2 月 | KPI dashboard + 优先级算法调优 + 审计日志查询 |
| Phase 4 | 后续 | 审核动作沉淀为训练数据（路径 B），定期再训 prompt |
| 长期 | — | 多审核员仲裁机制（一致性 voting）、Active Learning（让模型挑最值得审的样本） |

### 12.12 风险与设计取舍

| 风险 | 缓解 |
|------|------|
| 审核员疲劳 | 优先级队列 + top 5% 策略；UI 设计追求"3 秒决策"（一眼看完 → 一键操作） |
| 审核员之间标准不一 | Phase 4 引入多人仲裁；MVP 阶段先培训 + 文档化标注规范 |
| 反馈延迟影响推理质量 | 接受局部歧义（§9.2 Tier 2 兜底），审核是异步质量提升，不阻塞推理 |
| canonical_map 错误归一污染 | 审计日志可回滚；canonical_map 操作需 `canonical_map:write` 高权限 |
| 审核员主观偏见 | 记录所有动作；定期抽样多人复核；高接受率自动报警 |

**最关键的设计原则**：审核**不应该**成为系统的同步瓶颈。它是异步质量提升管道，即使审核员全部下线 1 个月，系统仍能用 raw micro-TBox 工作，只是质量曲线趋平而非上升。

---

## 13. 混合推理（Neuro-Symbolic）【新增设计】

### 13.1 为什么纳入正式设计

§7 的 dotNetRDF + N3 + SHACL 三引擎是**纯符号推理**——确定性、可证明、可解释，但**僵硬**：

- 自然语言查询不能直接喂给 reasoner
- SPARQL 表达不了"软关联"（"和 X 类似"、"重要的"、"通常"）
- SHACL 违反时只报错，不会建议修复
- 推理结果是三元组集，需要人去读

LLM 恰恰相反——**柔软**但不可证明：

- 理解模糊语义、生成解释、容忍部分信息
- 但容易遗漏传递关系；幻觉率非零；无法做形式化证明

**混合推理的本质是分工**：用 LLM 包住符号推理器，让两者各自做擅长的事，**互相校验对方的弱点**。这不是新算法，是工程编排模式。

### 13.2 任务分工矩阵【设计依据】

| 任务类型 | LLM | 符号 Reasoner | 协作方式 |
|---------|-----|--------------|---------|
| 自然语言 → SPARQL | ✅ | ❌ | LLM 翻译，Reasoner 执行（**模式 1**） |
| 传递闭包推理（多跳） | ❌ 易遗漏 | ✅ | Reasoner 主导 |
| 模糊语义召回（"类似/重要"） | ✅ | ❌ | LLM 主导，Reasoner 校验（**模式 2**） |
| SHACL 约束违反检测 | ❌ 漏检 | ✅ | Reasoner 主导 |
| 违反修复建议 | ✅ | ❌ | LLM 提议，Reasoner 验证（**模式 3**） |
| 推理结果 → 自然语言 | ✅ | ❌ | LLM 后处理 |
| 共指消解（"它"指代谁） | ✅ | ❌ | LLM 主导 |
| 反事实推理（"如果 X，那么…"） | △ | ✅ | LLM 提案，Reasoner 校验（**模式 4**） |
| 一致性证明 | ❌ | ✅ | Reasoner 主导 |
| 多步骤推理（链式） | △ | △ | 交错（**模式 5: Reasoning Tape**） |

`✅` = 强项；`❌` = 弱项；`△` = 中等（可做但不可靠）

**核心原则**：**LLM 永远不能直接推出"真"**，它只能 **propose**；**所有最终结论必须经过 Reasoner 校验**。这是反幻觉的最强约束。

### 13.3 双引擎循环架构

```
              ┌──────────────────────┐
              │  User Query (NL)     │
              └──────────┬───────────┘
                         │
                ┌────────▼────────┐
                │  Agent Router   │  ← Agent prompt 决定走哪条路径
                └────────┬────────┘
                         │
              ┌──────────┼──────────────────┐
              ▼          ▼                  ▼
        Pure Vector  Pure Graph        Hybrid Ontology
        (knowledge_  (query_kg)        (ontology_reason
         search)                        with mode=hybrid)
                                              │
            ┌─────────────────────────────────┘
            │
            ▼
   ┌────────────────────┐
   │ LLM Pre-processor  │  ← NL → SPARQL / SHACL / 候选三元组
   │ (translator)       │     (1 次 LLM 调用)
   └─────────┬──────────┘
             │
             ▼
   ┌────────────────────┐
   │ Symbolic Reasoner  │  ← dotNetRDF: RDFS + N3 + SHACL
   │ (dotNetRDF)        │     (< 300ms, 见 §7.9)
   └─────────┬──────────┘
             │
             ▼
   ┌────────────────────┐
   │ LLM Post-processor │  ← 结果 → NL 解释 / 修复建议
   │ (explainer/repair) │     (1 次 LLM 调用)
   └─────────┬──────────┘
             │
       [置信度低 / SHACL violation /
        用户追问] ───YES───→ 回到 Pre-processor
             │                  最多迭代 N 次
             │ NO
             ▼
        Final Answer
        + provenance (chunks + LLM trace)
```

**默认每次 hybrid 查询消耗 2 次 LLM 调用 + 1 次 Reasoner 调用**，必要时迭代到 3-4 次。延迟 1-3s，成本 $0.001-0.01（取决于模型）。

### 13.4 五种核心模式【核心规约】

#### 模式 1：NL → SPARQL → NL（最常用，必做）

LLM 把自然语言翻译成 SPARQL，Reasoner 执行，LLM 把结果翻回自然语言。

```csharp
public class NlSparqlPattern
{
    private readonly IChatModel _llm;
    private readonly ReasoningEngine _reasoner;

    public async Task<HybridResult> RunAsync(
        string userQuery,
        AssembledSlice slice,
        CancellationToken ct)
    {
        // 1. LLM 翻译：把可用的 class/property 词表喂给 LLM
        var vocab = ExtractVocab(slice);
        var sparql = await _llm.CompleteAsync(
            PromptBuilder.NlToSparql(userQuery, vocab), ct);

        // 2. SPARQL 后验校验（拒绝引用未知 IRI）
        if (!SparqlGuard.IsSafe(sparql, vocab))
            return HybridResult.Rejected("SPARQL references unknown vocabulary");

        // 3. Reasoner 执行（dataGraph 来自 slice 中的实例三元组，schemaGraph 来自合成的 TBox）
        var dataGraph = slice.BuildDataGraph();
        var inferred = _reasoner.Reason(
            dataGraph,
            slice.SchemaGraph,
            n3Rules: "", maxIterations: 5);
        var rows = (SparqlResultSet)inferred.ExecuteQuery(sparql);

        // 4. LLM 解释
        var nl = await _llm.CompleteAsync(
            PromptBuilder.ResultsToNl(userQuery, rows), ct);

        return new HybridResult(nl, sparql, rows, slice.Provenance);
    }
}
```

**关键防线**：`SparqlGuard.IsSafe` 检查 LLM 生成的 SPARQL 只能引用已抽取的 class/property IRI，防止 LLM 自创不存在的概念。

#### 模式 2：软关联召回 + Reasoner 校验

用户问"和 Engineer 类似的角色"——SPARQL 没法表达"类似"。让 LLM 提候选，再用 Reasoner 校验候选确实存在于图里。

```csharp
public async Task<List<Entity>> SoftRelatedAsync(
    string anchor, AssembledSlice slice, int topK)
{
    // 1. LLM 提候选（基于 ontology 词表 + embedding）
    var candidates = await _llm.SuggestSimilarAsync(anchor, slice.AlignedTBoxes);

    // 2. Reasoner 过滤：候选必须在图里且可达 anchor
    var verified = new List<Entity>();
    foreach (var c in candidates)
    {
        var ask = $@"
            ASK {{
                <urn:weknora:class/{c.Id}> rdfs:subClassOf* ?common .
                <urn:weknora:class/{anchor}> rdfs:subClassOf* ?common .
            }}";
        if ((bool)slice.SchemaGraph.ExecuteQuery(ask))
            verified.Add(c);
    }

    return verified.Take(topK).ToList();
}
```

#### 模式 3：SHACL 违反 → LLM 修复建议

Reasoner 报"Engineer 缺 hasSkill 关系"——LLM 看 chunk 原文，找隐含证据。

```csharp
public async Task<RepairSuggestion> SuggestRepairAsync(
    ValidationReport report, string chunkContent)
{
    if (report.Conforms) return RepairSuggestion.None;

    var violation = report.Results.First();
    var prompt = PromptBuilder.RepairFromText(
        violation.Message, chunkContent);

    var suggestion = await _llm.CompleteAsync(prompt);
    // suggestion 形如: {"propose_triple": [...], "evidence": "...", "confidence": 0.7}

    return suggestion.Confidence >= 0.6
        ? RepairSuggestion.Tentative(suggestion)
        : RepairSuggestion.Unresolved;
}
```

修复建议**不直接写库**——回到 §12 的专家审核队列，由人裁定。

#### 模式 4：反事实推理（What-If）

用户问"如果把 X 类移除，谁会失去类型？"。LLM 解析意图，Reasoner 在副本图上跑差异推理。

```csharp
public async Task<CounterfactualResult> WhatIfAsync(
    string hypothesis, AssembledSlice slice)
{
    var (delta, rationale) = await _llm.ParseHypothesisAsync(hypothesis);

    var modifiedSlice = slice.Clone();
    delta.ApplyTo(modifiedSlice.SchemaGraph);

  var originalData = slice.BuildDataGraph();
  var modifiedData = modifiedSlice.BuildDataGraph();
  var originalRules = slice.BuildN3Rules();
  var modifiedRules = modifiedSlice.BuildN3Rules();

  var originalInferred = _reasoner.Reason(originalData, slice.SchemaGraph, originalRules);
  var modifiedInferred = _reasoner.Reason(modifiedData, modifiedSlice.SchemaGraph, modifiedRules);

    var added = modifiedInferred.Triples.Except(originalInferred.Triples);
    var removed = originalInferred.Triples.Except(modifiedInferred.Triples);

    var explanation = await _llm.ExplainDiffAsync(added, removed, rationale);
    return new CounterfactualResult(added, removed, explanation);
}
```

#### 模式 5：Reasoning Tape（多步交错，最强但最贵）

LLM 和 Reasoner 交替执行步骤，类似 ReAct，但"action"换成正式 reasoner 调用。适用于多跳推理。

```
Round 1: LLM("先找 A 直接管理的所有人")
       → Reasoner SPARQL → ["B", "C"]
Round 2: LLM("再找 B 和 C 各自管的人")
       → Reasoner SPARQL → ["D", "E", "F"]
Round 3: LLM("再找 D, E, F 向谁汇报")
       → Reasoner SPARQL → ["A"]
Round 4: LLM("已收敛，回答用户")
       → "A 间接管理 D/E/F，他们最终汇报回 A——这是个环"
```

```csharp
public async Task<TapeResult> RunTapeAsync(
    string userQuery, AssembledSlice slice, int maxRounds = 5)
{
    var tape = new List<TapeEntry>();
    string currentGoal = userQuery;

    for (int round = 0; round < maxRounds; round++)
    {
        var step = await _llm.PlanNextStepAsync(currentGoal, tape);
        if (step.IsTerminal) break;

        var queryResult = step.Kind switch
        {
            "sparql"  => RunSparql(slice, step.Body),
            "shacl"   => RunShacl(slice, step.Body),
            "assert"  => AssertTentative(slice, step.Body),
            _ => throw new InvalidOperationException()
        };

        tape.Add(new TapeEntry(step, queryResult));
        currentGoal = step.NextGoal ?? currentGoal;
    }

    var finalAnswer = await _llm.SynthesizeAsync(userQuery, tape);
    return new TapeResult(finalAnswer, tape);
}
```

**成本**：N 轮 = N+1 次 LLM 调用 + N 次 Reasoner 调用。默认 `maxRounds=3`，复杂查询可放宽到 5-7。

### 13.5 实现位置（与现有代码集成）

| 模块 | 位置 | 改动 |
|------|------|------|
| Agent 工具扩展 | [internal/agent/tools/ontology_reason.go](internal/agent/tools/ontology_reason.go) | 增加 `mode: "symbolic" \| "hybrid" \| "tape"` 参数 |
| Sidecar 端点扩展 | `WeKnora.OntologyReasoner.Api/Endpoints/HybridEndpoint.cs`（新增） | 实现五种模式的 dispatcher |
| LLM 调用层 | `WeKnora.OntologyReasoner.Core/Llm/`（新增子包） | 抽象 `IChatModel` 接口，复用 §6 的同一个 LLM 配置 |
| Prompt 模板 | [config/prompt_templates/](config/prompt_templates/) | 新增 `hybrid_reasoning.yaml`：NL→SPARQL / 解释 / 修复建议三个模板 |
| Vocabulary 守卫 | `WeKnora.OntologyReasoner.Core/Guards/SparqlGuard.cs`（新增） | 后验校验 LLM 生成的 SPARQL 只引用合法词表 |

**关键设计**：sidecar 自己拥有一个 `IChatModel` 客户端，**不是**通过回调 Go 主项目去调 LLM。理由：

- 减少跨语言调用的延迟（hybrid 模式对延迟敏感）
- sidecar 自治，可独立扩缩容
- 复用 Go 侧的 LLM 配置（通过环境变量 / 配置文件共享）

### 13.6 API 扩展

§7.8 的 API 增加 `mode` 字段和 `tape` 输出：

```
POST /reason

Request:
{
  "tenant_id": 123,
  "knowledge_base_ids": ["kb_abc"],
  "chunk_ids": ["chunk_42", "chunk_88"],
  "instance_facts": [...],
  "query": {
    "type": "natural_language",        // 新增类型
    "body": "工程师 A 管理的所有人都向谁汇报？",
    "mode": "hybrid" | "tape" | "symbolic",   // 新增
    "max_rounds": 3                    // 仅 tape 模式
  }
}

Response (mode = "hybrid"):
{
  "status": "ok",
  "answer": "A 间接管理 D/E/F，他们汇报回 A——这是个环",
  "sparql_used": "SELECT ?p WHERE { :A :manages+ ?x . ?x :reportsTo ?p . }",
  "inferred_triples": [...],
  "evidence_chunks": ["chunk_42", "chunk_88"],
  "llm_calls": 2,
  "reasoner_calls": 1,
  "elapsed_ms": 1240
}

Response (mode = "tape"):
{
  ...
  "tape": [
    {"round": 1, "kind": "sparql", "body": "...", "result_summary": "..."},
    {"round": 2, "kind": "sparql", "body": "...", "result_summary": "..."},
    ...
  ],
  "llm_calls": 4,
  "reasoner_calls": 3
}
```

### 13.7 缓存与成本控制

混合推理的成本主要在 LLM 调用，必须有缓存层：

| 缓存对象 | Key | TTL | 命中场景 |
|---------|-----|-----|---------|
| NL → SPARQL 翻译 | (slice_hash, query_text) | 1 h | 同一切片同一查询 |
| 结果 → NL 解释 | (sparql, result_hash) | 24 h | 同样结果不同人问 |
| Reasoning Tape 中间步 | (tape_hash + step) | 1 h | 长链查询的部分前缀 |
| 修复建议 | (violation, chunk_id) | 7 d | SHACL 同一违反 |

**降级策略**：

- 缓存命中 → 直接返回（< 50ms）
- LLM 调用失败 → 降级到 `mode=symbolic`（仍可用，只是不解释）
- 总成本超阈值 → 该 chunk 该日不再走 hybrid，落回符号

### 13.8 风险与设计取舍

| 风险 | 缓解 |
|------|------|
| LLM 生成非法 SPARQL（引用不存在 IRI） | `SparqlGuard` 后验校验，词表锁定 |
| LLM 错译 NL → SPARQL | 关键查询要求 user confirmation；记录 sparql_used 供回溯 |
| 多轮 LLM 累积幻觉 | Reasoning Tape 每步必经 Reasoner 校验；步数硬上限 |
| 延迟翻倍 / 三倍 | 缓存层 + 默认 hybrid 关闭，仅在用户显式或 Agent 判断需要时启用 |
| LLM 调用成本失控 | 租户级 LLM budget + 单查询成本上限 + 阈值降级 |
| 修复建议被滥用 | 永远不直接写库，必须走 §12 专家审核 |

**核心取舍**：

- **取舍 1：默认 symbolic，hybrid 显式启用**——避免每次都吃 LLM 成本；symbolic 已能覆盖大部分查询
- **取舍 2：sidecar 内嵌 LLM 客户端**——延迟优先于代码复用；接受 LLM 配置在两处维护
- **取舍 3：所有 LLM 提议必经 Reasoner 校验**——绝不让 LLM 直接产出"事实"
- **取舍 4：Reasoning Tape 默认上限 3 轮**——更长链路用 Active Learning 或专家审核兜底

### 13.9 演进路径

| 阶段 | 时长 | 内容 |
|------|------|------|
| MVP | 1 周 | 仅模式 1（NL→SPARQL→NL）+ `SparqlGuard` + 基础缓存 |
| Phase 2 | 2 周 | 加模式 2（软关联）+ 模式 3（修复建议接入 §12 审核队列） |
| Phase 3 | 1 月 | 加模式 4（反事实）+ 模式 5（Reasoning Tape） |
| Phase 4 | 2 月 | LLM budget 系统 + 自适应 mode 选择（Agent 自动判断该不该用 hybrid） |
| 长期 | — | LoRA 微调小模型专门做 NL→SPARQL，降本 5-10x |

### 13.10 与现有模块的关系

```
              ┌─────────────────────────────────┐
              │  本节 §13: Hybrid Reasoning     │
              └────────────────┬────────────────┘
                               │ 依赖
       ┌───────────────────────┼───────────────────────┐
       ▼                       ▼                       ▼
  §7 Sidecar             §12 Expert Review        §6 Prompt 体系
  (Reasoner 引擎)        (修复建议落地)            (新增模板)
       │                       │                       │
       │                       ▼                       │
       │              §9 Canonical Map ◄──────────────┘
       │                       │
       └───────► §4.7 Slice 合成 ◄────────────────────
                       │
                       ▼
                  Agent ToolBox: ontology_reason
```

**§13 不替代任何现有模块，它是把现有模块用 LLM 串起来的"指挥层"**。最低成本启用方式：在 sidecar 加 LLM 客户端 + 改 Agent 工具的描述，无需改动 §1-§12 的任何核心数据流。

---

## 14. TBox 演进治理（Ontology Lifecycle Management）【新增设计】

### 14.1 为什么纳入正式设计

切片式 ontology 的最大优势是**增量友好**，但代价是缺少全局一致性。随着 KB 累积 1000+ chunk 之后会出现三个新问题：

- **重复计算**：同一概念被抽取数百次，每次推理都要重新合并切片 + 对齐——浪费成本
- **审核成果被局部化**：§12 专家审核认证过的概念只影响那个 chunk，不能阻止其他 chunk 重复犯错
- **缺少"权威"概念**：高频且稳定的概念应该享有"事实标准"地位，而不是每次都参与切片投票

**TBox 演进治理 = chunk-local 微观本体 → KB-global 宏观本体 → 上层共享本体 的三级进阶。**

### 14.2 三级 TBox 架构

```
┌─────────────────────────────────────────────────────────┐
│  Tier 0 (外部引入，未来扩展): Upper Ontology              │
│  - schema.org / FOAF / Wikidata / 行业本体               │
│  - 只读，作为 promotion 目标                              │
└────────────────────────────┬────────────────────────────┘
                             │ 延伸 / 复用
┌────────────────────────────▼────────────────────────────┐
│  Tier 1 (本节新增): KB-Global TBox                       │
│  - 表 kb_global_tbox + 版本化                            │
│  - 专家审核认证 + 高频自动晋升                            │
│  - 享有"权威"地位：与之冲突的 chunk-local 公理被忽略       │
└────────────────────────────┬────────────────────────────┘
                             │ 引用 / 复用
┌────────────────────────────▼────────────────────────────┐
│  Tier 2 (§4.5 已有): Chunk-Local micro-TBox             │
│  - chunks.ontology_json                                  │
│  - LLM 抽取，可矛盾，可演化                               │
│  - 高频成员是 Tier 1 的候选源                             │
└─────────────────────────────────────────────────────────┘
```

### 14.3 数据模型扩展【新增】

```sql
-- KB 级全局 TBox 存储
CREATE TABLE kb_global_tbox (
    id                   BIGSERIAL PRIMARY KEY,
    tenant_id            BIGINT NOT NULL,
    knowledge_base_id    TEXT NOT NULL,
    kind                 TEXT NOT NULL
                         CHECK (kind IN ('class', 'property', 'shape', 'axiom')),
    canonical_id         TEXT NOT NULL,
    definition           JSONB NOT NULL,        -- 完整的 ClassDecl/PropertyDecl 等
    version              INT NOT NULL DEFAULT 1,
    status               TEXT NOT NULL DEFAULT 'draft'
                         CHECK (status IN ('draft', 'active', 'deprecated', 'retired')),
    promoted_from_chunks TEXT[] NOT NULL DEFAULT '{}',   -- 哪些 chunk 贡献了证据
    promoted_by          BIGINT REFERENCES users(id),    -- 谁批准
    promoted_at          TIMESTAMPTZ,
    deprecated_at        TIMESTAMPTZ,
    deprecation_note     TEXT,
    parent_tbox_id       BIGINT REFERENCES kb_global_tbox(id),  -- 继承层级
    UNIQUE (tenant_id, knowledge_base_id, kind, canonical_id, version)
);

CREATE INDEX idx_global_tbox_active ON kb_global_tbox
    (tenant_id, knowledge_base_id, kind)
    WHERE status = 'active';

-- TBox 演进事件审计
CREATE TABLE tbox_evolution_log (
    id            BIGSERIAL PRIMARY KEY,
    tenant_id     BIGINT NOT NULL,
    tbox_id       BIGINT REFERENCES kb_global_tbox(id),
    event_type    TEXT NOT NULL,    -- promote / deprecate / version_bump / migrate / activate
    before_value  JSONB,
    after_value   JSONB,
    actor         BIGINT REFERENCES users(id),
    reason        TEXT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

### 14.4 Promotion 判定算法【新增】

一个 chunk-local 概念什么时候应该 promote 到 KB-global？三种触发路径：

**路径 A：自动晋升（统计驱动）**

需**同时**满足：

1. **频率**：被 ≥ N 个 chunk 引用（N = max(20, KB_size × 1%)）
2. **稳定性**：同一 `canonical_id` 已稳定 ≥ 14 天（无被合并/拆分动作）
3. **一致性**：跨 chunk 的 `disjointWith` / `subClassOf` 主张矛盾率 < 10%
4. **审核率**：被审核过的 chunk 中 accept 率 ≥ 80%

满足后自动入 `draft` 状态，**等专家手动 activate**（不自动激活）。

**路径 B：审核员手动晋升**

§12 专家审核 UI 加按钮 "Promote to Global"。专家在单 chunk 审核时看到一个高频或重要概念，可一键触发。

**路径 C：基于 query trace 的拉动**

近 30 天被 Agent SPARQL 查询命中率最高的 N 个 class/property 强制进入 promotion 候选队列——**用户实际在用的概念优先沉淀**。

```csharp
public class PromotionEvaluator
{
    public async Task<List<PromotionCandidate>> EvaluateAsync(
        long tenantId, string kbId, CancellationToken ct)
    {
        var candidates = new List<PromotionCandidate>();

        // Path A: 自动判定
        var aggregates = await _repo.AggregateChunkConcepts(tenantId, kbId, ct);
        foreach (var concept in aggregates)
        {
            if (concept.ChunkFrequency >= GetThreshold(kbId) &&
                concept.StableDays      >= 14 &&
                concept.ConflictRate    <  0.1 &&
                concept.AcceptanceRate  >= 0.8)
            {
                candidates.Add(new PromotionCandidate(
                    Kind:        concept.Kind,
                    CanonicalId: concept.Id,
                    Source:      PromotionSource.Auto,
                    Score:       concept.PromotionScore,
                    Evidence:    concept.SourceChunks));
            }
        }

        // Path C: query trace
        var topQueried = await _repo.GetTopQueriedConcepts(
            tenantId, kbId, days: 30, top: 50);
        foreach (var concept in topQueried
            .Where(c => !candidates.Any(x => x.CanonicalId == c.Id)))
        {
            candidates.Add(new PromotionCandidate(
                Kind:        concept.Kind,
                CanonicalId: concept.Id,
                Source:      PromotionSource.QueryTrace,
                Score:       concept.QueryFrequency,
                Evidence:    concept.SourceChunks));
        }

        return candidates.OrderByDescending(c => c.Score).ToList();
    }
}
```

### 14.5 全局 TBox 与 chunk-local 的交互

**优先级规则**：active 状态的全局公理 > chunk-local 公理。查询时合并逻辑：

```
        Query 时间触发切片合成 (§4.7)
                    ↓
        chunk-local TBoxes (Tier 2)
                    ↓
        ┌───────────────────────────┐
        │ 拉取 KB-global TBox       │
        │ (active 状态)              │
        └───────────────┬───────────┘
                        ↓
        ┌───────────────────────────┐
        │ 每条 chunk-local 公理:    │
        │   - 被 global 覆盖? → 丢弃 │
        │   - 与 global 冲突? → 标记 │
        │   - 不冲突? → 叠加         │
        └───────────────┬───────────┘
                        ↓
                推理用的最终 TBox
```

**冲突解决伪代码**：

```csharp
public List<Axiom> Reconcile(
    List<Axiom> chunkLocal,
    List<Axiom> kbGlobal)
{
    var result = new List<Axiom>(kbGlobal);  // 全局优先入选

    foreach (var local in chunkLocal)
    {
        var conflict = kbGlobal.FirstOrDefault(g => g.Overlaps(local));
        if (conflict == null)
        {
            result.Add(local);  // 不冲突 → 叠加
        }
        else if (conflict.IsStricterOrEqualTo(local))
        {
            // global 已经更严格或等价 → 丢弃 local（已被覆盖）
            _audit.LogConflict(local, conflict, "covered by global");
        }
        else
        {
            // 矛盾且 global 不更严格 → 标记 tentative + 报警
            result.Add(local.MarkAsTentative());
            _audit.LogConflict(local, conflict, "WARN: contradiction");
        }
    }

    return result;
}
```

### 14.6 版本与迁移【新增】

KB-global TBox 是"系统级 schema"，**改动必须经迁移**。版本号语义化：

| 改动类型 | 版本 bump | 备注 |
|---------|---------|------|
| 新增 class / property / shape | minor (1.0 → 1.1) | 向下兼容，无需迁移 |
| 收紧约束（加 SHACL `maxCount`） | minor (1.1 → 1.2) | 跑迁移检查：哪些历史实例违反? |
| 修改 `domain` / `range` | major (1.2 → 2.0) | 破坏性变更，必须迁移 |
| 重命名 `canonical_id` | major | 加别名映射 + 数据迁移 |
| 删除（deprecate） | major | 先 deprecate 30 天，再 retire |

**迁移执行示例**（1.5 → 1.6 加 SHACL 约束 "Engineer hasSkill minCount 1"）：

```sql
-- 1. 找出违反新约束的 chunk
INSERT INTO tbox_migration_violations (migration_id, chunk_id, violation_msg)
SELECT
    'v1.5-to-1.6',
    chunk_id,
    'Engineer instance missing required hasSkill'
FROM chunks
WHERE ontology_json @> '{"classes":[{"id":"Engineer"}]}'
  AND NOT EXISTS (
      SELECT 1 FROM jsonb_array_elements(ontology_json->'properties') p
      WHERE p->>'id' = 'hasSkill'
  );

-- 2. 如果违反集 > 阈值，迁移阻塞，等专家裁定
-- 3. 否则版本激活，旧版本变 deprecated（保留 30 天后 retire）
```

### 14.7 与现有 ExtractConfig 的关系

[ExtractConfig](internal/types/knowledgebase.go)（§2.1 已提及）是 KB 级别的实体 / 关系**类型限定**——它本质上就是一个**原始的 KB-global TBox**。本节设计把它升级为：

| 维度 | 现有 ExtractConfig | 新设计 KB-global TBox |
|------|-------------------|---------------------|
| 内容 | Nodes / Relations 类型名 | 完整公理（subClass、disjoint、SHACL、axiom） |
| 来源 | 手动配置 | 手动 + 自动 promote + 审核 |
| 演进 | 手动改 | 版本化 + 迁移 |
| 用途 | 限定 LLM 抽取词表 | 限定词表 + 推理优先级 + 一致性约束 |

**迁移策略**：§14 上线时一次性把现有 [ExtractConfig](internal/types/knowledgebase.go) 自动转为 `kb_global_tbox` 的 `v1.0.0` 初始版本（每个 `Node` 转为一个 `class` 公理）。原 API 兼容保留（读取走新表，写入双写一段时间），平滑过渡。

### 14.8 风险与设计取舍

| 风险 | 缓解 |
|------|------|
| Promotion 过激进 → 过早固化错误公理 | Path A 自动晋升只入 `draft`，**必须人工 activate** |
| 全局 TBox 与 chunk-local 矛盾难解 | 默认全局优先 + 矛盾 audit log + 定期专家复审 |
| 多版本并存增加查询复杂度 | 同时只激活一个 `active` 版本；retired 版本保留供历史推理重现 |
| 跨 KB 概念冲突 | 默认 KB 隔离，不做自动共享；未来加 org/system 继承时单独设计 |
| 迁移阻塞业务 | 迁移失败 → 回滚 + 报警；推理 fallback 到旧版本，不阻塞 |
| 自动 promote 把审核成果"提前"用掉 | 路径 A 的四个阈值都偏保守；审核 acceptance 是硬门槛 |

**核心取舍**：

- **取舍 1：默认 KB 隔离，不做跨 KB 自动共享**——避免一个 KB 的错误污染整个租户
- **取舍 2：promotion 默认入 `draft`，不自动激活**——避免错误公理一上线就有权威性
- **取舍 3：全局优先 chunk-local**——一旦审核晋升就是"事实"，不被新 chunk 的局部偏差挑战
- **取舍 4：版本永不删除，只能 retired**——保证历史推理可重现（reproducibility）
- **取舍 5：ExtractConfig 双写过渡**——避免 big-bang 切换风险，旧 UI / API 可继续工作

### 14.9 演进路径

| 阶段 | 时长 | 内容 |
|------|------|------|
| MVP | 2 周 | `kb_global_tbox` 表 + 仅 Path B（手动晋升）+ 基础 Reconcile 逻辑 |
| Phase 2 | 1 月 | Path A 自动判定 + `draft` 状态 + 专家激活 UI |
| Phase 3 | 1 月 | 版本化 + 迁移检查 + 矛盾审计 dashboard |
| Phase 4 | 2 月 | Path C query trace 拉动 + `ExtractConfig` 自动迁移 |
| 长期 | — | 跨 KB / 跨租户 ontology 继承体系（org-level、system-level）+ Tier 0 上层本体接入 |

### 14.10 与其他章节的关系

```
              ┌─────────────────────────────────┐
              │  本节 §14: TBox 演进治理         │
              └────────────────┬────────────────┘
                               │
       ┌───────────────────────┼───────────────────────┐
       ▼                       ▼                       ▼
  §12 专家审核              §4.5 chunk.ontology_json   §2.1 ExtractConfig
  (Path B 入口)            (Tier 2 源数据)            (v1.0.0 初始化)
       │                       │                       │
       └───────────┬───────────┴───────────────────────┘
                   ▼
              kb_global_tbox (新表)
                   │
                   ▼
              §4.7 切片合成时优先级合并 (Reconcile)
                   │
                   ▼
              §7 Symbolic Reasoner / §13 Hybrid 都受益
```

**§14 是把 §12 审核成果沉淀、把 §13 hybrid 推理优化的关键存储层**——没有这一层，所有审核成果都散落在单个 chunk 上，影响范围仅限被审核过的 chunk 本身。

---

## 15. Federated SPARQL：跨 KB 联邦查询【新增设计】

### 15.1 为什么纳入正式设计

§14 定下 "默认 KB 隔离" 原则——这避免了一个 KB 的错误污染另一个 KB。但随着 WeKnora 在企业内规模化，会出现**确实需要跨 KB 协同推理**的场景：

- **同租户多 KB 协同**：同一个公司有 "HR docs"、"Engineering wiki"、"Customer support" 三个 KB，回答"工程师 Alice 负责的客户来自哪个行业"需要 join 三个 KB
- **垂直领域引用**：医疗 KB 引用 SNOMED CT 上层本体；法律 KB 引用 LKIF；这些是"只读的远端 KB"
- **组织间协作（远期）**：合作伙伴间共享特定 ontology 切片（如供应链上下游）

**Federated SPARQL = 受控的、可观测的、有 trust boundary 的跨 KB 推理能力**。它不是"打破隔离"，而是"为打破隔离设计严格协议"。

### 15.2 联邦场景分级

按信任边界从低到高排序，**MVP 只做 Tier 1**：

| Tier | 场景 | 信任模型 | 实现优先级 |
|------|------|---------|----------|
| 1 | 同租户、同用户有权限的多 KB | 现有 RBAC | **MVP** |
| 2 | 同租户、跨 KB share（沿用现有 [kbshare](internal/application/service/kbshare.go) 机制） | KB 共享协议 | Phase 2 |
| 3 | 跨租户、双方 admin 配置 federation 协议 | 双向授权 | Phase 3 |
| 4 | 引用外部公共本体（schema.org / Wikidata） | 只读 + 缓存 | Phase 4 |
| 5 | 跨组织联邦（federated learning 风格） | 加密 + 全量审计 | 长期 |

### 15.3 架构：联邦路由层

```
              User Query (NL or SPARQL)
                       │
                ┌──────▼──────┐
                │   Agent     │
                │   Router    │  ← 现有 ontology_reason 工具（扩展）
                └──────┬──────┘
                       │ federated_kb_ids = [kb_a, kb_b, kb_c]
                       ▼
              ┌────────────────────────┐
              │   Federator            │  (.NET sidecar 新增组件)
              │   - Query Planner      │
              │   - Permission Gate    │
              │   - Alignment Resolver │
              │   - Result Merger      │
              └────────────┬───────────┘
                           │
            ┌──────────────┼──────────────────┐
            ▼              ▼                  ▼
          KB-A           KB-B               KB-C
        ┌──────┐       ┌──────┐           ┌──────┐
        │slice │       │slice │           │slice │
        │+rsn  │       │+rsn  │           │+rsn  │   (§7 Reasoner per-KB)
        └──┬───┘       └──┬───┘           └──┬───┘
           │              │                  │
           └──────────────┼──────────────────┘
                          ▼
              ┌────────────────────────┐
              │  Alignment Resolver    │  ← 跨 KB 概念归一
              │  (kb_federation_       │
              │   alignment 表)         │
              └────────────┬───────────┘
                           ▼
                  Final Result
                  + per-KB provenance
                  + alignment annotations
                  + warnings (partial / skipped)
```

### 15.4 数据模型扩展【新增】

**跨 KB 联邦协议表**：

```sql
CREATE TABLE kb_federation_protocol (
    id              BIGSERIAL PRIMARY KEY,
    tenant_id       BIGINT NOT NULL,
    source_kb_id    TEXT NOT NULL,
    target_kb_id    TEXT NOT NULL,
    direction       TEXT NOT NULL CHECK (direction IN ('read', 'bidirectional')),
    scopes          TEXT[] NOT NULL DEFAULT '{}',   -- 允许暴露的 class/property 白名单
    status          TEXT NOT NULL DEFAULT 'active'
                    CHECK (status IN ('active', 'suspended', 'revoked')),
    created_by      BIGINT REFERENCES users(id),
    expires_at      TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, source_kb_id, target_kb_id)
);

CREATE INDEX idx_federation_source ON kb_federation_protocol
    (tenant_id, source_kb_id, status) WHERE status = 'active';
```

**跨 KB 概念对齐表**：

```sql
CREATE TABLE kb_federation_alignment (
    id              BIGSERIAL PRIMARY KEY,
    tenant_id       BIGINT NOT NULL,
    kb_a_id         TEXT NOT NULL,
    kb_a_canonical  TEXT NOT NULL,
    kb_b_id         TEXT NOT NULL,
    kb_b_canonical  TEXT NOT NULL,
    relation        TEXT NOT NULL
                    CHECK (relation IN ('equivalent', 'sub_of', 'super_of', 'overlaps')),
    confidence      REAL NOT NULL DEFAULT 1.0,
    aligned_by      BIGINT REFERENCES users(id),
    aligned_method  TEXT,                  -- 'expert' / 'shared_iri' / 'llm' / 'embedding'
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, kb_a_id, kb_a_canonical, kb_b_id, kb_b_canonical)
);
```

**联邦审计**：

```sql
CREATE TABLE federation_audit_log (
    id              BIGSERIAL PRIMARY KEY,
    tenant_id       BIGINT NOT NULL,
    user_id         BIGINT REFERENCES users(id),
    source_kb_id    TEXT NOT NULL,
    target_kb_ids   TEXT[] NOT NULL,
    query_hash      TEXT NOT NULL,
    alignment_used  JSONB,
    result_summary  JSONB,           -- counts per KB, total triples, latency
    warnings        TEXT[],
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

### 15.5 API 扩展

在 §7.8 的 `/reason` 上追加联邦参数：

```
POST /reason

Request:
{
  "tenant_id": 123,
  "knowledge_base_ids": ["kb_a", "kb_b", "kb_c"],   // 多个 KB → 自动触发联邦
  "federation_strategy": "broadcast" | "planned",    // 新增；默认 broadcast
  "federation_timeout_per_kb_ms": 5000,              // 单 KB 超时
  "query": {
    "type": "natural_language",
    "body": "Alice 负责的客户来自哪些行业？",
    "mode": "hybrid"
  }
}

Response:
{
  "status": "ok" | "partial",
  "answer": "...",
  "per_kb_results": {
    "kb_a": { /* HR KB: Alice manages 3 customers */ },
    "kb_b": { /* Customer KB: customers = Foo / Bar / Baz */ },
    "kb_c": { /* Industry KB: Foo→Finance, Bar→Healthcare */ }
  },
  "alignment_used": [
    {"a":"kb_a:Person/Alice","b":"kb_b:Account/Alice","method":"expert"},
    {"a":"kb_b:Industry","b":"kb_c:Sector","method":"shared_iri"}
  ],
  "warnings": ["kb_d 因权限不足跳过", "kb_e 超时（部分结果）"],
  "skipped_kbs": ["kb_d"],
  "partial_kbs": ["kb_e"],
  "elapsed_ms": 2340
}
```

**两种 federation_strategy**：

- **broadcast**（默认，MVP 用）：把查询广播到所有 KB 并发执行，最后合并。简单可靠，但所有 KB 都查一遍可能浪费
- **planned**（Phase 2 优化）：先用 LLM（§13 hybrid 模式 1）分析查询，决定哪一段查询去哪个 KB。代价是多一次 LLM 调用，收益是只查相关 KB

### 15.6 跨 KB 概念对齐

这是 §9 alignment 在 KB 间的延伸。三种对齐来源：

1. **共享 canonical_id（最简单）**：两个 KB 都用同一个标准 IRI（如 schema.org 引入的 `schema:Person`）→ 自动对齐
2. **手动登记（最可靠）**：管理员在 federation 设置 UI 里建立桥接（KB-A:Engineer ≡ KB-B:Developer）→ 写入 `kb_federation_alignment`
3. **LLM 动态对齐（兜底）**：查询时用 §13 hybrid 模式让 LLM 提议跨 KB 等价对，标记为 `tentative`，UI 上显示黄色边框，引导用户去 §12 审核固化

```csharp
public class FederationAlignmentResolver
{
    public async Task<AlignedQuery> ResolveAsync(
        Query query,
        string[] targetKbs,
        long tenantId,
        CancellationToken ct)
    {
        // 1. 拉取已登记的跨 KB 对齐
        var alignments = await _repo.GetAlignments(tenantId, targetKbs, ct);

        // 2. 检测共享 IRI（如 schema.org 命名空间）→ 自动并入
        alignments.MergeWithSharedIris(targetKbs);

        // 3. 对每个目标 KB 翻译 query 中的 IRI
        var perKbQueries = new Dictionary<string, Query>();
        foreach (var kb in targetKbs)
        {
            perKbQueries[kb] = query.Translate(iri =>
                alignments.LookupInKb(iri, kb) ?? iri);
        }

        return new AlignedQuery(perKbQueries, alignments);
    }
}
```

LLM 动态对齐留作 Phase 3，**MVP 只用前两种**。

### 15.7 权限与安全

**默认拒绝**：联邦查询默认拒绝，必须满足以下任一：

1. 用户对所有目标 KB 都有 `read` 权限（沿用现有 [RBAC](internal/middleware/rbac.go)）
2. 存在 `kb_federation_protocol` 记录且 `status='active'` 且未过期
3. 用户角色为 `tenant_admin` 或新增的 `federation_admin`

**新增 RBAC 权限点**：

- `federation:query` —— 发起联邦查询
- `federation:configure` —— 配置 `kb_federation_protocol`
- `federation_alignment:write` —— 编辑跨 KB 对齐

**审计**：每次联邦查询都写 `federation_audit_log` 表，包含查询者、目标 KB 列表、对齐桥接、查询哈希、结果摘要、warnings——满足合规审计需求。

### 15.8 失败处理与降级

| 情况 | 行为 |
|------|------|
| 某 KB 超时（> `federation_timeout_per_kb_ms`） | 返回部分结果 + warning，标记 `partial_kbs` |
| 某 KB 权限不足 | 跳过 + warning，标记 `skipped_kbs`，不阻塞其他 KB |
| 对齐缺失（IRI 在 B KB 找不到对应） | 该跨 KB 关联走 §13 hybrid 模式 2 软关联（tentative） |
| 全部 KB 失败 | 降级到只查发起者所属的默认 KB（fallback） |
| 联邦循环（A → B → A） | 查询链路 ID 检测自引用，立即拒绝并报警 |
| Alignment confidence 过低（< 0.5） | 标 `tentative` 返回，UI 黄色提示 |

**关键设计原则**：联邦查询**永远返回**——即使部分失败也给出可用结果加 warning，绝不让一个 KB 的故障阻塞整个查询。

### 15.9 风险与设计取舍

| 风险 | 缓解 |
|------|------|
| 跨 KB 查询性能不可控（N 倍延迟） | broadcast 默认 timeout 5s/KB；planned 策略减少 KB 数；并发执行 |
| 概念对齐错误导致语义漂移 | 对齐 confidence 标记；tentative 标在结果上；定期 §12 审核固化 |
| 权限泄漏（用户通过 federation 看到不该看的 KB 内容） | 默认拒绝 + 显式协议 + 全量审计 + scope 白名单 |
| 联邦循环引用导致死循环 | 查询 ID 链路追踪，自引用立即拒绝 |
| 远端 KB schema 版本不一致 | 响应里带 `schema_version_hash`；客户端按需重试或忽略 |
| LLM 动态对齐误判 | MVP 不开启该路径；Phase 3 开启后必须经 §12 审核才能写入 `kb_federation_alignment` |

**核心取舍**：

- **取舍 1：MVP 只做同租户 Tier 1**——避免一开始陷入跨租户授权设计的泥潭
- **取舍 2：broadcast 优先 planned**——简单可靠，性能问题靠 timeout 兜底
- **取舍 3：对齐是 first-class 实体**——不是查询时临时猜，而是预先登记 + LLM 动态补充
- **取舍 4：默认拒绝**——任何联邦动作都需显式协议或全权限，违反 fail-open 原则
- **取舍 5：永远返回**——容错优于"严格正确"，partial result 加 warning 是可接受的契约
- **取舍 6：复用现有 [kbshare](internal/application/service/kbshare.go) 机制**——Tier 2 沿用，不引入第二套共享模型

### 15.10 演进路径

| 阶段 | 时长 | 内容 |
|------|------|------|
| MVP | 2 周 | Tier 1：同租户多 KB broadcast + 共享 canonical_id 自动对齐 + RBAC 校验 + `federation_audit_log` |
| Phase 2 | 1 月 | `kb_federation_alignment` 手动登记 UI + planned 策略（接入 §13 hybrid 模式 1） |
| Phase 3 | 2 月 | Tier 2/3：跨 KB share / 跨租户 `kb_federation_protocol` + LLM 动态对齐（接入 §12 审核固化） |
| Phase 4 | 1-2 月 | Tier 4：上层本体接入（schema.org / Wikidata 本地镜像缓存 + 周期性同步） |
| 长期 | — | Tier 5：跨组织联邦（端到端加密 + zero-knowledge provenance proof + 跨链审计） |

### 15.11 与其他章节的关系

```
              ┌─────────────────────────────────┐
              │  本节 §15: Federated SPARQL     │
              └────────────────┬────────────────┘
                               │
       ┌───────────────────────┼───────────────────────────┐
       ▼                       ▼                           ▼
  §14 KB-Global TBox       §9 Canonical Map         §13 Hybrid (LLM 对齐)
  (受控破除隔离)           (单 KB 对齐 → 跨 KB)      (动态对齐 + planning)
       │                       │                           │
       └───────────┬───────────┴───────────────────────────┘
                   ▼
              Federator 路由层（sidecar 新增）
                   │
                   ▼
              §7 Sidecar /reason 端点（扩展）
                   │
                   ▼
              Agent ToolBox: ontology_reason（扩展 federated_kb_ids）
                   │
                   ▼
              §12 专家审核（跨 KB tentative 对齐固化的归宿）
```

**§15 是 "受控破除 §14 KB 隔离" 的协议层**——它不动 per-KB 推理引擎，只在调度、对齐、结果合并层做扩展。MVP 完成后，跨 KB 查询能力对 Agent **透明可用**，用户只需传多个 `knowledge_base_ids` 即可。

---

## 16. 演进路线（章末汇总）

### 16.1 总体路线图

```
   Week 1-2      Month 1-2       Month 3       Month 4-6      Month 6+
  ──────────── ──────────────── ─────────── ───────────────── ──────────────
   [1]          [2]              [3]          [4]               [5]
   Core MVP     §12-§15 各自     Phase 2      Phase 3-4         长期
   §1-§11       MVP 渐进交付      扩展功能      治理 + 优化       战略级
                                              ↓
                                  [全部展开见 §16.6 统一 Backlog]
```

5 个 milestone 的总长度约 6+ 月。Core MVP 是唯一**串行**阶段——其他都可以并行/重叠推进。

### 16.2 Core MVP（核心 §1-§11，2 周）

最小可验证版本，**仅核心设计**——`micro-TBox 抽取 + .NET sidecar 推理 + Agent 工具`，不含 §12-§15 扩展。

```
Day 1-2:   chunks 表加 ontology_json 字段；types.MicroTBox 定义（Go 端）
Day 3-4:   graph_extraction.yaml 加 micro_tbox prompt（含完整示例）
Day 5-6:   graphBuilder.extractMicroTBoxes 实现 + evidence 后验校验
Day 7-8:   .NET sidecar 骨架 + dotNetRDF 引入 + PostgreSQL 连通
Day 9-10:  N3RuleGenerator + ShaclGenerator 核心场景 + Fixpoint 引擎
Day 11-12: Minimal API endpoint + Docker 化 + Go Agent 工具接入
Day 13-14: 端到端跑通（Romeo & Juliet 测试集）+ 文档
```

**Core MVP 验收标准**：

- 上传 10 篇技术文档，自动抽 micro-TBox
- 查询 "X 的所有间接依赖" 能通过传递推理返回正确结果
- 每条推理结果带 evidence chunk 链接

### 16.3 章节扩展 MVP 矩阵（§12-§15）

Core MVP 落地后，§12-§15 按需求优先级**渐进交付**：

| 章节 | MVP 范围 | 时长 | 依赖 |
|------|---------|------|------|
| §13 混合推理 MVP | 模式 1（NL→SPARQL→NL）+ `SparqlGuard` + 基础缓存 | 1 周 | Core MVP |
| §12 专家审核 MVP | 单 chunk 审核（accept / reject / edit），不做合并 | 2 周 | Core MVP |
| §14 TBox 演进 MVP | `kb_global_tbox` 表 + Path B 手动晋升 + Reconcile 逻辑 | 2 周 | Core MVP |
| §15 联邦查询 MVP | Tier 1：同租户多 KB broadcast + RBAC 校验 + 审计日志 | 2 周 | Core MVP |

**渐进策略**：建议按 §13 → §12 → §14 → §15 顺序交付（依赖最少的先做，§13 模式 1 还是 §15 planned 策略的前置）。4 个章节扩展 MVP 并行最快 2-3 周完成，串行约 1.5 月。

### 16.4 完整版（约 3 月，Core + 章节扩展 MVP 全部落地）

在 Core MVP + 全部 §12-§15 MVP 之上，额外补足**横切关注点**：

- 完整 N3 规则集（含 `equivalentClass` / `subPropertyOf` / `propertyChain` 等）
- SHACL 验证完整流程（含 `sh:sparql` 自定义约束）
- §9 Tier 3 离线对齐 job（embedding + LLM 二次判定）
- 推理结果缓存（按 chunk 集合 hash）
- 多租户隔离的运维保障
- 可观测性（推理延迟、命中率、对齐质量指标、LLM 成本）
- 前端可视化（micro-TBox diff 视图、推理链可视化）

### 16.5 章节升级索引

设计文档从最初 12 章 + 5 个长期演进 bullet 起步。其中 4 个 bullet 已经升级为独立设计章节，剩 1 个保留为可选路径：

| 主题 | 状态 | 位置 |
|------|------|------|
| 专家审核与反馈闭环 | ✅ 已升级 | §12 |
| 混合推理（Neuro-Symbolic） | ✅ 已升级 | §13 |
| TBox 演进治理 | ✅ 已升级 | §14 |
| Federated SPARQL（跨 KB 联邦查询） | ✅ 已升级 | §15 |
| OWL 2 DL 完整支持 | ◯ 保留可选 | §7.7 / §15.10 Tier 4 / §16.6 长期 |

这张表是**文档演进的元信息**——保留它让读者一眼看到本设计的边界扩张轨迹，也方便未来 review 时知道哪些是后续插入的章节。

### 16.6 统一 Backlog（跨章节汇总）

把各章节散落的 **Phase 2+** 和 **长期** 事项汇总到这张表，便于跨章节排期与季度复盘。本表是**索引不是设计文档**——每个工作项都有对应章节锚点，不在本表展开。

#### Phase 2（章节扩展 MVP 落地后 1 月内）

| 来源 | 工作项 | 依赖 |
|------|-------|------|
| §12.11 | 跨 chunk 合并 UI + canonical_map 反馈到 §6.1 prompt（路径 A） | §12 MVP |
| §13.9 | 模式 2（软关联）+ 模式 3（修复建议接入 §12 审核队列） | §13 MVP, §12 MVP |
| §14.9 | Path A 自动判定 + `draft` 状态 + 专家激活 UI | §14 MVP, §12 |
| §15.10 | `kb_federation_alignment` 手动登记 UI + planned 策略 | §15 MVP, §13 模式 1 |

#### Phase 3（Phase 2 后 2-3 月）

| 来源 | 工作项 | 依赖 |
|------|-------|------|
| §12.11 | KPI dashboard + 优先级算法调优 + 审计日志查询 | §12 Phase 2 |
| §13.9 | 模式 4（反事实推理）+ 模式 5（Reasoning Tape） | §13 Phase 2 |
| §14.9 | 版本化 + 迁移检查 + 矛盾审计 dashboard | §14 Phase 2 |
| §15.10 | Tier 2/3：跨 KB share / 跨租户 `federation_protocol` + LLM 动态对齐 | §15 Phase 2, §12 |

#### Phase 4（Phase 3 后 3-6 月）

| 来源 | 工作项 | 依赖 |
|------|-------|------|
| §12.11 | 审核动作沉淀为训练数据（路径 B），定期再训 prompt | §12 Phase 3 |
| §13.9 | LLM budget 系统 + 自适应 mode 选择（Agent 自动判断该不该 hybrid） | §13 Phase 3 |
| §14.9 | Path C query trace 拉动 + `ExtractConfig` 自动迁移 | §14 Phase 3 |
| §15.10 | Tier 4：上层本体接入（schema.org / Wikidata 镜像缓存 + 周期同步） | §15 Phase 3 |
| §16.4 | 前端可视化（micro-TBox diff 视图、推理链可视化） | UX 投资优先级 |

#### 长期（6 月以上 / 战略级）

| 来源 | 工作项 | 启动条件 |
|------|-------|---------|
| §12.11 | 多审核员仲裁机制 + Active Learning（让模型挑最值得审的样本） | 已有较多审核样本 |
| §13.9 | LoRA 微调小模型专做 NL→SPARQL，降本 5-10x | 路径 B 训练数据成熟 |
| §14.9 | 跨 KB / 跨租户 ontology 继承体系（org-level / system-level） | 跨 KB 治理已稳定 |
| §14.10 | Tier 0 上层本体接入（FOAF / SNOMED / FIBO 等行业本体） | 视垂直行业需求 |
| §15.10 | Tier 5：跨组织联邦（端到端加密 + ZK provenance proof） | 法务合规先行 |
| §7.7 / §16.5 | OWL 2 DL 完整支持（接外部 Stardog / RDFox） | 业务真出现 DL 推理需求 |

#### Backlog 维护原则

1. **新章节诞生时**，作者必须把该章节的 Phase 2+ 事项登记到本表，并在本章节的"演进路径"小节双向链接
2. **季度复盘**时重新评估 Phase 1→2 / 2→3 的项目是否仍然合理（可降级 / 升级 / 删除）
3. **依赖关系列必须填写**——避免下游 Phase 项目在依赖未完成时启动
4. **每个 Phase 项目都有对应章节锚点**——本表是**索引**不是**设计文档**，所有展开放在源章节
5. **Tier 划分按时间窗口（月）而不是优先级**——优先级体现在依赖关系与排期决策中

## 17. 已决策与待确认（评审闭环）

本节用于替代泛化的"开放问题 / 假设"表述：
- 已达成一致的内容写成**已决策**，直接作为实现契约
- 尚未达成一致但必须推进的内容写成**待确认**，附负责人与截止时间

### 17.1 已决策（可直接实施）

1. **consistency 查询必须执行 SHACL 校验**
  - 契约：任何 `query.type=consistency` 请求都必须运行 SHACL 校验
  - 返回：若校验未执行，返回 `status=error`；禁止默认 `consistent=true`
  - 关联章节：§7.6、§7.8

2. **默认执行路径为 n3-extended 蕴含推理；SHACL 按请求触发**
  - 契约：默认执行 `n3-extended`；当 `query.type=consistency|shacl`（或 `profile=shacl`）时执行 SHACL 校验
  - 目的：在正确性与延迟之间做可预测平衡
  - 关联章节：§7.7

3. **instance_facts 可省略，但服务端必须自动回填**
  - 契约：请求未提供 `instance_facts` 时，sidecar 必须按 `chunk_ids` 自动拉取实例事实
  - 可观测性：响应必须返回 `data_source=provided|fetched`
  - 关联章节：§7.6、§7.8

### 17.2 待确认（有截止时间）

| 议题 | 选项 | 建议默认值 | 负责人 | 截止时间 |
|------|------|-----------|--------|---------|
| SHACL 默认并行策略 | A. 仅按请求触发；B. 默认并行，超时降级 | A | 后端负责人 | 下次里程碑评审前 |
| consistency/shacl 错误语义统一 | 错误码、`status`、`warnings` 字段统一规范 | 在 API 冻结前确定 | API Owner | 接口冻结前 |
| 缺省回填数据源优先级 | Neo4j 优先 vs PostgreSQL 优先 vs 混合 | 与现有查询链路一致 | 架构负责人 | 开发联调前 |

### 17.3 验收标准（针对本次修订）

通过条件：
1. `consistency` 请求在未校验场景下不会返回 `true`
2. 不传 `instance_facts` 时，服务仍可返回推理结果，且 `data_source=fetched`
3. 文档中默认路径描述与 API/伪代码实现一致，无互相矛盾

### 17.4 记录维护规则

1. 新增争议点时，禁止只写"待定"，必须补齐负责人和截止时间
2. 待确认项达成后，48 小时内迁移到"已决策"并同步相关章节
3. 若延期，必须在本节追加延期原因与新截止时间

---

## 附录 A：与现有路线的协作矩阵

| 场景 | Vector RAG | Graph RAG | Wiki | Ontology 切片 |
|------|-----------|-----------|------|--------------|
| "什么是 X" | 主 | 辅 | 主 | - |
| "X 和 Y 的关系" | 辅 | 主 | 辅 | 辅 |
| "X 的所有间接 Y" | - | 辅 | - | **主** |
| "X 是 Y 的一种吗" | - | - | 辅 | **主** |
| "这条数据合法吗" | - | - | - | **主**（SHACL） |
| "找相似案例" | 主 | 辅 | - | - |
| "浏览领域知识" | - | - | 主 | 辅 |

## 附录 B：参考资料

- WeKnora 现有知识图谱文档：[docs/wiki/核心功能/知识图谱.md](docs/wiki/核心功能/知识图谱.md)
- WeKnora 知识图谱启用指南：[docs/开启知识图谱功能.md](docs/开启知识图谱功能.md)
- OWL 2 Profiles: https://www.w3.org/TR/owl2-profiles/
- SHACL Specification: https://www.w3.org/TR/shacl/
- N3 Rules (W3C Team Submission): https://www.w3.org/TeamSubmission/n3/
- dotNetRDF 主站: https://dotnetrdf.org/
- dotNetRDF Inference 用户指南: https://github.com/dotnetrdf/dotnetrdf/wiki/UserGuide-Inference-And-Reasoning
- dotNetRDF SHACL API: https://dotnetrdf.org/api/html/N_VDS_RDF_Shacl.htm
- ASP.NET Core Minimal API: https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis
- 相关 LLM-Ontology 工作: OntoGPT, SPIRES, LLMs4OL

## 附录 C：术语表

| 术语 | 含义 |
|------|------|
| TBox | Terminology Box，本体的 schema 部分（class、property、公理） |
| ABox | Assertion Box，本体的实例部分（individual、type 断言） |
| micro-TBox | 本文档提出，chunk 范围的 TBox 片段 |
| OWL | Web Ontology Language，W3C 本体语言标准 |
| SHACL | Shapes Constraint Language，RDF 数据约束语言 |
| SPARQL | RDF 数据的查询语言 |
| IRI | Internationalized Resource Identifier，资源标识符 |
| Reasoner | 推理引擎（本文档采用 dotNetRDF 的 RDFS + N3 + SHACL 三引擎组合） |
| Closure | 推理闭包，所有可推出的事实集合 |
| LPG | Labeled Property Graph，Neo4j 的图模型 |

---

**文档版本**：v0.1（提案 RFC）
**作者**：本设计基于 WeKnora 现有架构，由 Claude 协助整理
**反馈**：欢迎在 [ROADMAP.md](docs/ROADMAP.md) 跟踪进展
