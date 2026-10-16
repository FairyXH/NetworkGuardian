# 架构与维护约束

本文对应 0.14.0，描述当前代码边界。构建命令见 [开发与发布](development.md)，运行数据处理见 [维护与恢复](maintenance.md)。

## 项目分层

| 项目 / 目录 | 职责 | 主要入口 |
| --- | --- | --- |
| `src/NetworkGuardian.Core` | 配置、领域模型、决策、质量/偏好/神经网络策略、JSON 元数据、EAP 配置生成 | `Policies/GuardianDecisionEngine.cs`、`Configuration/GuardianConfig.cs` |
| `src/NetworkGuardian.Windows` | WLAN、PnP、接口/路由、按源探测、Npcap、无线电、DPAPI、托盘和提权助手实现 | `Connectivity/`、`Devices/`、`Wlan/`、`Radio/` |
| `src/NetworkGuardian.Infrastructure` | JSON 存储、历史/凭据、日志、外部进程、MCP 协议及持久训练任务 | `Configuration/`、`Mcp/` |
| `src/NetworkGuardian.Portable` | 应用生命周期、后台协调、Win32/GDI 界面、MCP 与真实操作的适配 | `Program.cs`、`Host/PortableApp.cs`、`Services/GuardianHostService.cs` |
| `src/NetworkGuardian.Helper` | 可单独构建的助手入口；便携版使用主 EXE 内置助手，不分发第二个 EXE | 对应项目与 Windows 层 `HelperEntry` |
| `tests/NetworkGuardian.Tests` | 策略、配置、互操作布局、MCP、持久化及训练测试 | 测试项目不引用 Portable UI |
| `tools/NetworkGuardian.Training` | 离线种子训练和已有教师数据复现，不在主解决方案中 | `Program.cs`、`teacher-distillation.json` |
| `skills/networkguardian` | Agent 行为约定、蒸馏协议与标准库客户端 | `SKILL.md` |

Core 不依赖 Windows 实现；Portable 负责组装与执行。系统控制应复用 Host/Windows 后端，避免 UI 和 MCP 各自实现一套状态变更。

## 运行链路

```text
Program：助手分支 / 单实例检查
  → PortableApp：加载配置、Host、窗口、托盘、MCP
  → GuardianHostService：枚举、探测、质量采样、策略输入
  → GuardianDecisionEngine：生成动作和解释
  → Host 执行：再次检查规则 → Windows API / 按需 UAC → 返回真实结果
  → 快照、日志、任务记录、质量报告 → UI / MCP
```

后台有主监测、软件无线电看门狗、路由看门狗及独立质量采样循环。设备/WLAN 通知可触发刷新；耗时探测和训练不能放到窗口消息线程，也不能阻塞路由看门狗。停止时应取消和等待后台工作，再保存历史并释放系统资源。

全局在线结论、当前默认出口、单接口探测和最近质量估计是不同数据。诊断“备用卡在线但业务不通”时，应核对默认出口，不能用任意备用接口成功掩盖当前出口失败。

## 策略约束

| 约束 | 维护要求 |
| --- | --- |
| 物理设备识别 | 虚拟/TUN/TAP 不能作为物理修复或原始发包目标 |
| 保持禁用 | `DeviceDisablePolicy` 同时影响策略输入和执行入口；独立于暂停/自动恢复，不允许通过手动连接、重启或无线电操作绕过 |
| SSID 指定 | `WifiBindingPolicy` 约束所有 SSID；沿用兼容字段 `wifi.campusWifiAdapterAssignments`，不能误恢复为仅校园网生效 |
| 稳定连接 | 保留可用连接；低质量迁移应先验证替代连接，具备超时和冷却 |
| 候选分配 | `CandidateSelector` 过滤，`AdapterAssignmentPlanner` 做有界组合搜索；不能把信号分数公式当成全部最终评分 |
| 恢复控制 | 失败次数、黑名单、退避、冷却、熔断和新出口确认需要一起维护 |
| 模型影响 | 质量和可信度门槛先于有限评分调整；教师标签不能直接改变硬绑定或设备规则 |
| 证据与动作 | 已保存规则、已启动命令、WLAN 已关联、外网已验证分别报告，不合并成一个成功状态 |

配置变更与动作执行之间存在时间差，规则既要在决策时检查，也要在真实执行前检查。修改恢复路径时，同时验证关闭自动恢复、暂停、保持禁用和过期动作的行为。

## 质量、问答与模型

`NetworkQualityPolicy` 从网卡/SSID/频段采样估计质量，`NetworkQualityReporter` 用确定性规则生成中文说明；报告不是神经网络自由生成的文本。TCP 失败不等于物理丢包，ICMP 无回应不一定是链路故障，没有吞吐测量就不能声称测速结果。

`NetworkQuestionBank` 生成带上下文的选择题；`SurveyLearning` 评估用户答案的可信度。用户回答是偏好/软证据，不能代替实际探测或直接修改校园网标记。`PreferencePolicy` 与 8→8→1 `NetworkBrain` 协作调整评分，输出是效用，任务置信度是启发式证据评分。

`BrainDistillation` 定义教师训练、固定特征哈希留出和专家退化检查。`DistillationService` 持久保存任务、运行后台训练并管理部署/回滚；`NetworkHistoryStore.ReplaceBrainAsync` 使用模型哈希比较，串行化写入权威历史后发布状态。新增个人学习会使旧基线失效。

## 存储与并发

- `JsonConfigStore` 管配置，`ConfigMigrator` 管迁移，`ConfigValidator` 管范围和空值规范化，`ConfigMerge` 处理不同页面的过期草稿合并。
- `NetworkHistoryStore` 统一管理历史、偏好、问答修订与模型发布；`network-brain.json` 只是副本。修改权重不能绕开历史锁、修订检查和原子保存。
- 蒸馏任务在 `distillation/<jobId>.json`，每 5 epochs 检查点及 `.bak`。HTTP 取消不取消后台任务；退出或重启后依靠持久进度续训。
- 发布给 UI/策略的模型和集合应作为不可变快照使用。不要在持有 UI 锁时等待探测、UAC、训练或磁盘异步操作。

文件关系与恢复限制见 [维护文档](maintenance.md)。

## AOT 与 UI

Portable 使用 Win32/GDI 自绘界面，`Ui/Pages/` 定义页面，`Widgets`、`Canvas` 和 `MainWindow` 负责控件、绘制、命中测试及消息分发。修改布局需同时考虑 DPI、滚动后坐标、确认框和资源释放；避免每帧创建无界 GDI 对象或缓存。

发布开启 Native AOT，关闭反射 JSON 回退和内置 COM 互操作。新持久化类型或 MCP 输出类型应通过 `Core/Serialization/NetworkGuardianJson.cs` 注册源生成元数据，并验证最终 AOT EXE。普通 JIT 测试通过不能证明裁剪后的序列化或 Windows 互操作可用。

不要引入依赖动态代码生成、未约束反射或完整 Windows SDK .NET 投影的库而只检查编译。需要新依赖时实测 AOT 兼容性和体积；13.5 MiB 门禁调整必须有实测与发布记录。

## 扩展 MCP

1. 在 `Infrastructure/Mcp/McpTools.cs` 定义工具名、参数 Schema、必填、范围和只读标注。
2. 在 `Portable/Mcp/GuardianMcpBackend.cs` 适配到现有配置/Host API；输入错误、真实失败和仅受理分别返回。
3. 为新增返回类型补 AOT 元数据；默认不输出密码、加密密文或可能含密钥的自定义 XML。
4. 增加相应协议、后端或持久化测试，并在隔离的最终 EXE 上通过官方 MCP 客户端验证。
5. 同步 MCP 文档、Skill、用户工具索引和交付记录。

服务器由 `LoopbackMcpServer` 和 `McpDispatcher` 实现无状态 Streamable HTTP；固定 IPv4 回环、Host/Origin 检查、请求上限与超时是当前协议边界。变更端口或自行关闭服务时，当前请求仍需能收到结果。
