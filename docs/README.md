# NetworkGuardian 文档索引

| 文件 | 内容 | 适用场景 |
| --- | --- | --- |
| [`../README.md`](../README.md) | 项目主文档：功能、系统要求、编译运行、粘性连接规则、恢复顺序、校园网认证与离线命令配置、物理网卡判定、原生 API 清单、管理员权限、位置权限、配置/日志路径、已知限制、安全边界、发布打包 | 首次了解项目 / 部署使用 |
| [`session-log-2026-09-17.md`](session-log-2026-09-17.md) | 开发会话记录：交付物清单、目录结构、真机验证证据、真机暴露的 7 个缺陷与修复、关键设计决策与理由、未验证项、提交历史、复现命令 | 交接、回溯"为什么这样设计"、复现验证 |
| [`portable-small-build-plan.md`](portable-small-build-plan.md) | 小体积单文件便携版方案：Native AOT / 裁剪自包含 / WinForms 的实测体积对比、当前 248.8 MB 包的体积构成、去 WinUI 与去 WinRT 的可行性与实施阶段、AOT 约束清单、风险与回退、待确认决策项 | 决定是否做便携版、评估成本与体积 |
| `ui-dashboard.png` | 总览页实际窗口截图 | 界面现状 |
| `ui-wireless.png` | 无线网卡页（含已保存配置、按网卡扫描结果） | 界面现状 |
| `ui-ethernet.png` | 以太网页（物理以太网与全部接口两张表） | 界面现状 |
| `ui-settings.png` | 设置页 | 界面现状 |
| `ui-logs.png` | 日志页（级别/分类筛选、实时缓冲） | 界面现状 |
| `ui-release-dashboard.png` | **发布包**中 exe 实际运行的总览页截图 | 发布验证证据 |

## 快速定位

- 想看"为什么某个值/常量必须写成这样" → `session-log-2026-09-17.md` 第 4 节（缺陷与根因）+ 第 5 节（设计决策）
- 想看"哪些能在真机上复现、怎么复现" → `session-log-2026-09-17.md` 第 3、8 节
- 想看"能不能做成 10 MB 单文件、怎么做" → `portable-small-build-plan.md` 第 1、4 节
- 想看"体积里都是什么、能不能裁" → `portable-small-build-plan.md` 第 2、3 节
