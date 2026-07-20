# 可推理 Ontology 切片：专家审核闭环 MVP 实施规格书

**状态：** 草案，待批准
**范围：** 专家审核闭环 MVP
**延后：** 跨 chunk merge UI、canonical_map 人工治理、prompt 回灌、多人仲裁、dashboard

---

## 1. 概述

### 1.1 新增能力

在 `Ontology Slice Core MVP` 已经完成的前提下，本规格为系统补齐“专家审核闭环”能力。系统当前已经能够从 chunk 中抽取 raw micro-TBox，并在查询阶段通过 sidecar 组合切片并执行 ontology reasoning；但这些结果仍然主要依赖自动抽取质量，一旦出现命名漂移、错误归类或关系误判，系统缺少稳定的人为纠偏入口。本规格的目标是让人工审核结果可以结构化沉淀，并在后续推理中优先生效，使 ontology 质量具备持续提升路径。

### 1.2 核心思路

本设计延续切片式 ontology 架构，不引入一个需要集中维护的全局 schema。chunk 仍然是最小的抽取单元、审核单元和推理拼装单元。系统在现有 raw ontology 之外，为 chunk 增加 reviewed ontology 表达层；人工审核只对当前 chunk 的 ontology 结果进行 accept、reject、edit 或 approve_all 操作，sidecar 在后续推理读取阶段优先使用 reviewed 数据，当 reviewed 缺失时自动回退到 raw 数据。这样既保留了切片式架构的弹性，又避免把专家审核设计成一条阻塞主链路的新流程。

### 1.3 关键架构决策

本规格坚持三个约束。第一，review 功能只增强质量闭环，不改变现有 Go 到 sidecar 的 request/response 契约，不影响现有 `ontology_reason` 的调用方式。第二，review API 首批继续采用 tenant scope，并通过 `kb_id` 过滤收窄到当前知识库，而不是重新引入一套 knowledge-base scoped router。第三，reviewed/raw fallback 的唯一切点固定在 sidecar 仓储层，即 `PostgresOntologyRepo`，避免把数据选择逻辑散落到 Go 服务端或前端。

## 2. 目标与范围

### 2.1 MVP 目标

MVP 只追求打通最短有效闭环，即“从当前知识库进入审核队列 -> 打开单 chunk -> 完成 accept/reject/edit/approve_all -> 持久化 reviewed ontology -> 后续 reasoning 优先读取 reviewed 结果”。只要这条链路稳定成立，系统就具备了把抽取错误逐步压缩掉的基础能力。

### 2.2 非目标

本阶段不交付跨 chunk merge UI，也不开放直接人工编辑 canonical_map 的后台能力。多人协作审核、仲裁机制、质量 dashboard、审核结果自动回灌到 prompt、以及基于审核日志的训练集沉淀都属于后续阶段的增量能力，而不是首批上线门槛。MVP 不应因为这些后续能力而膨胀范围。

## 3. 用户流

### 3.1 审核入口

具备 `ontology:review` 权限的租户管理员或专家审核者，从当前知识库详情页进入 ontology review。系统默认带上当前 `kb_id`，并将审核队列限制在当前知识库上下文内，避免在首批版本中向用户暴露跨知识库任务池，降低认知负担。

### 3.2 审核队列

审核队列页展示当前知识库下待审核的 chunk 列表。每条记录至少包含 `chunk_id`、优先级、状态、标题或摘要、更新时间等信息。排序以优先级为主，优先将低置信度、冲突、命名漂移、高复用 chunk 暴露给审核者。队列页的职责只是帮助审核者快速进入具体工作项，不承载复杂治理逻辑。

### 3.3 单 chunk 审核

审核者进入单 chunk 页面后，应当在同一视图中同时看到 evidence、高亮原文、raw ontology、当前 reviewed ontology 和审核状态。页面需要支持逐条执行 accept、reject、edit，也需要支持当当前 chunk 抽取结果整体可信时直接执行 approve_all。审核动作提交后，服务端写入 reviewed ontology 和 audit 记录。

### 3.4 生效路径

审核动作完成后，reviewed ontology 与审核元数据持久化到数据库。后续任意一次 `ontology_reason` 请求在命中该 chunk 时，由 sidecar 优先读取 reviewed ontology；若 reviewed 数据缺失、为空或状态不满足读取条件，则自动回退 raw ontology。这样可以在不改变调用契约的前提下，让审核结果自然进入现有 reasoning 主链路。

## 4. 数据模型

### 4.1 chunks 扩展

在现有 chunk ontology 存储字段基础上，新增 reviewed 相关字段。至少包括 reviewed ontology 内容、审核状态、审核人、审核时间。reviewed ontology 必须与 raw ontology 共用同一份 schema，避免出现结构漂移或双份 DTO 长期分叉的问题。

### 4.2 review queue

新增 `ontology_review_queue` 表承载待审核项。表中至少需要能够表达所属租户、所属知识库、chunk 标识、当前状态、优先级、更新时间，以及必要的摘要信息。该表的职责是支撑 UI 队列与服务端排序，而不是作为第二套 ontology 存储来源。

**冗余字段**：为避免队列接口触发 N+1 detail fetch，`ontology_review_queue` 表需要承载两个展示用冗余字段：

| 字段 | 类型 | 来源 | 说明 |
|------|------|------|------|
| `knowledge_title` | TEXT | `knowledges.title`（入队时快照） | 源文档标题，审核者据此判断 chunk 上下文 |
| `content_preview` | TEXT | `chunks.content`（入队时截断前 200 字符） | 内容预览，审核者快速浏览 chunk 主题 |

这两个字段是入队时的快照，后续不随源数据更新自动同步（接受一定程度的过时展示）。若审核者需要最新完整内容，应进入 detail 页面查看。

完整 DDL：

```sql
CREATE TABLE ontology_review_queue (
    id                BIGSERIAL PRIMARY KEY,
    tenant_id         BIGINT NOT NULL,
    knowledge_base_id TEXT NOT NULL,
    chunk_id          TEXT NOT NULL,
    knowledge_title   TEXT NOT NULL DEFAULT '',      -- 源文档标题快照
    content_preview   TEXT NOT NULL DEFAULT '',      -- chunk 前 200 字符快照
    priority          INT NOT NULL DEFAULT 50,
    priority_reason   TEXT,
    assigned_to       BIGINT REFERENCES users(id),
    status            TEXT NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending','in_review','approved','rejected','no_review')),
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, chunk_id)
);

CREATE INDEX idx_review_queue_prio ON ontology_review_queue
    (tenant_id, knowledge_base_id, status, priority DESC);
```

### 4.3 review audit

新增 `ontology_review_audit` 表记录每一次 accept、reject、edit、approve_all 动作。audit 数据必须追加写入，不覆盖历史，以支持后续的指标统计、问题回溯和训练数据沉淀。MVP 阶段虽然不建设 dashboard，但审计数据结构必须从第一版开始完整保留。

```sql
CREATE TABLE ontology_review_audit (
    id            BIGSERIAL PRIMARY KEY,
    tenant_id     BIGINT NOT NULL,
    chunk_id      TEXT NOT NULL,
    reviewer_id   BIGINT NOT NULL,
    action        TEXT NOT NULL CHECK (action IN ('accept','reject','edit','approve_all')),
    target_kind   TEXT NOT NULL CHECK (target_kind IN ('class','property','shape','alias','axiom')),
    target_id     TEXT NOT NULL,
    before_value  JSONB,
    after_value   JSONB,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_review_audit_chunk ON ontology_review_audit (tenant_id, chunk_id);
```

### 4.4 入库时冲突预检（轻量）

**目的**：在 micro-TBox 入队之前，检测同一知识库内不同 chunk 对同一 class 的声明是否存在冲突（如 chunk_A 说 `Engineer subClassOf Person`，chunk_B 说 `Engineer subClassOf Department`）。命中冲突的 chunk 获得优先级加分（+15），使其更快进入审核视野。

**与查询时 conflict 检测的区别**：查询时 sidecar 的冲突检测（§4.7 设计文档）在合并多个 chunk 的 schema graph 时运行，检测的是 `subClassOf + disjointWith` 同时存在的逻辑矛盾。入库时预检更轻量——只比较同一 class id 在不同 chunk 中的 `subClassOf` / `disjointWith` 声明是否一致，不做完整推理。

**实现**：在 graph builder 的 `extractMicroTBoxes` 完成后、`EnqueueChunk` 之前，对当前批次内的 chunk 做一次 KB 级 class 声明一致性检查：

```go
// internal/application/service/graph.go — 在 extractMicroTBoxes 完成后调用
func (b *graphBuilder) detectClassConflicts(
    ctx context.Context,
    chunks []*types.Chunk,
    kbID string,
) map[string][]string {
    // 1. 收集当前批次所有 class 声明: classID → []{chunkID, subClassOf, disjointWith}
    currentDecls := make(map[string][]classDeclRecord)
    for _, chunk := range chunks {
        if chunk.OntologyJSON == nil {
            continue
        }
        for _, cls := range chunk.OntologyJSON.Classes {
            currentDecls[cls.ID] = append(currentDecls[cls.ID], classDeclRecord{
                ChunkID:      chunk.ID,
                SubClassOf:   cls.SubClassOf,
                DisjointWith: cls.DisjointWith,
            })
        }
    }

    // 2. 对于出现 >= 2 次的 class，检查声明是否一致
    conflicts := make(map[string][]string)
    for classID, decls := range currentDecls {
        if len(decls) < 2 {
            continue
        }
        // 比较所有记录的 subClassOf 是否一致
        firstSub := decls[0].SubClassOf
        firstDisjoint := strings.Join(decls[0].DisjointWith, ",")
        for _, decl := range decls[1:] {
            curSub := decl.SubClassOf
            curDisjoint := strings.Join(decl.DisjointWith, ",")
            if (firstSub == nil) != (curSub == nil) ||
                (firstSub != nil && *firstSub != *curSub) ||
                firstDisjoint != curDisjoint {
                conflicts[classID] = append(conflicts[classID], decl.ChunkID)
            }
        }
    }

    // 3. 如果本批次只有一个 chunk 声明了该 class，查数据库中已有的同 KB chunk
    for classID, decls := range currentDecls {
        if len(decls) != 1 {
            continue
        }
        // 查询同 KB 下其他 chunk 的 ontology_json 中是否有同名 class
        existing, _ := b.chunkRepo.FindConflictingClass(ctx, kbID, classID, decls[0])
        if existing != nil {
            conflicts[classID] = append(conflicts[classID], existing.ChunkID)
        }
    }

    return conflicts
}
```

**性能约束**：预检只在当前批次（通常 ≤ 50 chunks）内做内存比较 + 一次数据库查询（查同 KB 冲突 chunk），不触发 LLM 调用，总耗时 < 50ms。

**优先级加成**：在 `OntologyReviewService.CalculatePriority` 中，命中冲突检测的 chunk 获得 `+15` 分（对应 §12.5 的 15 权重）。

### 4.5 批量回填命令

**目的**：当 ontology 功能刚开启，或为已有知识库补开 ontology review 时，大量历史 chunk 已有 `ontology_json` 但未入审核队列。需要一个管理命令批量生成队列条目。

**实现位置**：`cli/cmd/` 下新增 `ontology/` 子命令组（仿照现有 `cli/cmd/chunk/` 模式），提供 `backfill` 子命令。

```bash
# 为指定知识库的所有已有 ontology 的 chunk 回填审核队列
weknora ontology backfill --kb-id <kb_id>

# 仅回填最近 30 天内创建的 chunk
weknora ontology backfill --kb-id <kb_id> --since 720h

# 干跑模式：仅打印将入队数量，不实际写入
weknora ontology backfill --kb-id <kb_id> --dry-run
```

**核心逻辑**（`cli/cmd/ontology/backfill.go`）：

```go
func runBackfill(ctx context.Context, kbID string, since time.Duration, dryRun bool) error {
    // 1. 查询目标 KB 中 ontology_json IS NOT NULL 且未入队的 chunk
    chunks, err := chunkRepo.FindWithOntologyNotInQueue(ctx, tenantID, kbID, since)
    if err != nil {
        return err
    }

    if dryRun {
        fmt.Printf("Would enqueue %d chunks\n", len(chunks))
        return nil
    }

    // 2. 逐 chunk 计算优先级并批量入队
    for _, chunk := range chunks {
        priority, reason := reviewSvc.CalculatePriority(chunk)
        knowledgeTitle := getKnowledgeTitle(ctx, chunk.KnowledgeID)
        contentPreview := truncate(chunk.Content, 200)

        reviewSvc.EnqueueChunk(ctx, &EnqueueParams{
            Chunk:           chunk,
            Priority:        priority,
            PriorityReason:  reason,
            KnowledgeTitle:  knowledgeTitle,
            ContentPreview:  contentPreview,
        })
    }

    fmt.Printf("Enqueued %d chunks\n", len(chunks))
    return nil
}
```

**CLI 注册**：在 `cli/cmd/root.go` 中注册 `ontology` 子命令组，在 `cli/cmd/ontology/` 下创建 `ontology.go`（命令组入口）和 `backfill.go`。

## 5. 后端接口

### 5.1 Queue API

提供 tenant-scope 的审核队列查询接口，支持通过 `kb_id` 将结果收窄到当前知识库。返回结构应尽量精简，优先服务队列展示和跳转，而不是一次性返回完整 ontology 内容。

### 5.2 Detail API

提供单 chunk 审核详情接口。返回内容至少包含 evidence 信息、raw ontology、reviewed ontology、审核状态和审核元数据。该接口是单 chunk 审核页面的主数据源。

**evidence_spans**：为保证前端 evidence 高亮的精确性，后端在 detail 响应中预先计算每条 evidence 在 chunk.Content 中的起止偏移：

```json
{
  "chunk": { ... },
  "evidence_spans": [
    {
      "target_id": "Engineer",
      "target_kind": "class",
      "evidence": "software engineers design and build systems",
      "start_offset": 142,
      "end_offset": 184
    },
    {
      "target_id": "reportsTo",
      "target_kind": "property",
      "evidence": "each engineer reports to a team lead",
      "start_offset": 256,
      "end_offset": 293
    }
  ],
  "ontology_raw": { ... },
  "ontology_reviewed": { ... },
  "review_status": "pending"
}
```

**计算方式**：后端遍历 `ontology_raw` 中所有 class/property/shape/axiom 的 `evidence` 字段，在 `chunk.Content` 中用 `strings.Index` 定位首次出现位置。若 evidence 出现多次（如短证据串），取第一次出现的偏移。若 evidence 在原文中找不到（后验校验的兜底），则 `start_offset = -1`，前端跳过该条高亮。

**类型定义**：

```go
// internal/types/ontology_review.go
type EvidenceSpan struct {
    TargetID    string `json:"target_id"`
    TargetKind  string `json:"target_kind"`  // class | property | shape | axiom
    Evidence    string `json:"evidence"`
    StartOffset int    `json:"start_offset"` // -1 表示未在原文中找到
    EndOffset   int    `json:"end_offset"`
}
```

**前端使用**：收到 `evidence_spans` 后，前端将 `chunk.Content` 渲染为带高亮标记的文本——在 `start_offset` 到 `end_offset` 区间应用黄色背景色，hover 时 tooltip 显示对应的 `target_id` 和 `target_kind`。这比前端自己做 `indexOf` 匹配更精确，也避免了 evidence 在原文中多次出现导致的歧义。

### 5.3 Actions API

提供单 chunk 审核动作提交接口，支持 accept、reject、edit。当前端提交动作后，服务端负责校验权限、更新 reviewed ontology、写 audit 记录，并返回最新审核状态。前端不应承担审计拼装逻辑。

### 5.4 Approve All API

提供整 chunk 快捷通过接口。当审核者判断该 chunk 的 raw ontology 可以整体沿用时，服务端应支持直接将 raw ontology 提升为 reviewed ontology，而不要求前端逐条执行 accept。这个接口的目标是降低审核摩擦，而不是绕过审计，因此 approve_all 也必须写入 audit。

## 6. 推理读取路径改造

### 6.1 Sidecar fallback

reviewed/raw fallback 只在 sidecar 仓储层实现。`PostgresOntologyRepo` 在读取 chunk ontology 时，优先返回 reviewed ontology；如果 reviewed 缺失，则继续返回 raw ontology。该策略应当由仓储层统一封装，避免不同调用路径对 reviewed 是否生效产生分叉。

### 6.2 契约稳定性

`ReasonEndpoint`、Go 端 `ontology_client` 与 `ontology_reason` DTO 在 MVP 内保持不变。对于现有调用方来说，review 功能带来的变化只体现在 reasoning 结果质量提升，而不体现在 API 结构变化上。这个约束是控制变更范围的关键。

## 7. 前端集成

### 7.1 页面挂载点

前端继续复用现有 `knowledge-bases/:kbId` 详情页结构，在当前知识库上下文中增加 ontology review 入口。这样用户不需要建立新的导航心智，也不会把 review 功能误认为平台级治理工具。

### 7.2 MVP 页面

首批只建设两个页面：`OntologyReviewQueue` 和 `OntologyChunkReview`。前者承载列表和跳转，后者承载 evidence 展示与动作执行。`OntologyMergeCandidates`、`MergeDialog` 等页面和组件明确延后，不进入首批交付。

### 7.3 交互要求

审核页需要在单屏内同时呈现 evidence 与 axiom 操作区，避免审核者频繁切换上下文。approve_all 必须是显式动作按钮，不能与逐条 accept 混淆。编辑后的 axiom 需要和 raw 提取结果有清晰视觉区分，确保审核者理解当前保存态。

## 8. 权限与安全

### 8.1 权限点

首批仅新增一个权限点：`ontology:review`。具备该权限的租户管理员或专家审核者，可以访问 queue、detail、actions 和 approve_all 四类接口。

### 8.2 安全边界

系统继续复用现有 tenant route 与 RBAC 守卫模式，不创建新的旁路鉴权入口。未授权用户既不能读取审核数据，也不能写入 reviewed ontology。MVP 不引入 `ontology:merge` 与 `canonical_map:write`，以避免把高风险治理能力提前暴露到首批实现中。

## 9. 发布与验收

### 9.1 发布策略

发布顺序应当是先应用数据库迁移，再落地后端 API 和前端页面，最后联调 sidecar fallback。若联调风险偏高，可以在 `config.yaml` 中增加独立 review 开关；若风险可控，则可以沿用现有 ontology 功能开关，不强制新增一层 feature flag。

### 9.2 验收标准

验收至少满足四点。第一，审核者可以从当前知识库进入 review，并完成一次单 chunk 审核。第二，当 reviewed 数据存在时，后续 `ontology_reason` 实际优先读取 reviewed ontology。第三，当 reviewed 数据不存在时，系统自动回退 raw ontology，且主链路不报错。第四，无 `ontology:review` 权限的用户无法访问 review 相关接口或提交审核动作。

## 10. 后续演进

本规格完成后，最自然的第二阶段是补跨 chunk merge API/UI 与 canonical_map 反馈，把同义 class/property 的归一化能力纳入闭环。再往后才是 prompt 回灌、指标 dashboard、多人仲裁与训练数据沉淀。这些能力都建立在本 MVP 已经跑通“单 chunk 审核可真实改变推理输入”的基础上。
