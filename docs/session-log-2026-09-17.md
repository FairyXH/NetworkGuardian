# NetworkGuardian 开发会话记录（2026-09-17）

> 本文记录本次会话的完整产出、真机验证证据、发现的缺陷与修复，以及当前状态。
> 项目根：`D:\Files\Develop\Windows\NetworkGuardian`

---

## 1. 会话目标与结论

| 目标 | 结果 |
| --- | --- |
| 交付可用的 Windows 网络保活工具（C# + WinUI 3） | 完成，真机验证通过 |
| 分阶段构建：Core 策略 → 原生互操作 → 基础设施 → 提权助手 → UI → 测试 | 完成 |
| 独立审查（物理网卡限定、不主动切换、扫描/认证风暴、竞态、句柄、退出） | 完成，修掉 6 个真机才暴露的缺陷 |
| 构建自包含发布包，确保无开发环境设备可运行 | 完成，`release\` 248.8 MB + zip 118.5 MB，15/15 校验通过 |
| 评估 ~10 MB 单文件便携 exe 的可行性 | 完成，见 `docs/portable-small-build-plan.md` |

**规模**：84 个 C# 文件 / 18,867 行；7 个 XAML；117 个受版本控制文件；15 个提交。
**质量门**：`dotnet build NetworkGuardian.sln -c Debug/Release` = 0 警告 0 错误；`dotnet test` = 161/161 通过。

---

## 2. 交付物

### 2.1 代码结构

```
NetworkGuardian.sln
src/NetworkGuardian.Core            纯逻辑（不引用任何 Windows API，可完整单元测试）
  Models/          Enums, WifiModels, DeviceModels, InterfaceModels, ProbeModels,
                   ActionModels, GuardianSnapshot, GuardianInput
  Configuration/   GuardianConfig, ConfigJson, ConfigValidator, ConfigMigrator, GuardianPaths
  Policies/        GuardianDecisionEngine, NetworkDeviceClassifier, CandidateSelector,
                   RecoveryStateMachine, FailureTracker, ExponentialBackoff,
                   SlidingWindowRateLimiter, ConnectFailureBlacklist, AdapterAssignmentPlanner,
                   AdapterPolicyState, IClock
  Abstractions/    ServiceContracts        Helper/ HelperProtocol        Logging/ LogModels
src/NetworkGuardian.Windows         原生互操作与领域胶水
  Native/          WlanApiNative, CfgMgr32Native, SetupApiNative, IpHlpApiNative,
                   NetIoApiNative, Win32Error, NativeMemoryHelpers
  Wlan/            NativeWifiManager, WifiMapping
  Devices/         PnpDeviceInventory, PhysicalDeviceManager, DeviceNodeOperations,
                   DeviceNotificationWatcher, HelperClient
  Network/         NetworkInterfaceProvider          Connectivity/ ConnectivityProbe
  Radio/           WifiRadioController, NativeRadioAccess     Location/ LocationPermissionService
  Startup/         StartupRegistration               Tray/ TrayIcon
  Privileges/      ProcessElevation
src/NetworkGuardian.Infrastructure  配置存储、滚动文件日志、外部命令运行器
src/NetworkGuardian.Helper          提权助手（requireAdministrator，一次只做一次 PnP 操作）
src/NetworkGuardian.App             WinUI 3 界面 + ViewModels + GuardianHostService + 生命周期
tests/NetworkGuardian.Tests         161 个测试
```

### 2.2 工具脚本（tools/）

| 脚本 | 用途 |
| --- | --- |
| `build-release.ps1` | Release 构建 + 测试 + 发布到 `release\`（应用与助手均自包含），可选 `-Zip` 生成传输压缩包 |
| `verify-release.ps1` | 发布包校验：中性目录 + 干净环境启动、提权助手真实执行（15 项检查） |
| `smoke-run.ps1` | 隔离配置根下的真机冒烟（`-Recovery` 可开自动恢复） |
| `ui-screenshot.ps1` | 只截取自身窗口的界面截图（DPI 感知 + HWND_TOPMOST + 支持 `-ExePath`/`-Page`） |
| `shutdown-test.ps1` | 发 `WM_CLOSE` 验证关闭链路与进程自退 |
| `window-dump.ps1` | 列出某进程的全部顶层窗口（句柄/可见性/类名/标题） |
| `diagnose-xaml-error.ps1` | 还原被 `WMC9999` 掩盖的 XAML 编译错误（读 `obj\...\output.json`） |
| `convert-template-bindings.ps1` | 把 `DataTemplate` 内的 `x:Bind` 批量改为 `{Binding}`（绕过 WinUI 1.8 编译器缺陷） |

### 2.3 文档

| 文件 | 内容 |
| --- | --- |
| `README.md` | 功能、系统要求、编译运行、粘性规则、恢复顺序、校园网认证与离线命令配置、物理网卡判定、原生 API 清单、权限、配置/日志路径、限制、安全边界、14.1 发布打包 |
| `docs/session-log-2026-09-17.md` | 本文（会话记录） |
| `docs/portable-small-build-plan.md` | 小体积单文件便携版方案（实测数据 + 实施计划） |
| `docs/ui-*.png` | 总览 / 无线网卡 / 以太网 / 设置 / 日志 / 发布版总览 的实际窗口截图 |

---

## 3. 真机验证证据

环境：Windows 11 22631 x64；.NET SDK 10.0.302；Windows App SDK 1.8.260804001（WinUI 1.8.260803003）；
Windows SDK 头文件 10.0.26100.0；硬件含 AIC8800D80 USB WiFi、Intel AX201（故障）、MediaTek MT7961 USB WiFi、
Realtek PCIe/USB 有线网卡、VMware VMnet、蓝牙 PAN。

| 验证项 | 证据 |
| --- | --- |
| 设备枚举与分类 | 21 个网络类节点 → 5 个物理、16 个虚拟全部正确过滤（VMware×2、WAN Miniport×8、蓝牙 PAN×2、Wi-Fi Direct 虚拟适配器×4） |
| PnP ↔ WLAN 关联 | 活动 WLAN 接口 `c7bd53a9-fb3a-47e4-a3b2-54b7836004ea` 与 AIC8800D80 的 `NetCfgInstanceId` 命中（`rule=wlansvc-correlation`） |
| 扫描 | 0.9 秒返回 13 个网络，日志含 SSID / RSSI / 频段 / BSSID / 是否有已保存配置 |
| 候选过滤 | 只对已保存 Profile（ZhangAndroid / HXXY-WiFi / ZXH / ZhangMobile）建候选，未知 SSID 全部忽略 |
| 连接 | 选中并 `WlanConnect` 成功连上 ZhangAndroid（5 GHz，信号 100），随后进入 DHCP 等待 |
| 粘性 | 适配器已连接状态下运行 60 秒：**零扫描、零重连** |
| 故障设备 | Intel AX201（problem code 43，驱动故障）不被启用，仅在 UI 提示 |
| 关闭链路 | `WM_CLOSE` → `Guardian monitor loop stopped` → `Unregistered WLAN notifications` → `WLAN client handle closed` → `Guardian host disposed` → 进程自行退出 |
| 位置权限 | consent = Deny 时：扫描可用、BSS 细节被拒；总览页显示中文指引且区分三种受限情形 |
| 发布包 | `tools\verify-release.ps1` 15/15 通过：中性目录 + `PATH=C:\Windows\system32;C:\Windows` + 无 `DOTNET_ROOT` 下启动存活、WinUI 窗口创建、日志写入、首次运行生成配置；助手返回 `DeviceNotFound`（未知设备）与 `Succeeded`（真实设备 `USB\VID_0BDA&PID_8153\001000001`），`helperElevated=True` |
| 压缩包往返 | 解压 515 文件 / 248.8 MB，`NetworkGuardian.exe` 与 helper 哈希与 `release\` 完全一致，且对解压副本重跑 15/15 |

---

## 4. 真机暴露的缺陷与修复（全部已加回归测试或工具化）

| # | 现象 | 根因 | 修复提交 |
| --- | --- | --- | --- |
| 1 | PnP ↔ WLAN 无法关联；Realtek 有线网卡被判为虚拟 | 手写 `CM_DRP_*` 常量错用了 `SPDRP_*` 的取值（`CM_DRP_DRIVER=0x0A` 而 `SPDRP_DRIVER=0x09`），静默读到别的属性 | `574508f`（含 `DevicePropertySelectorTests` 锁定取值） |
| 2 | 默认路由显示乱码网关与荒谬 LUID；"21 条默认路由" | 托管 `MIB_IPFORWARD_ROW2` 布局 112 字节，原生为 104（托管 `SOCKADDR_INET` 8 字节对齐、原生 4），整表按错误步长读取 | `145c3e3`（`Pack = 4` + `Marshal.SizeOf` 断言） |
| 3 | 已保存 Wi-Fi 配置名显示为单字母 `Z H Z Z` | 通过 `Marshal.PtrToStructure` 后的托管副本读 `strProfileName` | `145c3e3`（改从原生缓冲 `Marshal.PtrToStringUni` 读取） |
| 4 | 日志告警"跳过启用"后仍然对驱动故障设备执行 `CM_Enable_DevNode` | 早退分支只记日志、缺 `return`；引擎又用 `!IsEnabled` 作为启用条件（把 problem code 43 也算进去） | `8af42ce`（仅对 problem code 22/21 启用 + 提前返回） |
| 5 | 日志级别为 Information 时首轮运行日志文件可能为空 | 日志目录由配置存储创建，写失败被泛化 catch 静默吞掉 | `c51289e`（写入器自建目录 + 失败上报到 UI 日志通道） |
| 6 | 蓝牙 PAN / VMware 接口被列为"物理以太网"，可能对不可能认证的链路触发校园网认证 | 界面按 `IfType=802.3` 判断，未使用 PnP 物理判定 | `e5d480b`（`InterfaceRuntimeState.IsPhysicalDevice` + 引擎与界面同时过滤，新增 `EthernetEligibilityTests`） |
| 7 | 界面事件处理器异常会终止进程；`Guardian host disposed` 写在日志工厂 Dispose 之后 | `async void` 处理器无护栏 | `2544722` |

工具侧修复：截图脚本最初用 `PrintWindow`（WinUI 走 DirectComposition，返回黑屏）→ 改 `HWND_TOPMOST` + 屏幕拷贝；
宿主 PowerShell 非 DPI 感知导致 `GetWindowRect` 虚拟化裁剪 → 先 `SetProcessDpiAwarenessContext(-4)`；
最小化到托盘会隐藏窗口导致截图脚本找不到窗口 → 改为 `EnumWindows` 按窗口类名定位并显式 `ShowWindow`。

---

## 5. 关键设计决策（含理由）

1. **分层**：策略层（`Core`）零 Windows 依赖，因此 161 个测试里策略/配置部分不需要任何真实硬件；`GuardianHostService` 是唯一把策略与原生操作连起来的地方。
2. **粘性优先**：已连接且可用的网卡不进入候选流程；只有"未连接 / 连续失败达阈值 / 过渡态超时"才重选。真机 60 秒零动作验证。
3. **只操作物理设备**：白名单式判定（`wlansvc` 关联最强，其次真实总线 + media type），虚拟设备一律不碰。
4. **提权外置**：`NetworkGuardian.Helper.exe`（requireAdministrator）通过请求/响应 JSON 文件通信，主程序始终非管理员。
5. **自包含部署**：本机已装 Windows App Runtime 8000.836.2153.0 低于 SDK 要求的 8000.946.1701.0，框架依赖部署会直接弹 "This application could not be started"，故应用与助手都自包含。
6. **配置安全**：配置模型中不存在任何 Wi-Fi 密钥字段（有测试断言）；认证命令只记录路径与退出码，不记录参数。
7. **`NeutralLanguage` 不设置**：设为 `zh-CN` 会让 WinUI 1.8 的 XAML 编译器在部分机器上以 `WMC9999` 失败（卫星资源查找）。

---

## 6. 未验证 / 已知限制

- **未做真实验证**：休眠/唤醒（含 Fast Startup）、USB Wi-Fi 物理拔插、被禁用网卡的提权启用（需要一块处于"已禁用"状态的物理无线网卡 + UAC 交互）、校园网认证接真客户端、多块物理无线网卡同时在线的候选分配。
- 位置权限未授予时退化：拿不到 BSSID / RSSI / 信道，仅能按 Profile 名 + 信号质量工作。
- 只判断可达性（TCP/HTTP/DNS/ICMP），不测带宽。
- 扫描完成依赖驱动回报 `scan_complete`，个别老驱动靠配置的 `scanTimeoutSeconds` 兜底。
- 仅 x64，未做 MSIX 打包与 ARM64。
- 发布包 exe 哈希每次构建会变化（编译期非确定性），未声明可复现构建。

---

## 7. 提交历史（本次会话）

```
0c106a3 docs: refresh the release-build screenshot from the shipped executable
dd6ca73 feat(release): optional transfer archive with a versioned top-level folder
8ca2e7c feat(release): self-contained release package with a verifying build script
e5d480b fix(recovery): exclude virtual interfaces from Ethernet eligibility
c51289e fix(logging): make the log file survive the first run and surface write failures
c6896a7 docs: README, UI captures and a warning-free build
2544722 fix(app): keep UI event handlers from killing the process, tidy shutdown order
8af42ce fix(recovery): never enable a device that failed to start
145c3e3 fix(interop): correct IP forward row layout and WLAN wide-string reads
574508f fix(devices): correct CM_DRP_* selectors and stop per-cycle warning spam
2195362 test: policy, configuration, interop and infrastructure coverage
f6de8b7 feat(app): WinUI 3 shell, monitor loop and pages
8db1bea feat: configuration store, file logger, command runner and privileged helper
4e3676c feat(windows): native interop, PnP device management, WLAN, radio and probes
b719f5b feat: scaffold solution and core domain layer
```

---

## 8. 复现命令

```powershell
# 构建与测试
dotnet build NetworkGuardian.sln -c Debug
dotnet test  tests\NetworkGuardian.Tests\NetworkGuardian.Tests.csproj -c Debug

# 真机冒烟（隔离配置根，不动真实 %LOCALAPPDATA%）
pwsh -NoProfile -File tools\smoke-run.ps1                 # 只观察
pwsh -NoProfile -File tools\smoke-run.ps1 -Recovery       # 带自动恢复

# 界面截图 / 关闭验证
pwsh -NoProfile -File tools\ui-screenshot.ps1 -Page wireless -Out docs\ui-wireless.png
pwsh -NoProfile -File tools\shutdown-test.ps1

# 发布包
pwsh -NoProfile -File tools\build-release.ps1 -Zip
pwsh -NoProfile -File tools\verify-release.ps1
```

---

## 9. 后续建议（按优先级）

1. **小体积便携版**：见 `docs/portable-small-build-plan.md`（Native AOT + 自绘 Win32 UI，预计 6–8 MB 单文件）。
2. 校园网认证接一个真实客户端做端到端验证（目前只有逻辑与限流的测试覆盖）。
3. 休眠/唤醒与 USB 插拔做一次真实场景验证，必要时把 `resumeSettleSeconds` 调优。
4. 如需版本固化，加入 `<Deterministic>true</Deterministic>` 与 `ContinuousIntegrationBuild`。
5. 发布包瘦身（若坚持 WinUI）：目前无受支持手段去掉 WinAppSDK 的 ML 载荷（onnxruntime + DirectML 38.6 MB）。
