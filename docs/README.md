# 文档导航

当前维护基线为 **0.14.0 / 配置 Schema v15**。版本和验收数字以对应交付记录为准；历史截图、设计方案和早期功能记录不能替代当前实现。

## 使用软件

| 文档 | 内容 |
| --- | --- |
| [项目 README](../README.md) | 功能、第一次使用、MCP、数据位置和源码构建入口 |
| [中文使用说明 PDF](user-guide/NetworkGuardian-User-Guide.pdf) | 从首次打开软件到网络管理、模型、MCP 和 Agent 蒸馏 |
| [使用说明 LaTeX](user-guide/NetworkGuardian-User-Guide.tex) | PDF 可编辑源码，重建方法见开发文档 |
| [故障排查](troubleshooting.md) | 网络、设备、配置、MCP 和训练问题的证据与处理顺序 |
| [维护与恢复](maintenance.md) | 升级、备份、恢复、训练任务清理和维护交接 |

## 开发与接手维护

| 文档 | 内容 |
| --- | --- |
| [贡献说明](../CONTRIBUTING.md) | 改动流程、验证范围、提交和问题报告要求 |
| [架构与约束](architecture.md) | 项目边界、运行链路、策略优先级、AOT、UI、存储和 MCP 扩展点 |
| [开发、测试和发布](development.md) | 环境准备、命令、脚本副作用、发布验收与 Skill/PDF 产物 |

## 功能专题

| 文档 | 范围 |
| --- | --- |
| [质量与偏好](quality-preferences.md) | 质量采样、SSID 强绑定、组合分配、早期偏好策略与性能设计 |
| [问答与网络模型](network-brain.md) | 60 道题、回答可信度、8→8→1 种子模型、数据与置信度 |
| [质量评价报告](quality-report.md) | 自然语言解释的测量依据和限制 |
| [保持禁用与侧栏](device-disable-and-navigation.md) | 0.12.0 引入的设备规则、功能分类和确认范围 |
| [MCP 服务器](mcp-server.md) | 当前 41 个工具、HTTP 行为、隔离验证和专用蒸馏接口 |
| [Agent Skill](../skills/networkguardian/SKILL.md) | 可导入 Agent 的网络操作和长期训练流程 |
| [蒸馏训练协议](../skills/networkguardian/references/training.md) | 特征、教师标签、任务恢复、验证门槛与部署回滚 |

## 版本与历史

| 文档 | 定位 |
| --- | --- |
| [0.14.0 交付](release-0.14.0.md) | 当前模型训练接口、Skill、389 项测试与最终产物哈希 |
| [0.13.0 交付](release-0.13.0.md) | 首次本机 HTTP MCP 与使用说明交付；数字对应当时版本 |
| [2026-10-16 运行检查](runtime-check-2026-10-16.md) | 当时真实运行数据中的模型更新证据 |
| [2026-10-18 稳定性修复](stability-fixes-2026-10-18.md) | 恢复期间探测、路由写入与 TCP 提权清理的候选修复及验证范围 |
| [小体积改造方案](portable-small-build-plan.md) | 早期 UI/AOT 选型与体积实验，保留历史依据 |

仓库已删除的早期 session-log 和 EAP 专题不再作为当前文档入口；需要回溯时从 Git 历史查阅。例如 `git log --all -- docs/eap-credential-library.md`。历史 UI 截图用于了解旧版外观，当前侧栏和行为以用户说明、代码及实测为准。
