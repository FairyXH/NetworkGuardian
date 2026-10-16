# 本机 HTTP MCP（0.14.0）

在“自动管理 → AI 工具接入 / MCP”开启，默认关闭，固定默认端口 18765，可修改为 1–65535。
端点为 `http://127.0.0.1:18765/mcp`，没有鉴权。地址写死为 `IPAddress.Loopback`，没有监听地址设置，不监听 IPv6 或其他网卡。端口占用显示失败，不自动换端口；退出停止服务，隐藏到托盘仍运行。

## 协议与功能

JSON-RPC 2.0 + Streamable HTTP，支持 2025-11-25、2025-06-18、2025-03-26 的协商；提供初始化、ping、工具发现与调用。
使用无状态 JSON 响应，不发放会话 ID；通知返回 HTTP 202，GET/DELETE `/mcp` 返回 405，不提供旧式 SSE 地址。
校验 Host 和存在时的 Origin，只接受相同端口的回环 HTTP 来源。HTTP/1.1 支持 Content-Length、chunked、UTF-8、100-continue；请求体最多 2 MiB、头部 16 KiB、并发连接最多 16，读取超时 15 秒、单次处理最多 3 分钟。

41 个工具覆盖概览/质量/设备/路由，网卡启用、保持禁用与重启，无线电，扫描/连接/断开/配置删除/SSID 绑定，校园标记，全部配置合并和重载，凭据库，认证命令，模型问答/偏好/导出/删除/重置，日志/权重/实际决策/调试汇总，以及独立模型蒸馏训练。
完整索引在使用说明附录，参数由 `tools/list` 的 JSON Schema 返回。

写工具直接执行，不触发界面确认；原有 Windows UAC 仍适用。写工具串行化，复用应用配置锁与原子保存，变更同步到界面。
设备识别、保持禁用和 SSID 指定仍优先。命令工具按已有配置 ID 执行，没有独立任意命令文本参数。
配置递归合并、数组整体替换，拒绝未知字段、错误类型、越界值；版本只读。SSID 字典支持新增/移除键。
密码默认隐藏，显式 `includeSecrets=true` 可读取内存中可解密密码；不记录工具参数。
用户问答反馈通过当前题目与现场快照评估，不接收伪造探测值或任意权重覆盖。Agent 教师标签使用专用蒸馏接口，单独记录来源。

可以通过 MCP 改自身端口或关闭监听：本次结果仍返回，下一次调用使用新地址。主动关闭后客户端可能报告连接断开，这是停止服务的结果。
已受理、规则保存、命令启动、网卡配置、连接关联与外网验证分别报告，不把它们混为成功。

## 验证与复现

389 项测试通过，覆盖协议、参数、回环访问、来源拒绝、chunked 中文、端口冲突、停止与重开，以及后台训练、数据冻结、暂停/重载续训、过期基线保护、持久化部署回滚、备份恢复和旧任务清理。
官方 Python MCP 客户端对 Native AOT EXE 完成 70 余次隔离调用，包括信息序列化、配置校验/重载、校园标记、SSID 字典、实际扫描与探测、删除不存在的测试配置、运行无副作用 `exit 0` 并核对退出码、禁用测试凭据增删、答题反馈/导出/删除/重置、错误结果、端口热切换和自停用，以及 160 条数值标签、300 epochs 后台训练、验证、部署、哈希一致的回滚。Skill 的标准库客户端也经过真实服务器验证。
真实在用网卡未被禁用、重启或切换；硬件控制沿用原 Windows 后端与 UAC，仍以实际硬件返回为准。隔离实例不改写用户的开机启动注册。

```powershell
dotnet test tests/NetworkGuardian.Tests/NetworkGuardian.Tests.csproj -c Release
python tools/qa-mcp.py --exe artifacts/release-0.14.0/NetworkGuardian.exe --root artifacts/qa-mcp
pwsh -File tools/build-user-guide.ps1
```

依据 [Streamable HTTP 规范](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports) 和 [工具规范](https://modelcontextprotocol.io/specification/2025-11-25/server/tools) 实现。

## 独立蒸馏接口

- `training_status`：任务列表或单任务进度、误差、模型哈希；只读。
- `training_examples`：读取服务器生成的样例，支持分页和仅未标注项；只读。
- `model_distill`：`create / label / start / pause / evaluate / deploy / rollback / delete`。create 的 requestId 幂等；训练在后台执行，每 5 epochs 保存检查点，HTTP 超时/断线不会取消。软件重启后使用 start 续训。

样例只能由服务器生成，区分 synthetic 和 observed-aggregate；不允许教师伪造实测或上传任意权重。特征、标签格式和完整调用顺序见 [Skill 训练协议](../skills/networkguardian/references/training.md)。
训练数据开始后冻结，特征哈希固定约 20% 留出；内置专家锚定与独立专家场景限制退化。门槛合格才允许部署；当前模型相对训练基线变化则拒绝。回滚必须与部署版本匹配，保留后续个人学习。模型仍受设备禁用、SSID 指定和原有决策约束。

任务保存在数据目录 `distillation/<jobId>.json`，原子替换并保留 `.bak`；每任务最多 512 条样例、1000 epochs，最多 64 任务、同时运行一个。可删除未部署或已经被其他部署替代的旧任务；当前部署及运行中的任务不能删除。查询默认返回摘要，避免把所有权重和样例塞进 Agent 上下文。

项目中的 [Skill](../skills/networkguardian/SKILL.md) 可整体导入 Agent，包含长任务预算、断线恢复、可靠调用和网络诊断方法。Agent 平台仍可能中断；Skill 不承诺无限运行。教师由 Agent 担任，无须在 EXE 或 Skill 内保存模型服务密钥。
