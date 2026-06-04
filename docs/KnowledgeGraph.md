# WeKnora 知识图谱

## 快速开始

- .env 配置相关环境变量
    - 启用 Neo4j: `NEO4J_ENABLE=true`
    - Neo4j URI: `NEO4J_URI=bolt://neo4j:7687`
    - Neo4j 用户名: `NEO4J_USERNAME=neo4j`
    - Neo4j 密码: `NEO4J_PASSWORD=password`

    如果使用仓库自带的 `docker compose` 启动整套服务，并且这里设置了
    `NEO4J_ENABLE=true`，则后续启动 WeKnora 时也必须带上 `--profile neo4j`
    （或 `--profile full`）；否则 `app` 会在启动阶段等待 `neo4j:7687`。

- 启动 Neo4j
```bash
docker compose --profile neo4j up -d
```

- 重建或重启 WeKnora 时保持同一个 profile
```bash
docker compose --profile neo4j up -d --build
```

- 如果当前不需要知识图谱功能，可将 `.env` 中的 `NEO4J_ENABLE` 改回 `false`

- 在知识库设置页面启用实体和关系提取，并根据提示配置相关内容

## 生成图谱

上传任意文档后，系统会自动提取实体和关系，并生成对应的知识图谱。

![知识图片示例](./images/graph3.png)

## 查看图谱

登陆 `http://localhost:7474`，执行 `match (n) return (n)` 即可查看生成的知识图谱。

在对话时，系统会自动查询知识图谱，并获取相关知识。
