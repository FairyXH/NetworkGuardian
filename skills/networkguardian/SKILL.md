---
name: networkguardian
description: 通过 NetworkGuardian 本机 HTTP MCP 查询和管理网络、诊断故障、担任教师蒸馏网络管家评分模型，并恢复持久化训练任务。适用于 NetworkGuardian；不用于训练通用大语言模型。
---

# NetworkGuardian Agent

使用已连接的 NetworkGuardian MCP；默认 `http://127.0.0.1:18765/mcp`，端口以用户设置为准。先发现工具，再查询 `guardian_status`、`training_status` 和相关配置。当前版本提供 41 个工具，包括专用 `model_distill`。只读查询无须额外批准；已授权范围内的写操作直接推进。

## 蒸馏与续训

当用户说“开始蒸馏”“继续训练”时，按 [训练协议](references/training.md) 执行。你就是教师；不需要用户再次提供大模型密钥。训练对象是 8→8→1 数值网络效用模型。

默认执行一个可恢复训练批次（160 条，300 epochs）。用户要求长期训练时，默认预算为 60 分钟、最多 12 批次，连续两批验证未改善则停止并报告。用户指定的预算优先。不要为消耗时长反复重训同一批数据。获得蒸馏授权后，完成标注、启动、验证和门槛内的部署，无需每批确认。

优先恢复尚未完成的任务；创建时使用可辨识且稳定的 `requestId`。每批完成后记录 jobId、状态、验证误差和部署结果；上下文压缩或 Agent 重启后从服务器状态恢复，不依赖对话记忆。服务器后台训练不依赖连接，软件重启后暂停，调用 start 续训。

Agent 平台仍可能因会话时间、配额或进程退出而停止；Skill 不能绕过这些限制。退出前尽量提交已经生成的标签，报告 jobId 和下一步。不得声称 Skill 保证 Agent 永不中断，也不要擅自创建定时任务。

## 网络管理与诊断

- 先查询真实设备 GUID、PnP ID、SSID、profile、配置和质量报告，使用工具返回的标识；不得猜测网卡或保存配置名称。
- 区分链路已连接、取得 IP、外网探测成功和实际默认出口。旧采样不代表当前状态。效用值与任务置信度都不是成功概率。
- 保持禁用与 SSID 指定优先于自动策略和模型。训练授权本身不授权断网、重启网卡、改绑定、凭据或执行认证命令。
- 配置修改用最小 `config_patch`；数组整体替换，先读取原数组。检查 `success`、`isError`、操作结果、保存结果以及后续实测。`accepted` 不等于已完成。
- 连接/扫描只用真实返回的 adapter/profile；无网络动作必要时不要以反复扫描、探测、切换制造训练数据。先查 `debug_decision`、过滤后的 `debug_logs`，再扩大查询；不要默认导出密码或全量日志。
- `learning_answer` 是用户问答，不作为教师标签入口。不得代替用户编造个人偏好；教师反馈只用 `model_distill label`。
- 连接超时后先读状态再判断是否重试。对网卡控制、配置变更、命令执行不盲目重放。蒸馏 create 的 requestId、相同 label 和 start 可重试。

## 客户端适配

优先使用 Agent 已注册的 MCP 工具。没有 MCP 连接器时，可使用标准库客户端 [scripts/ng_mcp.py](scripts/ng_mcp.py)：

```text
python <skill目录>/scripts/ng_mcp.py tools
python <skill目录>/scripts/ng_mcp.py status
python <skill目录>/scripts/ng_mcp.py call model_distill --args-file <参数JSON路径>
python <skill目录>/scripts/ng_mcp.py wait <jobId> --seconds 60
```

全局 `--url http://127.0.0.1:端口/mcp` 放在子命令前。客户端只接受 IPv4 回环，不自动重试写请求；写入超时后查询服务器。服务关闭/未安装时明确报告具体连接错误和设置入口，不伪造工具结果。

导入：将整个 `networkguardian` 文件夹放入 Agent 的 skills 目录（Codex 为 `~/.codex/skills/`），并在软件设置开启 MCP。Skill 目录可整体复制，也可解压发布的同名 ZIP；不同 Agent 的 MCP 注册方式以其客户端为准。

最短调用示例：`使用 $networkguardian，持续蒸馏网络模型 1 小时，通过验证就部署；中断后继续已有任务。`
