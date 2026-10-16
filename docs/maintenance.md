# 维护、升级与数据恢复

适用于 0.14.0 / Schema v15。本文以保留用户设置、问答和模型为目标；日常操作见 [使用说明](user-guide/NetworkGuardian-User-Guide.pdf)，开发验收见 [开发文档](development.md)。

## 维护基线

| 信息 | 权威来源 |
| --- | --- |
| 应用版本 | 根目录 `Directory.Build.props`；实际运行版本以 EXE 属性 / MCP 服务器版本为准 |
| 配置版本与默认值 | `Core/Configuration/GuardianConfig.cs`、`QualitySettings.cs` |
| 迁移、范围和空值处理 | `ConfigMigrator.cs`、`ConfigValidator.cs` |
| 数据路径 | `GuardianPaths.cs`、`NetworkHistoryStore.cs`、`DistillationService.cs` |
| MCP 能力和参数 | 当前运行服务器的 `tools/list`，对应 `McpTools.cs` |
| 发布体积和验收 | `tools/build-portable.ps1` 与对应 `docs/release-*.md` |

应用版本升级不一定需要提高配置 Schema。涉及旧字段语义或数据转换时才设计迁移；不要通过随意更改 JSON 中的 version 解决不兼容。旧程序不能作为恢复新 Schema 数据的默认方案。

## 升级和回退程序

1. 记录当前 EXE 版本、数据目录和 MCP 端口；若有训练任务，先记录 jobId，必要时调用 pause。
2. 从托盘退出程序，确认对应进程已退出。仅关闭窗口通常会隐藏到托盘。
3. 备份完整数据目录与旧 EXE，再替换主程序。默认目录不在 EXE 旁边，移动 EXE 不等于迁移数据。
4. 启动新版，核对设备、SSID 绑定、保持禁用、校园网设置和凭据；检查配置迁移与恢复日志。
5. 确认外网出口、质量采样、学习状态和 MCP 监听。恢复 paused 训练时使用已有 jobId。

回退 EXE 前核对 Schema 和数据兼容性；需要回退数据时，优先恢复与旧 EXE 同时保存的整份备份。不要用旧版打开唯一一份新数据进行试错。复制 EXE 只负责程序回退，不自动撤销 Windows 设备禁用、系统 WLAN Profile、接口 Metric 或开机启动项。

## 备份与恢复

默认根目录为 `%LOCALAPPDATA%\NetworkGuardian\`；测试或自定义环境可能通过 `NETWORKGUARDIAN_CONFIG_ROOT` 重定向。先确认实际实例的数据根，避免备份错目录。

下面命令在**程序退出后**执行，将数据复制到用户选定的另一个目录；时间戳目录应保留，不覆盖唯一备份：

```powershell
$guardianDataRoot = Join-Path $env:LOCALAPPDATA 'NetworkGuardian'
$guardianBackupPath = Join-Path $env:USERPROFILE ('NetworkGuardian-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
Copy-Item -LiteralPath $guardianDataRoot -Destination $guardianBackupPath -Recurse
```

恢复时同样先退出，保留故障现场副本，再将选定备份中的整份数据恢复至实际根目录。若目标目录已有数据，应先将其另存，避免混合不同时刻的历史和问答文件。恢复后核对绑定设备标识、学习修订和启动日志。

| 文件 / 目录 | 恢复要点 |
| --- | --- |
| `config.json` / `config.backup.json` | 设置含硬绑定和保持禁用；可解析备份不一定是故障前想要的配置，需核对内容 |
| `config.invalid.json` | 主配置与备份均无法使用时可能保留的损坏现场；不要当有效设置覆盖回去 |
| `wifi-networks.json` 及其备份 | 当前 Windows 用户 DPAPI 保护密码；跨用户/机器迁移通常需要重新录入，不能只证明 JSON 可读就判断密码可用 |
| `network-history.json` / `.bak` | 质量、偏好与权威模型；损坏时会尝试备份，无法恢复则重建 |
| `network-survey.json` / `.bak` | 答题数据及修订；与历史不一致时从保留问答重建，可能改变已蒸馏模型 |
| `network-brain.json` / `.bak` | 权重副本，单独替换不会成为权威运行状态；部署后可能在后续常规保存刷新 |
| `distillation/*.json` / `.bak` | 教师数据、基线、候选、进度与回滚；损坏且备份也不可用时训练服务拒绝加载，应先保留现场排查 |
| `state.json`、`Logs/`、`helper/` | 分别为运行状态、排查证据和助手通信；故障恢复时不要误把临时文件当配置 |

`.bak` 是最近一次原子替换前的版本，不是长期备份，也不能保证跨文件属于同一时刻。不要直接修改运行中程序使用的文件。

## 配置和设备规则

优先通过软件或 MCP 的最小 `config_patch` 修改。该工具递归合并对象，数组整体替换；先读当前值，再改所需字段。手工编辑应先退出程序并备份，重启后检查规范化结果。

`disabledDeviceInstanceIds` 使用 PnP 标识而不是显示名称；SSID 绑定使用接口 GUID，保存在兼容字段 `wifi.campusWifiAdapterAssignments`。驱动重装、设备更换或不同 USB 端口可能改变标识，需要重新核对。取消校园网标记不等于取消 SSID 绑定。

暂停自动恢复仍会执行保持禁用。若要启用设备，应先在软件内取消禁用规则；在 Windows 中单独启用可能很快再次被禁用。保存规则与设备真正执行成功分开检查，后者可能需要 UAC 或重启。

维护认证命令时核对路径、启用状态、管理员需求、退出码和后续外网探测。命令启动成功不能证明校园认证完成。命令参数及自定义凭据 XML 可能包含用户填写的秘密，不应直接放进问题报告。

## 模型与训练任务

保留模型首先要保留历史、问答与修订关系，不能只复制 `network-brain.json`。删除问答会按保留数据从内置种子重建，重置学习也会清除当前部署效果；执行前应明确用户希望删除的数据范围。

任务维护优先使用 MCP：

| 状态 / 情况 | 操作 |
| --- | --- |
| `labeling` | 继续读取未标注样例，保持 offset=0 分批投喂 |
| `running` | 由软件后台训练；查询进度，必要时 pause |
| `paused` | 使用同一 jobId 调 start，不重复创建任务 |
| `failed` | 查看 error、磁盘空间/权限与数据，再决定续训；不要无限重试 |
| `ready` | 查看 evaluation，通过门槛后再 deploy；过期基线需新建任务 |
| `deployed` | 保留回滚资料；只有当前权重仍与该次部署匹配时可 rollback |
| 旧任务占用容量 | delete 清理无用未部署、已回滚或被后续部署替代的任务；运行中及当前部署不可删除 |

最多 64 任务、512 样例/任务、1000 epochs、同时一项训练。新增用户回答可能使部署基线失效；不得手改哈希或放宽门槛覆盖新学习。教师标签和合成误差只证明评分拟合，长期效果仍要观察真实选网与网络证据。

## 周期维护与交接

- 根据实际故障检查滚动日志、质量历史和磁盘空间，不因模型更新次数少就重置数据。
- 长期运行的 Agent 应记录 run 标识、jobId、教师来源、验证结果和部署版本；接近任务容量时清理无用旧任务。
- MCP 不使用时可关闭；更改端口后更新 Agent 客户端和 Skill 元数据中的地址。仅接受本机回环是当前设计，不要将监听改成所有网卡作为临时修复。
- 交接记录实际 EXE/提交、Schema、数据备份位置、已知问题、未完成训练任务和已验证的硬件范围；不要在记录中附密码或模型服务密钥。
- 发布新功能时同步 README、专题文档、Skill 和用户说明；验收数字写入对应发布记录，历史记录保持当时事实。

故障取证与报告字段见 [故障排查](troubleshooting.md)，代码改动流程见 [贡献说明](../CONTRIBUTING.md)。
