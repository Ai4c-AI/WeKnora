# Ontology Review SDK 首批接入设计

**状态：** 草案，待批准
**范围：** Go SDK 首批封装 ontology review 四个接口
**延后：** CLI 命令、前端页面接入、SDK 自定义业务错误分类

---

## 1. 概述

### 1.1 背景

`ontology review loop MVP` 的后端接口已经具备最小闭环能力，但当前仓库中的 Go SDK `client/` 还没有对应的 typed API 封装。这会导致后续 CLI 或其他调用方如果要消费 review 能力，只能手工拼接 URL、维护请求体和解析响应，既破坏现有 SDK 的职责边界，也会让后续 CLI 接入产生重复实现。

### 1.2 目标

本设计只解决一件事：在 `client/` 下为 ontology review 增加首批 typed SDK 封装，并把方法签名、DTO 形状、错误处理和测试边界固定下来，使后续 CLI 可以直接复用这些接口，而不是重新实现一层 transport 逻辑。

### 1.3 关键决策

首批采用独立文件 `client/ontology_review.go` 承载 ontology review 方法、review 队列 DTO、review action/status 常量，以及 SDK 可复用的 ontology DTO。detail / action / approve_all 的返回值仍然以现有 `Chunk` wire model 为基础扩展 review 字段，不把后端已经返回的 chunk 字段窄化成只含审核页首屏字段的专用小模型。

公开 SDK 方法遵循现有 client 的 tenant-scoped 使用方式，不要求 CLI 或调用方在每个 review 方法里重复传入 path tenant id。后端当前 endpoint 仍然是 `/api/v1/tenants/{tenantID}/...` 形态，因此 SDK 内部需要一个私有 tenant path helper 从 context 中解析 active tenant id 来组装路径；该 helper 不应通过 `X-Tenant-ID` 触发跨租户访问语义。只有显式跨租户调用才应使用单独的 `ForTenant` 变体或 tenant override。

## 2. 范围与非目标

### 2.1 本期范围

本期只实现四个 SDK 方法，对应后端既有四个接口：

- `ListOntologyReviewQueue`
- `GetOntologyReviewChunk`
- `ApplyOntologyReviewAction`
- `ApproveAllOntologyReview`

这些方法都运行在现有 `Client` 基础设施之上，继续复用 `doRequest`、`parseResponse` 和现有鉴权头处理逻辑。

### 2.2 非目标

本期不实现 CLI 子命令，不对现有前端页面做任何接入，也不新增 SDK 侧的错误码枚举或业务语义包装。对于 review detail，也不把 review 字段并入通用 `client.Chunk` 定义，避免让普通 chunk/knowledge API 与审核视图耦合。

若实际后端 queue endpoint 仍缺少分页或队列摘要字段，本期 SDK 设计应把这些差异显式暴露为后端契约前置条件，而不是把 first-page-only 或 N+1 的限制固化进 SDK 公共签名。

## 3. 文件边界

### 3.1 新增文件

新增 `client/ontology_review.go`，负责 ontology review 相关 DTO、常量、tenant path helper 和方法实现。

### 3.2 尽量不修改的边界

`client/chunk.go`、`client/knowledge.go`、`client/knowledgebase.go` 保持职责不变。review 场景不通过扩展通用 `Chunk` 来承载审核字段；但 `OntologyReviewChunkDetail` 必须嵌入或等价覆盖现有 `Chunk` wire 字段，以避免把后端已返回字段丢弃。

如需让普通 client 也能保存“active tenant id for path building but not header injection”，可在 `client.go` 中补一个很小的只读 helper/option；该变更必须与 `WithTenantID` 的跨租户 header 语义保持区分。

### 3.3 测试文件

新增 `client/ontology_review_test.go`，采用与 `client/` 现有测试一致的 HTTP mock / test server 模式，验证路径、方法、请求体和响应解析。

## 4. SDK 接口设计

### 4.1 Tenant path 解析

后端 review endpoint 当前带有 tenant path segment，但现有 CLI/client 默认依赖 bearer token 或 tenant-scoped API key 表达租户身份，`WithTenantID` 会设置 `X-Tenant-ID` 并触发跨租户语义，不适合作为普通调用的默认路径来源。

因此 SDK 内部增加私有 helper，例如：

`func (c *Client) ontologyReviewTenantID(ctx context.Context) (uint64, error)`

解析顺序建议为：

1. context 中专用于 path 的 active tenant id，例如 `ActiveTenantID`
2. 若后续 `Client` 增加不注入 header 的 active tenant id 字段，则使用该字段
3. 都不存在时返回明确错误，提示调用方需要提供 active tenant id 才能访问 tenant-path review endpoint

不要把现有 `TenantID` context value 作为普通 path fallback，因为 `doRequest` 会把它同步写入 `X-Tenant-ID` header，从而触发跨租户访问语义。测试也应使用专用的 `ActiveTenantID` context key 或私有 helper 注入，而不是复用 `TenantID`。

公共方法不把 `tenantID` 暴露为每个调用的普通参数；若确实需要跨租户访问，应提供带显式命名的变体，例如 `ListOntologyReviewQueueForTenant(ctx, tenantID, query)`，并在该变体中明确是否需要设置 `X-Tenant-ID`。这样可以避免普通 CLI 路径误用 stale/display-only tenant id。

### 4.2 Queue 查询

定义 `OntologyReviewQueueQuery` 作为查询参数 DTO，至少包含：

- `KnowledgeBaseID string`
- `Status OntologyReviewStatus`
- `Page int`
- `PageSize int`

若后端短期只能支持 `limit`，SDK 仍不应只返回 `[]OntologyReviewQueueEntry`。公共返回值应使用分页响应 DTO，例如：

`func (c *Client) ListOntologyReviewQueue(ctx context.Context, query *OntologyReviewQueueQuery) (*OntologyReviewQueuePage, error)`

`OntologyReviewQueuePage` 至少包含：

- `Items []OntologyReviewQueueEntry`
- `Page int`
- `PageSize int`
- `Total int64`
- `HasMore bool`

该方法负责把 `kb_id`、`status`、`page`、`page_size` 编码到 query string，并请求：

`/api/v1/tenants/{tenantID}/ontology/review/queue`

如果当前后端尚未返回 `total` 或 `has_more`，应先补齐后端契约，或在 SDK 文档中明确该方法只支持第一页且后续需要破坏性签名变更；不允许静默把公共 SDK 固化为不可分页的 `limit` 切片。

### 4.3 Chunk Detail 查询

定义 review 专用 detail DTO：`OntologyReviewChunkDetail`。它用于区分“普通 chunk 查询”和“审核详情查询”，但不能窄化后端实际返回的 full chunk payload。

建议形态为：

`type OntologyReviewChunkDetail struct { Chunk ...review fields... }`

该结构必须保留或嵌入现有 `Chunk` wire 字段，包括但不限于：

- chunk 基本标识：`id`、`seq_id`、`knowledge_id`、`knowledge_base_id`、`tenant_id`
- chunk 上下文：`content`、`chunk_type`、`chunk_index`、`start_at`、`end_at`、`pre_chunk_id`、`next_chunk_id`、`parent_chunk_id`
- 现有上下文字段：`metadata`、`image_info`、`relation_chunks`、`indirect_relation_chunks`、`created_at`、`updated_at`
- ontology 相关字段：`ontology_json`、`ontology_json_reviewed`、`instance_facts_json`、`ontology_confidence`、`ontology_extracted_at`
- 审核元数据：`ontology_review_status`、`ontology_reviewed_by`、`ontology_reviewed_at`

方法签名建议为：

`func (c *Client) GetOntologyReviewChunk(ctx context.Context, chunkID string) (*OntologyReviewChunkDetail, error)`

该方法请求：

`/api/v1/tenants/{tenantID}/ontology/review/chunks/{chunkID}`

### 4.4 Action 提交

定义 SDK 侧 action/status 类型和常量，避免后续 CLI 重复维护字符串字面量：

`type OntologyReviewAction string`

至少包含：

- `OntologyReviewActionAccept OntologyReviewAction = "accept"`
- `OntologyReviewActionReject OntologyReviewAction = "reject"`
- `OntologyReviewActionEdit OntologyReviewAction = "edit"`
- `OntologyReviewActionApproveAll OntologyReviewAction = "approve_all"`

定义 `ApplyOntologyReviewActionRequest`，包含：

- `Action OntologyReviewAction`
- `ReviewedOntology *OntologyMicroTBox`，JSON tag 使用 `json:"reviewed_ontology,omitempty"`

`ReviewedOntology` 必须是 pointer，以保留后端对“字段缺失/null”和“提交了空 ontology 对象”的区分：

- `accept` / `approve_all` 未带 `reviewed_ontology` 时，后端可以回退到现有 reviewed ontology 或 raw ontology
- `edit` 未带 `reviewed_ontology` 时，后端应返回 validation error
- 显式传入空 `OntologyMicroTBox{}` 表示调用方真的要提交空 ontology，不能和 omitted 混淆

`OntologyMicroTBox` 不能只定义“最小可用字段”。首批必须覆盖后端当前公开 `MicroTBox` 的完整 wire contract，并保持精确 JSON tag，包括 `subClassOf`、`disjointWith`、`inverseOf` 等非 snake_case 字段，以及 nullable constraint 的 pointer 语义。若后端 schema 后续演进，SDK DTO 与测试 fixture 必须同步更新，避免 fetch-edit-submit round trip 丢字段。

方法签名建议为：

`func (c *Client) ApplyOntologyReviewAction(ctx context.Context, chunkID string, req *ApplyOntologyReviewActionRequest) (*OntologyReviewChunkDetail, error)`

该方法请求：

`/api/v1/tenants/{tenantID}/ontology/review/chunks/{chunkID}/actions`

### 4.5 Approve All

`approve_all` 单独保留方法，不折叠进 `ApplyOntologyReviewAction`，因为后端已经提供独立 endpoint，且该操作没有请求体。但 SDK 侧仍应复用同一套 action/status 常量，避免 CLI 同时维护两份 action vocabulary。

方法签名建议为：

`func (c *Client) ApproveAllOntologyReview(ctx context.Context, chunkID string) (*OntologyReviewChunkDetail, error)`

该方法请求：

`/api/v1/tenants/{tenantID}/ontology/review/chunks/{chunkID}/approve_all`

## 5. DTO 设计原则

### 5.1 队列 DTO

定义 `OntologyReviewQueueEntry`，保留队列视图所需字段：

- `id`
- `tenant_id`
- `knowledge_base_id`
- `chunk_id`
- `priority`
- `priority_reason`
- `assigned_to`
- `status OntologyReviewStatus`
- `created_at`
- `updated_at`

同时队列 DTO 应包含足够的轻量展示字段，避免 CLI/UI 为渲染列表对每一行再请求 detail。建议至少包括：

- `knowledge_title` 或等价来源标题
- `content_preview` 或 `summary`
- `ontology_summary` 或待审核 ontology 的轻量摘要

队列接口不返回完整 chunk 内容和完整 ontology JSON，以保持轻量；但它也不能只返回纯队列表元数据，否则后续 CLI queue 命令会天然形成 N+1 detail fetch。

### 5.2 详情 DTO

detail 使用 review 专用 DTO，而不是直接扩展通用 `client.Chunk` 定义。这样调用方可以明确区分“普通 chunk 查询”和“审核详情查询”，同时避免通用 `Chunk` 在未来逐渐膨胀为审核专用大对象。

但该 review 专用 DTO 必须以 `Chunk` wire model 为基底，保留后端返回的普通 chunk 字段。测试 fixture 应包含至少一个非首屏字段，例如 `metadata`、`image_info` 或 `next_chunk_id`，确保 SDK 不会因为 DTO 过窄而丢弃可用上下文。

### 5.3 Ontology DTO

`OntologyMicroTBox` 是 SDK 公共类型，不是 review 私有临时类型。即使首个使用方在 `ontology_review.go`，类型命名和注释也应面向后续 ontology extraction / reasoning / CLI 复用。

该 DTO 必须覆盖后端当前公开 schema 的完整字段集合，包含：

- `classes`
- `properties`
- `shapes`
- `aliases`
- `axioms`
- `confidence`

嵌套结构必须保留后端 JSON tag 和 pointer/nullability 语义。测试应覆盖完整 fixture 的 unmarshal + marshal round trip，至少验证 mixed-case tag 不会被错误改成 snake_case。

### 5.4 响应壳

继续沿用客户端现有模式，在 SDK 内部定义：

- `ontologyReviewQueueResponse`
- `ontologyReviewChunkResponse`

外部方法返回解包后的 typed data，而不是把 `success/data/message` 外壳暴露给调用方。

## 6. 错误处理

### 6.1 复用现有行为

本期不新增 ontology review 专属错误类型。四个新方法继续复用 `parseResponse` 的现有行为，使 SDK 在错误处理上与其他资源 API 保持一致。

### 6.2 不做业务猜测

SDK 不在客户端侧为 `reviewed_ontology` 补默认值，也不把服务端 message 翻译成新的 SDK 错误枚举。后端对 action payload 的语义由服务端保持权威：

- `edit` 未带 `reviewed_ontology` 时应返回 validation error
- `accept` 未带 `reviewed_ontology` 是合法路径，后端会回退到现有 reviewed ontology 或 raw ontology
- 显式空 ontology 与 omitted ontology 必须在 SDK 请求体中可区分

这样后续 CLI 可以直接复用 SDK 返回的错误文本，不需要再绕过 SDK 才能拿到真实失败原因。

### 6.3 错误 payload 保真

由于本期不新增业务错误类型，测试必须确认现有错误路径不会丢弃后端结构化错误 body。服务端返回非 2xx 且 body 包含 `code`、`message`、`details` 时，SDK 返回的 error 文本应保留原始 body，至少不能退化成只有 HTTP status 的泛化错误。

## 7. 测试策略

### 7.1 覆盖范围

`client/ontology_review_test.go` 至少覆盖六类测试：

1. queue 查询参数编码正确，包括 `kb_id`、`status`、`page`、`page_size`；若后端仍使用 `limit`，测试必须明确这是临时兼容层而不是最终公共契约
2. queue 响应保留分页元数据和轻量展示字段，避免只能返回 first page metadata slice
3. detail / action / approve_all 的路径与 HTTP method 正确，并验证 tenant path id 来自 active tenant helper，而不是误用 `X-Tenant-ID` header 语义
4. detail 响应能解析 full chunk context 与 review 字段，fixture 至少包含一个普通 chunk 上下文字段和一个 ontology review 字段
5. action 请求体能区分 omitted `reviewed_ontology`、显式空 `reviewed_ontology`、非空 `reviewed_ontology`，并验证 `edit`/`accept` 的不同语义不会被 SDK 序列化抹平
6. 服务端返回结构化错误 payload 时，SDK 继续沿用现有错误路径并保留原始 body 中的 `code`、`message`、`details`

### 7.2 非测试范围

本期不增加 CLI 测试。除非 queue 分页或轻量展示字段的后端契约仍缺失，否则不在 `internal/` 侧新增后端测试；如果缺失，则应先补齐后端契约再落 SDK，避免 SDK 公开不可分页或 N+1 的接口。

## 8. 与后续 CLI 的关系

本期虽然不做 CLI，但 DTO 和方法命名应直接面向 CLI 复用场景设计。CLI 后续只需要调用这四个 typed method，做参数解析和输出格式化即可，不应再重新定义 review transport struct、action/status 字符串、tenant path 解析或自行拼接 URL。也就是说，本期 SDK 是后续 CLI 的唯一 transport 入口，而不是临时过渡层。

CLI 接入时应复用 SDK 的 active tenant path helper。不要从 profile 中读取 display-only tenant id 后直接拼进 URL；如果 CLI 当前无法可靠提供 active tenant id，应先补 CLI factory / auth result 到 SDK context 的传递，而不是让每个命令各自处理 tenant。

## 9. 验收标准

验收满足七点即可通过：

1. `client/ontology_review.go` 独立存在，未污染 `chunk.go` / `knowledge.go` 职责边界
2. 四个 ontology review 方法可以成功调用对应后端 endpoint，且普通公共方法不要求调用方重复传入 `tenantID`
3. queue 返回分页 DTO 和轻量展示字段，而不是不可分页的 `[]Entry` metadata slice
4. detail 返回 typed DTO，并保留后端 full chunk payload 中的普通 chunk 上下文字段与 review 字段
5. action request 使用 typed action 常量和 `*OntologyMicroTBox`，能区分 omitted、空对象和非空对象
6. `OntologyMicroTBox` 覆盖后端当前完整公开 wire contract，mixed-case JSON tag 和 pointer/nullability 有测试保护
7. `client/ontology_review_test.go` 通过，错误测试确认结构化错误 payload 不被丢弃，且不引入对 CLI 的实现依赖
