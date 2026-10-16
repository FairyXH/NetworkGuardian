# 开发、测试与发布

以下命令在仓库根目录的 PowerShell 中执行。交付目标为 Windows x64；最终能力以实际发布 EXE 验收，不能只看 IDE 编译结果。

## 环境

| 组件 | 用途 |
| --- | --- |
| Windows x64 | Windows API、UI、设备集成与发布验收 |
| .NET SDK | 项目目标为 .NET 8；0.14.0 交付机器实测使用 SDK 10.0.302，仓库没有 global.json 锁定 SDK |
| Visual Studio Build Tools C++ 工具链及 Windows SDK | Native AOT 原生链接 |
| PowerShell 7（pwsh） | 构建、验收和打包脚本 |
| Python 3.11+ | 可选 MCP 集成 QA；脚本使用 except*，不能用更旧 Python |
| XeLaTeX / TeX 依赖 | 仅在重建用户说明 PDF 时需要，普通程序构建不需要 |

先记录 `dotnet --info`、`pwsh --version` 和实际测试依赖版本。初次 restore/publish 需要访问 NuGet 或已有缓存；SDK、运行时目标和 AOT 链接器缺失应分别排查，不通过关闭 AOT 或改为框架依赖发布规避。

## 构建与单元测试

```powershell
dotnet restore NetworkGuardian.sln --nologo
dotnet build NetworkGuardian.sln -c Release --nologo
dotnet test tests/NetworkGuardian.Tests/NetworkGuardian.Tests.csproj -c Release --nologo
```

检查明确的 `Passed!` / `Failed!` 摘要；只有退出码 0、没有实际测试输出不能作为成功证据。包缓存缺失或恢复异常时先重新 restore，必要时使用 `--force`，再执行测试，不要反复运行缺依赖的 `--no-restore`。

聚焦验证示例：

```powershell
dotnet test tests/NetworkGuardian.Tests/NetworkGuardian.Tests.csproj -c Release --filter FullyQualifiedName~DistillationTests --nologo
dotnet test tests/NetworkGuardian.Tests/NetworkGuardian.Tests.csproj -c Release --filter FullyQualifiedName~Mcp --nologo
```

按修改风险选择测试；纯文档变更核对事实和链接即可。设备模拟测试、实际 Windows 设备控制与独立网络环境的测试结论应分别记录。

## 发布与体积

```powershell
pwsh -NoProfile -File tools/build-portable.ps1
pwsh -NoProfile -File tools/build-portable.ps1 -OutputDir artifacts/release-candidate -Zip
```

脚本会清空输出目录，只能指向专用产物目录。依次执行 Release 构建、零构建警告检查、测试、win-x64 AOT 发布、单 EXE/无 PDB 检查及 13.5 MiB 门禁。`-SkipTests` 仅用于有明确理由的迭代，不作为完整发布验收。构建日志写入 `artifacts/logs/`；失败时控制台末尾输出也应保留，核对具体失败步骤。

输出为 `NetworkGuardian.exe` 和从 `tools/portable-notes.txt` 复制的 `发布说明.txt`；提权功能位于同一 EXE 的助手入口，不应再拷贝独立 Helper EXE、DLL、运行时或开发符号。`-Zip` 额外在仓库根生成版本化 ZIP，并验证解压后的 EXE 哈希。

主程序版本来自 `Directory.Build.props`；Schema 来自 `GuardianConfig.CurrentVersion`，二者不联动。新依赖或 AOT 元数据增加后重新测量体积，不通过无解释提高 `MaxExeMb` 掩盖增长。

## 最终 EXE 的 MCP 集成验证

建议将 Python 环境放入已忽略的 artifacts，不污染源码。以下版本在文档核对环境中可用，未来升级依赖后重新验收：

```powershell
python -m venv artifacts/mcp-venv
.\artifacts\mcp-venv\Scripts\python.exe -m pip install mcp==1.28.1 httpx==0.28.1
.\artifacts\mcp-venv\Scripts\python.exe -X utf8 tools/qa-mcp.py --exe artifacts/release-candidate/NetworkGuardian.exe --root artifacts/qa-mcp-candidate
```

使用独立空目录作为 `--root`：脚本会写入该目录的配置并启动隔离实例，选取本机空闲端口。测试真实扫描和探测、临时测试凭据、无副作用的 `exit 0` 命令、配置、问答、模型标注/训练/部署/回滚、端口切换和自关闭，并验证 Skill 客户端。它不会为测试而禁用、重启或切换真实在用网卡，但扫描和外网探测仍使用当前电脑的网络能力。

完成后检查 `mcp-report.json` 的 passed、toolCount、version、distillation、rollbackVerified 和 skillClientVerified；报告中的调用次数可能随后台轮询略有变化。机器快照与日志留在 artifacts，不提交到 Git。0.14.0 的 76 次调用是对应交付的一次实测，不是脚本永远固定的次数。

## 其他验收脚本与副作用

| 脚本 | 适用与边界 |
| --- | --- |
| `verify-portable.ps1 -PackageDir <目录>` | 检查包结构、AOT/体积、中性目录启动、窗口响应、内置助手及退出；历史脚本，部分默认开关仍可能操作硬件，先审阅配置；新版确认框也可能影响退出断言 |
| `qa-quality-release.ps1 -Exe <路径> -ReportDirectory <目录>` | 指定 EXE 的隔离启动、质量采样和短时 CPU/内存观察；不代表长期性能基准，已有目录的配置需先检查 |
| `smoke-run.ps1`、`shutdown-test.ps1` | 历史启动/退出检查，会从 bin 或旧 portable 路径寻找 EXE；不保证选中本次发布，也不保证所有系统写操作关闭 |
| `qa-radio-watchdog.ps1`、`qa-wifi-radio*.ps1`、`qa-winrt-radio.ps1` | 真机无线电测试，可能关闭/打开实际无线电和影响网络，仅在指定设备与影响范围明确时执行；其中 WinRT 脚本使用 Windows PowerShell 环境 |
| `ui-screenshot.ps1`、`qa-ui-click.ps1`、`window-dump.ps1` | 历史 UI 截图/定位/点击辅助；检查参数默认路径、DPI、页面和坐标，不把旧坐标当成稳定接口 |

以上脚本不因为存在就代表已经对当前发布重跑通过。选择脚本前阅读参数及配置；当前优先使用明确指定 EXE 和数据根的验收方式。

手动隔离实例需同时设置数据根与实例后缀，并显式关闭不需要的系统控制：`automaticRecovery`、`manageInterfaceMetrics`、`ensureRadioOnAtStartup`、`autoEnableWifiRadio`、`autoEnableWifiDevices`、`radioWatchdogEnabled`，设置 `startup.runAtLogon=false`，不带保持禁用规则和认证命令。仅关 automaticRecovery 或设置独立目录不足以隔离硬件副作用。可参照 `qa-mcp.py` 创建配置，并在完成后恢复当前 Shell 的测试环境变量。

## Skill、种子模型与 PDF

Skill 导入包：

```powershell
pwsh -NoProfile -File tools/package-agent-skill.ps1 -OutputPath artifacts/NetworkGuardian-Skill.zip
python -X utf8 skills/networkguardian/scripts/ng_mcp.py --help
```

ZIP 内应以 `networkguardian/` 为根，包含 SKILL、agents 元数据、训练协议和客户端四个文件，无 Python 缓存、密钥及本地运行数据。服务器工具变化后同步 Skill；变更脚本需验证实际调用。

离线种子复现工具会写入所提供的输出路径，验证时使用 artifacts，避免意外改动内置种子：

```powershell
dotnet run --project tools/NetworkGuardian.Training -c Release -- artifacts/network-brain-reproduced.json tools/NetworkGuardian.Training/teacher-distillation.json
```

该工具不在解决方案中；复用已有 160 条教师标签，无需 API key。只有有意更新内置种子时才替换 `Core/Models/network-brain.seed.json`，并审查来源、训练/留出误差及实际影响。

用户说明：

```powershell
pwsh -NoProfile -File tools/build-user-guide.ps1
```

脚本对保存的 `.tex` 运行三次 XeLaTeX（禁用 shell escape），依赖 ctex/Fandol、geometry、xcolor、booktabs、longtable、tabularx、array、enumitem、fvextra、tcolorbox、fancyhdr、hyperref、xurl。检查溢出、缺字和引用等警告，仅成功后替换仓库 PDF。当前辅助文件在 `artifacts/manual-0.14.0/`；发布版本改变时同步脚本路径。

在支持内置 LaTeX 编辑器的环境中可先打开源码并编译；内置编译失败不能作为 PDF 成功证据。终端脚本提供可复现交付路径。文档修改后检查封面、目录、代码块和工具表，源码与最终 PDF 一同提交。

## 发布交接

1. 确认范围和工作树，提交完成的逻辑单元；同步应用版本、用户入口及专题文档。
2. 完成所需测试和最终 AOT 验收；检查没有实际运行测试却空退出的情况。
3. 核对 EXE 版本、AMD64/AOT、包结构、体积、Skill ZIP、PDF 和 SHA256。
4. 在对应 `docs/release-<版本>.md` 记录提交依据、验证范围、未验证项、产物字节数和哈希；原始机器日志留在 artifacts。
5. 审查暂存文件，不提交用户数据或不相关改动；提交后按指定远端/分支推送，确认远端提交一致。

纯文档更新不重建 EXE、不修改应用版本或历史产物哈希。需要更新安装包内的“发布说明.txt”时，下一次打包会从已更新的文本源复制。
