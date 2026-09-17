# NetworkGuardian 文档索引

当前交付形态是 **Native AOT 单文件便携版**（`release\NetworkGuardian.exe`，7.8 MB，自绘 Win32 UI；`release\` 为发布目录，已在 `.gitignore` 内）。
`docs\session-log-2026-09-17.md` 与其中的界面截图描述的是已被取代的 WinUI 3 版本，仅作历史参考。

| 文件 | 内容 | 适用场景 |
| --- | --- | --- |
| [`../README.md`](../README.md) | 项目主文档：交付形态与体积、功能、系统要求、编译/测试/打包、运行与 QA 开关、粘性规则与恢复顺序、校园网认证、物理/虚拟网卡判定、原生 API 清单、UI 与托盘、管理员权限、位置权限、配置与日志路径、Native AOT 约束、已知限制、安全边界、本机实测 | 首次了解项目 / 部署使用 / 改动前必读约束 |
| [`session-log-2026-09-18.md`](session-log-2026-09-18.md) | 小体积改造的完整记录：体积账（每步实测）、真机验证证据、**改造中暴露的 8 个缺陷与根因**、关键设计决策、提交历史、复现命令、未验证项 | 交接、回溯「为什么自绘 UI 这么写」、复现验证 |
| [`portable-small-build-plan.md`](portable-small-build-plan.md) | 改造前的体积矩阵（WinUI 248.8 MB / WinForms 68 MB / 裁剪自包含 ~10 MB / Native AOT 2.1–4.7 MB）、体积构成分析、可行性与阶段计划（顶部已标明实施结果） | 评估「为什么必须换 UI 框架」、看历史决策依据 |
| [`session-log-2026-09-17.md`](session-log-2026-09-17.md) | WinUI 3 版本的会话记录：真机验证证据、7 个真机缺陷与修复、设计决策 | 历史参考（当前代码已不含 WinUI） |
| `ui-dashboard.png` | 便携版总览页实际截图 | 界面现状 |
| `ui-wireless.png` | 便携版无线网卡页（按网卡扫描、已保存配置、设备列表） | 界面现状 |
| `ui-ethernet.png` | 便携版以太网页（物理接口、全部接口明细、DNS/MAC、PnP 设备） | 界面现状 |
| `ui-settings.png` | 便携版设置页（分组卡片、开关、步进、文本字段） | 界面现状 |
| `ui-logs.png` | 便携版日志页（级别/分类/搜索筛选、实时缓冲） | 界面现状 |

## 快速定位

- 想看「为什么体积能降到 7.8 MB、每一步省了多少」 → `session-log-2026-09-18.md` 第 3 节
- 想看「自绘 UI 有哪些坑（不绘制、窗口不响应、退出残留）」 → `session-log-2026-09-18.md` 第 5 节
- 想看「哪些能在真机上复现、怎么复现」 → `session-log-2026-09-18.md` 第 4、8 节
- 想看「改代码时有什么硬约束」 → `../README.md` 第 16 节（Native AOT 约束）
- 想看「体积里都是什么、为什么 WinUI 不可能变轻」 → `portable-small-build-plan.md` 第 1、2、3 节

## 验证脚本速查

| 命令 | 断言 |
| --- | --- |
| `tools\build-portable.ps1` | 构建 0 警告 + 196 测试通过 + 单文件 ≤ 8 MB + 无 PDB |
| `tools\verify-portable.ps1` | 中性目录 + 干净环境启动、窗口类存在、**窗口线程应答消息**、日志/配置副作用、提权助手真实执行、关闭链路、无残留 |
| `tools\smoke-run.ps1` | 隔离配置根下的真机冒烟（枚举/探测/日志） |
| `tools\shutdown-test.ps1` | `WM_CLOSE` 后关闭序列完整且进程自退 |
| `tools\ui-screenshot.ps1` | 页面截图（`PrintWindow`，不抢焦点） |
