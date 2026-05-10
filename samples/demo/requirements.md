# 示例需求：Todo API 服务

## 功能需求

1. **创建 Todo**：POST /api/todos，接收 title 和 description，返回创建的 Todo 对象（含 id）
2. **查询 Todo 列表**：GET /api/todos，支持按 completed 状态过滤
3. **更新 Todo**：PUT /api/todos/{id}，支持更新 title、description、completed 状态
4. **删除 Todo**：DELETE /api/todos/{id}，软删除

## 技术约束

- 使用 ASP.NET Core 10 Web API
- 数据存储使用 SQLite（通过 Microsoft.Data.Sqlite）
- 所有接口返回标准 JSON
- 每个接口有对应的 xUnit 集成测试

## 验收标准（DoD）

- [ ] 所有 4 个接口实现完整
- [ ] 单元测试覆盖率 > 80%
- [ ] 无编译错误
- [ ] dotnet test 全部通过
