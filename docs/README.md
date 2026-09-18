# NetworkGuardian 文档索引

当前交付形态是 **Native AOT 单文件便携版**（`release\NetworkGuardian.exe`，8.24 MB，自绘 Win32 UI，提权助手已内置；`release\` 为发布目录，已在 `.gitignore` 内）。
`docs\session-log-2026-09-17.md` 与其中的界面截图描述的是已被取代的 WinUI 3 版本，仅作历史参考。

| 文件 | 内容 | 适用场景 |
| --- | --- | --- |
| [`../README.md`](../README.md) | 项目主文档：交付形态与体积、功能、系统要求、编译/测试/打包、运行与 QA 开关、粘性规则与恢复顺序、校园网认证、物理/虚拟网卡判定、原生 API 清单、UI 与托盘、管理员权限、位置权限、配置与日志路径、Native AOT 约束、已知限制、安全边界、本机实测 | 首次了解项目 / 部署使用 / 改动前必读约束 |
| [`session-log-2026-09-18.md`](session-log-2026-09-18.md) | 小体积改造的完整记录：体积账（每步实测）、真机验证证据、**改造中暴露的 8 个缺陷与根因**、关键设计决策、提交历史、复现命令、未验证项 | 交接、回溯「为什么自绘 UI 这么写」、复现验证 |
| [`eap-credential-library.md`](eap-credential-library.md) | 自维护无线网络库（802.1X/EAP）：需求映射、数据与存储、生成的两份文档、**真机接受/拒绝矩阵（含 WLAN 原因码）**、看门狗设计理由、真机证据、体积账、8 个缺陷根因、未验证边界 | 理解或改 802.1X 相关代码前必读 |
| [`session-log-2026-09-18-8021x.md`](session-log-2026-09-18-8021x.md) | 802.1X 库 + 软开关看门狗的会话记录：交付物、关键决策、真机证据、体积账、复现命令、未验证项 | 交接与复现该功能 |
| [`portable-small-build-plan.md`](portable-small-build-plan.md) | 改造前的体积矩阵（WinUI 248.8 MB / WinForms 68 MB / 裁剪自包含 ~10 MB / Native AOT 2.1–4.7 MB）、体积构成分析、可行性与阶段计划（顶部已标明实施结果） | 评估「为什么必须换 UI 框架」、看历史决策依据 |
| [`session-log-2026-09-17.md`](session-log-2026-09-17.md) | WinUI 3 版本的会话记录：真机验证证据、7 个真机缺陷与修复、设计决策 | 历史参考（当前代码已不含 WinUI） |
| `ui-dashboard.png` | 便携版总览页实际截图 | 界面现状 |
| `ui-wireless.png` | 便携版无线网卡页（按网卡扫描、已保存配置、设备列表） | 界面现状 |
| `ui-credentials.png` | 便携版网络凭据库页（802.1X 账号条目、密码遮罩、写入到所有网卡） | 界面现状 |
| `ui-ethernet.png` | 便携版以太网页（物理接口、全部接口明细、DNS/MAC、PnP 设备） | 界面现状 |
| `ui-settings.png` | 便携版设置页（分组卡片、开关、步进、文本字段） | 界面现状 |
| `ui-logs.png` | 便携版日志页（级别/分类/搜索筛选、实时缓冲） | 界面现状 |

## 快速定位

- 想看「为什么体积能降到 7.8 MB、每一步省了多少」 → `session-log-2026-09-18.md` 第 3 节
- 想看「自绘 UI 有哪些坑（不绘制、窗口不响应、退出残留）」 → `session-log-2026-09-18.md` 第 5 节
- 想看「哪些能在真机上复现、怎么复现」 → `session-log-2026-09-18.md` 第 4、8 节
- 想看「改代码时有什么硬约束」 → `../README.md` 第 16 节（Native AOT 约束）
- 想看「点击为什么无效、助手怎么并进主程序」 → `session-log-2026-09-18.md` 第 10 节（点击命中链修复 + 真·单文件）
- 想看「被禁用的网卡为什么没自动打开、重复 SSID 怎么处理」 → `session-log-2026-09-18.md` 第 11 节
- 想看「设置里每块网卡的 Wi-Fi 分开关（radio_state）怎么被自动打开」 → `session-log-2026-09-18.md` 第 12 节
- 想看「企业级 Wi-Fi（802.1X/EAP）的账号怎么维护、配置怎么生成」 → `eap-credential-library.md` 第 2、3 节
- 想看「哪些 802.1X 配置被 WLAN 服务接受、哪些被拒（原因码）」 → `eap-credential-library.md` 第 3 节
- 想看「软开关看门狗为什么是独立循环、怎么验证」 → `eap-credential-library.md` 第 6、7 节
- 想看「体积里都是什么、为什么 WinUI 不可能变轻」 → `portable-small-build-plan.md` 第 1、2、3 节

## 验证脚本速查

| 命令 | 断言 |
| --- | --- |
| `tools\build-portable.ps1` | 构建 0 警告 + 257 测试通过 + 单文件 ≤ 8.5 MB（包内只允许一个 exe）+ 无 PDB（门限理由与逐步实测见脚本头部） |
| `tools\verify-portable.ps1` | 中性目录 + 干净环境启动、窗口类存在、**窗口线程应答消息**、日志/配置副作用、**GUI 实例运行时 `--helper` 仍应答并真实执行**、关闭链路、无残留（33 项；体积门限从构建脚本读取） |
| `tools\qa-radio-watchdog.ps1` | 真机软开关看门狗：关掉一块网卡的软开关，要求 3 秒内被重新打开，并断言日志出现看门狗行 |
| `tools\qa-wifi-radio-winrt.ps1` | 读取/翻转单块网卡的软开关（无线电管理器路径，Windows PowerShell 5.1） |
| `tools\dump-wlan-profile.ps1` | 导出网卡上已保存的配置 XML（对照 802.1X 结构用） |
| `tools\qa-ui-click.ps1` | 自绘 UI 交互回归：`PrintWindow` 抓图 + 标定后模拟点击（客户端坐标），读 debug 命中日志 |
| `tools\smoke-run.ps1` | 隔离配置根下的真机冒烟（枚举/探测/日志） |
| `tools\shutdown-test.ps1` | `WM_CLOSE` 后关闭序列完整且进程自退 |
| `tools\ui-screenshot.ps1` | 页面截图（`PrintWindow`，不抢焦点） |
