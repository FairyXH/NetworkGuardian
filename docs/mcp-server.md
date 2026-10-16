# 本机 HTTP MCP（0.13.0）

在“自动管理 → AI 工具接入 / MCP”开启，默认关闭，固定默认端口 18765，可修改为 1–65535。
端点为 `http://127.0.0.1:18765/mcp`，没有鉴权。地址写死为 `IPAddress.Loopback`，没有监听地址设置，不监听 IPv6 或其他网卡。端口占用显示失败，不自动换端口；退出停止服务，隐藏到托盘仍运行。

## 协议与功能

JSON-RPC 2.0 + Streamable HTTP，支持 2025-11-25、2025-06-18、2025-03-26 的协商；提供初始化、ping、工具发现与调用。
使用无状态 JSON 响应，不发放会话 ID；通知返回 HTTP 202，GET/DELETE `/mcp` 返回 405，不提供旧式 SSE 地址。
校验 Host 和存在时的 Origin，只接受相同端口的回环 HTTP 来源。HTTP/1.1 支持 Content-Length、chunked、UTF-8、100-continue；请求体最多 2 MiB、头部 16 KiB、并发连接最多 16，读取超时 15 秒、单次处理最多 3 分钟。

38 个工具覆盖概览/质量/设备/路由，网卡启用、保持禁用与重启，无线电，扫描/连接/断开/配置删除/SSID 绑定，校园标记，全部配置合并和重载，凭据库，认证命令，模型问答/偏好/导出/删除/重置，日志/权重/实际决策/调试汇总。
完整索引在使用说明附录，参数由 `tools/list` 的 JSON Schema 返回。

写工具直接执行，不触发界面确认；原有 Windows UAC 仍适用。写工具串行化，复用应用配置锁与原子保存，变更同步到界面。
设备识别、保持禁用和 SSID 指定仍优先。命令工具按已有配置 ID 执行，没有独立任意命令文本参数。
配置递归合并、数组整体替换，拒绝未知字段、错误类型、越界值；版本只读。SSID 字典支持新增/移除键。
密码默认隐藏，显式 `includeSecrets=true` 可读取内存中可解密密码；不记录工具参数。
模型反馈通过当前题目与现场快照评估，不接收伪造探测值或任意权重覆盖。

可以通过 MCP 改自身端口或关闭监听：本次结果仍返回，下一次调用使用新地址。主动关闭后客户端可能报告连接断开，这是停止服务的结果。
已受理、规则保存、命令启动、网卡配置、连接关联与外网验证分别报告，不把它们混为成功。

## 验证与复现

381 项测试通过，覆盖协议、参数、回环访问、来源拒绝、chunked 中文、端口冲突、停止与重开。
官方 Python MCP 客户端对最终 Native AOT EXE 完成 53 次隔离调用，包括信息序列化、配置校验/重载、校园标记、SSID 字典、实际扫描与探测、删除不存在的测试配置、运行无副作用 `exit 0` 并核对退出码、禁用测试凭据增删、答题反馈/导出/删除/重置、错误结果、端口热切换和自停用。
真实在用网卡未被禁用、重启或切换；硬件控制沿用原 Windows 后端与 UAC，仍以实际硬件返回为准。隔离实例不改写用户的开机启动注册。

```powershell
dotnet test tests/NetworkGuardian.Tests/NetworkGuardian.Tests.csproj -c Release
python tools/qa-mcp.py --exe artifacts/release-0.13.0/NetworkGuardian.exe --root artifacts/qa-mcp
pwsh -File tools/build-user-guide.ps1
```

依据 [Streamable HTTP 规范](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports) 和 [工具规范](https://modelcontextprotocol.io/specification/2025-11-25/server/tools) 实现。
