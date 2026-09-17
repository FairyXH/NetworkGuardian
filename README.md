# NetworkGuardian

Windows 网络保活工具（C# + WinUI 3）。持续监测物理以太网 / 无线网卡的链路、IP、路由与外网可达性，
在连接**真正失效**时才做最小必要的恢复动作：打开 Wi-Fi 无线电、启用被禁用的物理无线网卡、按网卡单独扫描、
只连接已保存过的配置文件、以及在校园网未认证时运行用户自己配置的认证程序或命令行。

核心原则是 **稳定优先**：已经正常工作的连接不会被主动切换。

---

## 1. 功能概览

| 能力 | 说明 |
| --- | --- |
| 物理网卡识别 | 通过 PnP / Configuration Manager 枚举网络类设备，按总线、枚举器、驱动服务、NDIS media type、`wlansvc` 关联 综合判定，绝不操作虚拟网卡 |
| 粘性连接 | 已连接且可用的无线网卡不参与任何择优、不会被更强的信号抢走 |
| 按网卡独立扫描 | 每块物理无线网卡拥有自己的扫描状态与扫描结果，不做"全局一次扫描" |
| 已保存配置枚举 | `WlanGetProfileList`，只使用 Profile 名；**从不导出、显示或记录 Wi-Fi 密码** |
| 互联网可达性 | 多端点 TCP / HTTP(S) / DNS / 可选 ICMP 组合探测，能识别 Captive Portal 重定向 |
| 校园网认证 | 用户自行配置可执行文件或命令行；带最小间隔、每小时上限、连续次数上限、超时与"已在运行则跳过" |
| 断网自定义命令 | `offlineCommands` 列表：断网（外网判定失败）时按阈值执行用户定义的**命令行**，用于软件式校园网客户端 |
| Wi-Fi 无线电 | `Windows.Devices.Radios` 打开 Windows 的 Wi-Fi 总开关，处理权限被拒 / 策略限制 / 硬件开关 |
| 启用被禁用网卡 | 仅对确认物理、且处于"已禁用"（CM problem code 22/21）的无线网卡调用 `CM_Enable_DevNode`，由提权助手执行 |
| 恢复状态机 | 显式状态机 + 事件驱动 + 低频兜底巡检，含去抖、恢复计数、冷却、指数退避与限流 |
| 配置 / 日志 | JSON 配置（校验、迁移、原子写入、损坏隔离与备份回退）；滚动文本日志 + UI 实时日志（按级别/通道筛选） |
| 界面 | 总览 / 无线网卡 / 以太网 / 设置 / 日志 五个页面，Windows 11 深色 Fluent 风格，支持托盘常驻 |

---

## 2. 系统要求

- Windows 11（已验证 23H2 / build 22631）或 Windows 10 2004 及以上（`TargetPlatformMinVersion 10.0.19041.0`）
- x64
- 无需预装 .NET 运行时或 Windows App Runtime：`WindowsAppSDKSelfContained` + `SelfContained` 均为 `true`，
  .NET 8 与 Windows App SDK 运行时随程序一起发布
- 若要改为框架依赖部署（体积更小），把两个开关设为 `false`，并安装不低于 SDK 包要求的
  `Microsoft.WindowsAppRuntime.1.8`（本机曾遇到已安装 8000.836.2153.0 而 SDK 要求 8000.946.1701.0，
  于是引导器直接弹出 "This application could not be started"）

## 3. 编译要求

- .NET SDK 8.0 或更高（开发机使用 10.0.302，目标框架 `net8.0` / `net8.0-windows10.0.19041.0`）
- Windows SDK（编译期需要，用于 WinRT 投影）：`10.0.26100.0` 或本地任意可用版本
- 主要 NuGet 依赖：
  - `Microsoft.WindowsAppSDK` 1.8.260804001（WinUI 3 + Windows Runtime 投影）
  - `Microsoft.Windows.SDK.BuildTools` 10.0.26100.4654
  - `Microsoft.Extensions.Logging` / `Microsoft.Extensions.Logging.Abstractions` 8.x
  - `Microsoft.Win32.SystemEvents` 8.0.0（休眠/唤醒通知）
  - `xunit` + `Microsoft.NET.Test.Sdk`（仅测试工程）

```powershell
dotnet restore NetworkGuardian.sln
dotnet build NetworkGuardian.sln -c Debug
dotnet test  tests\NetworkGuardian.Tests\NetworkGuardian.Tests.csproj -c Debug
```

> Windows App SDK 的 XAML 编译器在 1.8.260803003 里有一个坑：当 `x:Bind` 路径解析失败时，
> 它会抛出被掩盖的 `WMC9999: Could not find any resources ... ErrorMessages.resources`，完全不提是哪个页面、
> 哪个属性名写错了。`tools/diagnose-xaml-error.ps1` 会从 `obj\...\output.json` 中还原真正出错的页面与堆栈。
> 另外本工程的 `DataTemplate` 内部统一使用经典 `{Binding}`（`tools/convert-template-bindings.ps1` 做转换）。

## 4. 运行

```powershell
# 编译产物（self-contained，直接双击即可运行）
src\NetworkGuardian.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\NetworkGuardian.exe

# 静默启动到托盘
NetworkGuardian.exe --minimized

# 启动时直接打开指定页面（用于自查/截图）
NetworkGuardian.exe --page wireless     # dashboard | wireless | ethernet | settings | logs
```

发布：

```powershell
dotnet publish src\NetworkGuardian.App\NetworkGuardian.App.csproj -c Release -r win-x64
```

## 5. 核心设计原则

### 5.1 粘性连接（不会主动切换）

`GuardianDecisionEngine` 对每块无线网卡独立判断，只有在满足以下**全部**条件时才会重新选网：

1. 该网卡当前**未连接**；或
2. 该网卡已连接，但绑定的连通性探测连续失败达到 `recovery.wifiFailureThreshold`（默认 3 次），
   并且 `wifi.recoverStaleConnections` 为 true；或
3. 处于"连接中/正在获取 DHCP"等过渡态且已超过宽限时间。

因此：

- 网卡 A 连 Campus(45%)、扫描发现 Dorm(92%) → **不动**；
- A 连 Campus、B 连 Dorm，即使扫描显示"交换一下更好" → **不动**；
- 单次探测失败、单个探测端点超时 → **不动**（需要连续失败 + 去抖）。

### 5.2 恢复动作的选择顺序

```
无线电关闭 ──（autoEnableWifiRadio）→ 打开无线电
物理网卡被禁用 ──（autoEnableWifiDevices）→ 提权启用设备 → 等待 WLAN 接口重新出现
以太网链路 Up 但外网不可达 ──→ 运行校园网认证 / offlineCommands → 等待 → 重新探测
某网卡掉线 ──→ 扫描该网卡 → 过滤（仅已保存 Profile）→ 按该网卡自己的信号排序 → 连接最佳候选
多网卡掉线 ──→ 分别扫描 → 统一分配候选（可配置禁止同一 SSID 被多块网卡使用）
以太网恢复 ──→ 保持 Wi-Fi 现状，不打断
```

### 5.3 防风暴

| 风险 | 防护 |
| --- | --- |
| 扫描风暴 | 单网卡最小扫描间隔 `general.minimumScanIntervalSeconds`（默认 25s）+ 扫描超时 `scanTimeoutSeconds`（默认 12s，驱动不回报完成事件也能结束） |
| 连接风暴 | 指数退避 + 抖动 + `recovery.cooldownSeconds` + 单轮最大尝试次数 + 连接失败短名单 `connectFailureBlacklistSeconds` |
| 认证风暴 | 最小间隔、每小时上限、连续次数上限、执行超时、`skipIfAlreadyRunning`、认证后等待与连续验证 |
| 设备启用风暴 | 每设备独立限流 + `maxDeviceEnablePerHour` + `deviceEnableSettleSeconds`；**只对 problem code 22/21 的设备生效** |
| 探测风暴 | 低频巡检（默认 20s）+ 事件触发；探测端点并发受限、单个端点失败不影响整体结论 |
| CPU | 不使用 100ms 轮询；周期以秒为单位，事件驱动为主 |

### 5.4 状态机

`Initializing → Healthy / EthernetNoInternet / Authenticating → WaitingForAuthentication /
WifiRadioOff / EnablingWifiRadio / EnablingWifiDevices / WifiScanning / WifiConnecting /
WaitingForDHCP / VerifyingInternet / Recovering / Cooldown / Paused / Degraded / Error`，
全部转换经 `RecoveryStateMachine`，带原因、时间与转换日志。

## 6. 校园网认证与"断网时运行命令行"

校园网有两种形态，本项目都支持：

1. **网页重定向式**：探测能识别 Captive Portal 重定向（`probeEndpoints[].kind = http` + 重定向检测），
   可用 `campusAuth.runOnCaptivePortal` 触发认证程序。
2. **软件客户端式**：未认证时直接阻断外网，没有重定向页面。此时把客户端的可执行文件填进 `campusAuth`，
   或把任意命令行填进 `offlineCommands`，由"外网判定失败"触发。

```jsonc
{
  "ethernet": {
    "enabled": true,
    "failureThreshold": 3,
    "authenticateWhenLinkUpButOffline": true,
    "linkUpGraceSeconds": 10
  },
  "campusAuth": {
    "enabled": true,
    "name": "校园网认证",
    "kind": "executable",              // executable | shell
    "executablePath": "D:\\Campus\\AuthClient.exe",
    "arguments": "--auto",
    "workingDirectory": "D:\\Campus",
    "runAsAdministrator": true,
    "triggerAfterConsecutiveFailures": 3,
    "waitAfterRunSeconds": 20,
    "minIntervalSeconds": 300,
    "maxRunsPerHour": 6,
    "maxConsecutiveRuns": 3,
    "executionTimeoutSeconds": 60,
    "killOnTimeout": true,
    "skipIfAlreadyRunning": true,
    "waitForExit": false,
    "runOnCaptivePortal": true,
    "requireEthernetLink": true,
    "verificationProbes": 2
  },
  "offlineCommands": [
    {
      "id": "portal-login",
      "name": "校园网命令行登录",
      "enabled": true,
      "kind": "shell",                 // shell = cmd.exe /c <executablePath 即整条命令行>
      "executablePath": "curl -s -X POST http://10.0.0.1/login -d \"user=...&pass=...\"",
      "arguments": "",
      "workingDirectory": "C:\\Windows\\System32",
      "executionTimeoutSeconds": 30,
      "minIntervalSeconds": 600,
      "maxRunsPerHour": 3,
      "maxConsecutiveRuns": 2,
      "runAsAdministrator": false,
      "skipIfAlreadyRunning": true,
      "killOnTimeout": true,
      "waitForExit": true
    }
  ]
}
```

说明：

- 认证程序路径不硬编码，全部来自配置；也可以直接在设置页用"浏览"选择。
- 认证只在"链路 Up（或存在默认路由）但外网判定失败"时触发，不会因为一次丢包就启动。
- 每个认证/命令都有独立的限流状态与"最近一次运行时间"，UI 会显示次数与最近动作。
- 日志只记录：可执行文件路径、退出码、耗时、是否超时；**不记录参数中的任何内容**（避免把口令写进日志）。

## 7. 物理 / 虚拟无线网卡如何区分

判定顺序（`NetworkDeviceClassifier`，规则名会写进日志）：

1. **拒绝**：instance id 以 `ROOT\`、`SWD\`、`SW\`、`BTHENUM\` 等软件枚举器开头；
2. **拒绝**：instance id 命中 `{5d624f94-...}\vwifimp*`（Microsoft Wi-Fi Direct / 承载网络虚拟适配器）；
3. **拒绝**：硬件 ID 命中虚拟厂商/驱动名单（VMware、Hyper-V/VMBus、VirtualBox、QEMU、TAP、TUN、Wintun、WireGuard、OpenVPN、Loopback、Npcap…）；
4. **拒绝**：`vwifibus` / `vwifimp` / `BthPan` 等服务，或 media type 为空/0 且无 WLAN 关联；
5. **接受**：`wlansvc` 关联——该 PnP 设备的 `NetCfgInstanceId` 命中当前 Native Wi-Fi 接口 GUID（`rule=wlansvc-correlation`），
   这是最强的"物理无线网卡"证据；
6. **接受**：枚举器为 `PCI` / `USB` / `SD` / `SDIO` / `VMBUS`(排除虚拟厂商) / `ACPI` 等真实总线，
   且 NDIS `PhysicalMediaType` 为 9（Native 802.11）或 14（802.3）；
7. 其余一律**不接受**（`no-lan-media-type` / `not-on-physical-bus`），并写出原因。

排除名单还可以通过 `interfaceDenyList`（instance id 前缀/通配）追加。

PnP 节点与 Native Wi-Fi 接口的关联方式：读取 `CM_Get_DevNode_Registry_PropertyW(CM_DRP_DRIVER)` 得到驱动键，
再到 `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-...}\<NNNN>` 读取 `NetCfgInstanceId`、
`*PhysicalMediaType`、`*IfType`。这些 `CM_DRP_*` 常量与 `SPDRP_*` 并不相同（`CM_DRP_DRIVER=0x0A` 而
`SPDRP_DRIVER=0x09`），用错会静默读到别的属性——单元测试 `DevicePropertySelectorTests` 专门锁住这些取值。

## 8. 使用的 Windows 原生 API

| 领域 | API |
| --- | --- |
| Native Wi-Fi | `WlanOpenHandle` `WlanCloseHandle` `WlanEnumInterfaces` `WlanRegisterNotification` `WlanScan` `WlanGetAvailableNetworkList` `WlanGetNetworkBssList` `WlanGetProfileList` `WlanGetProfile` `WlanQueryInterface` `WlanConnect` `WlanDisconnect` `WlanFreeMemory`（`wlanapi.dll`，negotiated version 2） |
| 设备管理 | `CM_Get_Device_ID` `CM_Get_DevNode_Status` `CM_Get_DevNode_Registry_PropertyW` `CM_Get_Parent` `CM_Locate_DevNodeW` `CM_Enable_DevNode`（`cfgmgr32.dll`），`SetupDiGetClassDevs` / `SetupDiEnumDeviceInfo`（`setupapi.dll`），`CM_Register_Notification` 设备变更通知 |
| 网络与路由（只读） | `GetAdaptersAddresses` `GetIpForwardTable2`（`iphlpapi.dll`）；仅在显式开启接口度量管理时才使用 `GetIpInterfaceEntry` / `InitializeIpInterfaceEntry` / `SetIpInterfaceEntry`（`netioapi.h`，带 `sizeof(MIB_IPINTERFACE_ROW)=176` 断言） |
| 无线电 | `Windows.Devices.Radios.Radio`（WinRT）：`RequestAccessAsync` / `GetRadiosAsync` / `SetStateAsync` / `StateChanged`，并回退到 `Windows.Devices.Radios` 原生接口做交叉验证 |
| 系统集成 | `Shell_NotifyIcon`（托盘）、`Wlanapi` 通知 + `Microsoft.Win32.SystemEvents.PowerModeChanged`（休眠/唤醒）、HKCU `Run` 键（开机启动，仅当前用户） |
| 提权 | 独立控制台助手 `NetworkGuardian.Helper.exe`（`asInvoker` 主程序 + `requireAdministrator` 助手，通过请求/响应 JSON 文件通信，`runas` 触发 UAC） |

所有 P/Invoke 结构体都对照本机 SDK 头文件（`10.0.26100.0` 的 `wlanapi.h` / `cfgmgr32.h` / `setupapi.h` /
`devpkey.h` / `netioapi.h` / `iphlpapi.h`）逐字段核对，并有结构体尺寸断言（例如
`MIB_IPFORWARD_ROW2` 必须是原生 104 字节，`DOT11_SSID` 36 字节）。原生内存一律 `WlanFreeMemory` /
`Marshal.FreeHGlobal` / `FreeMibTable` 释放，句柄用 `SafeHandle`。

## 9. 管理员权限

- **主程序不需要管理员权限**，正常以当前用户运行。
- 只有两件事需要提权，且都由助手进程完成：
  1. `CM_Enable_DevNode` 启用被禁用的物理无线网卡；
  2. 配置里显式要求 `runAsAdministrator` 的认证程序/离线命令。
- 触发时会出现一次 UAC 提示（`Verb=runas`）；拒绝提权不会导致程序异常，只会在日志与 UI 里记录
  `ERROR_ACCESS_DENIED`，并进入冷却，避免反复弹窗。
- 不修改 UAC 设置、不写系统安全策略、不做隐蔽持久化。

## 10. Windows 位置权限（Wi-Fi API 的限制）

Windows 11 把 **SSID / BSSID / RSSI 等网络标识**放在位置隐私权限之后。没有权限时：

- `WlanGetNetworkBssList` 往往返回 `ERROR_ACCESS_DENIED`（本机实测：扫描本身可用，BSS 细节被拒）；
- 某些配置下 `WlanScan` / `WlanGetAvailableNetworkList` 也会被拒。

程序的行为：

1. 捕获 `ERROR_ACCESS_DENIED` 并**区分**"驱动失败"与"位置权限被拒"；
2. 不崩溃、不重试风暴，继续用能拿到的信息工作（例如仅用 Profile 名 + 信号质量）；
3. 在总览页显示中文提示，并区分三种情况：仅 BSSID/RSSI 被拒（扫描仍可用）、扫描被拒、consent 未授予；
4. **绝不尝试绕过**该权限。

开启方式：`设置 > 隐私和安全性 > 位置` → 打开「定位服务」→ 打开「让桌面应用访问你的位置」→ 重启 NetworkGuardian。
（桌面应用会在 `ConsentStore\location\NonPackaged` 下生成自己的条目。）

## 11. 配置与日志位置

| 内容 | 路径 |
| --- | --- |
| 配置目录 | `%LOCALAPPDATA%\NetworkGuardian\` |
| 配置 | `%LOCALAPPDATA%\NetworkGuardian\config.json` |
| 配置备份 | `config.backup.json`（每次保存前的上一版） |
| 损坏隔离 | `config.invalid.json`（无法解析时保留原文件，并用默认配置启动） |
| 日志 | `%LOCALAPPDATA%\NetworkGuardian\Logs\networkguardian-YYYYMMDD-NNN.log` |
| 提权助手临时文件 | `%LOCALAPPDATA%\NetworkGuardian\helper\request-*.json` / `response-*.json`（用完即删） |
| 测试/隔离用覆盖 | 环境变量 `NETWORKGUARDIAN_CONFIG_ROOT` 可整体替换上述根目录 |

配置保证：

- 每个字段都有默认值与合法区间；加载时 `ConfigValidator` 会把越界值夹回并记录 note；
- `ConfigMigrator` 处理版本升级（老文档补默认值，绝不因缺字段启动失败）；
- 保存使用"临时文件 + `File.Replace`"原子替换并留下 `.bak`；
- 解析失败 → 隔离 + 备份回退 + 默认值启动，程序始终能起来；
- 配置里**不存在**任何 Wi-Fi 密钥字段（`ConfigJsonTests` 有专门断言）。

主要开关（`config.json` 使用 camelCase）：

| 段 | 关键字段 |
| --- | --- |
| `general` | `automaticRecovery` `preferEthernet` `autoEnableWifiRadio` `autoEnableWifiDevices` `ensureRadioOnAtStartup` `healthSweepSeconds` `enumerationRefreshSeconds` `resumeSettleSeconds` `minimumScanIntervalSeconds` `scanTimeoutSeconds` `manualScanTimeoutSeconds` `dhcpWaitSeconds` `manageInterfaceMetrics`（默认关闭） |
| `probe` | `enabled` `intervalSeconds` `timeoutMs` `roundTimeoutMs` `maxConcurrency` `requiredSuccessCount` `treatCaptivePortalAsOffline` `perInterfaceProbing` `allowIcmp` `detectCaptivePortalRedirects` |
| `probeEndpoints[]` | `name` `kind`(tcp/http/https/dns) `target` `timeoutMs` `expectedStatusMin/Max` `bodyMarker` `enabled` |
| `wifi` | `stickyConnection` `recoverStaleConnections` `preferHighBand` `highBandBonus` `signalHysteresis` `allowSameSsidOnMultipleAdapters` `preferRecentProfiles` `recentProfileBonus` `minimumSignalQuality` `onlySavedProfiles` `allowHiddenProfiles` `disconnectGraceSeconds` `ssidDenyList` `ssidAllowList` |
| `recovery` | `internetFailureThreshold` `internetRecoveryThreshold` `wifiFailureThreshold` `cooldownSeconds` `baseBackoffSeconds` `maxBackoffSeconds` `maxConnectAttemptsPerRound` `connectFailureBlacklistSeconds` `deviceEnableSettleSeconds` `maxDeviceEnablePerHour` `operationCircuitBreakerThreshold/Seconds` |
| `ethernet` | `enabled` `failureThreshold` `authenticateWhenLinkUpButOffline` `authenticateOnNoIpConfiguration` `linkUpGraceSeconds` |
| `campusAuth` | 见第 6 节 |
| `offlineCommands[]` | 见第 6 节 |
| `startup` | `runAtLogon` `startMinimized` `minimizeToTray` `closeToTray` |
| `logging` | `minimumLevel`(trace/debug/information/warning/error) `writeToFile` `retentionDays` `maxFileSizeKb` `maxFiles` `verboseNetwork` `uiBufferSize` |
| `interfaceDenyList[]` | instance id 前缀/通配，追加排除 |

## 12. 日志

- 级别：`trace` `debug` `information` `warning` `error`；文件按大小滚动 + 按天数/个数清理。
- UI「日志」页实时显示，可按级别、通道（全部/WiFi/Network/以太网/设备/认证/配置/应用）筛选与搜索，可一键打开日志目录。
- 记录的关键事件：网卡发现与接受/拒绝（含规则名）、无线电状态、扫描开始/完成/失败、连接尝试与结果、断开原因、
  外网丢失/恢复、认证启动与结果、PnP 启用请求与结果、异常、配置加载与校验 note。
- **隐私**：不记录 Wi-Fi 密钥、不导出 Profile 内容、不记录认证命令的参数字符串，只记录路径与退出码。
- 注意：`logging.minimumLevel = debug` 会在每个巡检周期输出完整的设备枚举明细（本机约 21 行/次），排查问题时才建议开启。

## 13. 界面与托盘

- **总览**：外网状态、以太网链路、默认路由（接口名 + 接口度量 + 路由度量）、Wi-Fi 无线电、物理无线网卡数量与各自 SSID/信号、
  恢复状态机状态、最近恢复动作、连续失败计数、校园网认证统计、位置权限提示、待执行动作。
- **无线网卡**：每块物理网卡一张卡片（名称、状态、SSID、BSSID、信号、RSSI、频段、MAC、IP、
  InterfaceGUID、DeviceInstanceId、已保存配置、上次扫描/连接/失败），支持手动扫描/连接/断开；下方为全部无线设备（含被过滤的虚拟网卡及其拒绝原因）。
- **以太网**：全部接口的链路、地址、网关、DNS、度量、默认路由与探测结果，以及物理以太网设备列表（只读）。
- **设置**：所有开关与阈值；可添加/删除任意多条"离线命令"。
- **日志**：见上一节。
- **托盘**：打开 NetworkGuardian / 暂停自动恢复（或恢复自动恢复）/ 运行连通性测试 / 重新扫描 Wi-Fi /
  清除失败记录与冷却 / 打开日志目录 / 退出。关闭窗口的行为由 `startup.closeToTray` 决定。

## 14. 开发与调试

```powershell
# 全量构建 + 测试
dotnet build NetworkGuardian.sln -c Debug
dotnet test  tests\NetworkGuardian.Tests\NetworkGuardian.Tests.csproj -c Debug

# 隔离环境跑一次（不改动真实 %LOCALAPPDATA% 配置，也不改动机器网络）
pwsh -NoProfile -File tools\smoke-run.ps1                 # 只观察（automaticRecovery=false）
pwsh -NoProfile -File tools\smoke-run.ps1 -Recovery       # 带自动恢复

# 页面截图（只截取自己的窗口区域）
pwsh -NoProfile -File tools\ui-screenshot.ps1 -Page wireless -Out docs\ui-wireless.png

# 关闭行为验证（发送 WM_CLOSE，确认循环停止/句柄释放/进程退出）
pwsh -NoProfile -File tools\shutdown-test.ps1

# XAML 编译器被掩盖的错误还原
pwsh -NoProfile -File tools\diagnose-xaml-error.ps1
```

### 14.1 发布打包（自包含，目标机器无需任何开发环境）

```powershell
# 1) 打包：Release 构建 + 测试 + 发布到 release\
pwsh -NoProfile -File tools\build-release.ps1

# 2) 验证：把包复制到中性目录，用干净环境（PATH 无 dotnet、无 DOTNET_ROOT）启动并测试提权助手
pwsh -NoProfile -File tools\verify-release.ps1
pwsh -NoProfile -File tools\verify-release.ps1 -InstanceId 'USB\VID_0BDA&PID_8153\001000001'   # 额外跑一次真实设备查询
```

`release\` 目录结构（约 249 MB，515 个文件）：

```
release\
  NetworkGuardian.exe           主程序（自包含：.NET 8 + Windows App SDK 运行时随之发布）
  NetworkGuardian.dll / *.json  托管程序集与部署清单
  coreclr.dll, hostfxr.dll ...  .NET 运行时
  Microsoft.WindowsAppRuntime.dll, Microsoft.ui.xaml.dll ... Windows App SDK 运行时
  helper\
    NetworkGuardian.Helper.exe  提权助手（单文件自包含压缩包，约 40 MB）
  发布说明.txt                   面向使用者的运行/权限/位置权限说明
```

打包要点与已验证项：

- **不依赖任何已安装运行时**：`NetworkGuardian.runtimeconfig.json` 使用 `includedFrameworks`，
  包内自带 `coreclr.dll` / `Microsoft.WindowsAppRuntime.dll`；目标机不需要 .NET、Windows App Runtime、
  Visual Studio 或任何 SDK 组件。
- **助手同样自包含**：用单文件 + 压缩发布，避免再复制一份完整 .NET 运行时（95 MB → 40 MB）；
  提权执行时首次运行会解压到 `%TEMP%\.net\`。
- **不含 PDB**：`-p:DebugType=none -p:DebugSymbols=false`，发布包内无调试符号。
- **无构建树路径**：部署清单中不包含仓库绝对路径（`tools\verify-release.ps1` 会检查）。
- **实测通过**（`tools\verify-release.ps1`，15/15）：包复制到 `%TEMP%` 中性目录、`PATH=C:\Windows\system32;C:\Windows`、
  `DOTNET_ROOT` 未设置的环境下启动，进程存活、WinUI 窗口创建、日志写入、首次运行生成配置；
  提权助手从包内运行并返回协议响应（未知设备优雅失败 `DeviceNotFound`，真实设备查询 `Succeeded`，`helperElevated=True`）。

> `release/` 已在 `.gitignore` 中，发布包不入库（体积大且可由上述脚本随时重建）。

架构分层（测试与原生代码解耦）：

```
src/NetworkGuardian.Core            纯逻辑：模型、配置（校验/迁移）、策略（粘性、候选、退避、限流、状态机、决策引擎）
src/NetworkGuardian.Windows         原生互操作 + 领域胶水：wlanapi / cfgmgr32 / setupapi / iphlpapi、PnP 清单、
                                    设备分类器、设备启用、Wi-Fi 无线电、连通性探测、位置权限、托盘
src/NetworkGuardian.Infrastructure  配置存储、滚动文件日志、外部命令运行器
src/NetworkGuardian.Helper          提权助手（控制台，requireAdministrator），只做设备启用/禁用
src/NetworkGuardian.App             WinUI 3 界面、ViewModels、GuardianHostService（监测循环 + 动作执行）、托盘、生命周期
tests/NetworkGuardian.Tests         158 个单元测试
```

`GuardianHostService` 是唯一把策略与原生操作连起来的地方：它按周期采集 `GuardianInput`
（接口状态、无线网卡状态、设备、无线电、探测结果），交给纯函数式的 `GuardianDecisionEngine` 得到动作列表，
再逐个执行并发布 `GuardianSnapshot` 给 UI。策略层不依赖任何 Windows API，因此可以在测试里完整覆盖。

## 15. 本机实测结果（Windows 11 22631，AIC8800D80 USB 无线网卡）

- 设备枚举：21 个网络类节点 → 5 个判定为物理（Intel AX201 PCIe Wi-Fi、MediaTek MT7961 USB Wi-Fi、
  AIC8800D80 USB Wi-Fi、Realtek PCIe 网卡、Realtek USB 网卡），16 个被正确过滤
  （VMware ×2、WAN Miniport ×8、蓝牙 PAN ×2、Microsoft Wi-Fi Direct 虚拟适配器 ×4）。
- 生效中的 WLAN 接口 GUID 与 AIC8800D80 的 `NetCfgInstanceId` 成功关联（`rule=wlansvc-correlation`）。
- 扫描：0.9 秒返回 13 个网络；候选过滤只保留已保存的 Profile，未知 SSID 一律忽略。
- 连接：按"信号 + 5 GHz 加成"选中已保存的 `ZhangAndroid`，`WlanConnect` 成功后进入 DHCP 等待。
- 粘性：适配器已连接时，60 秒内**没有任何**扫描或重连动作。
- 故障设备：Intel AX201 处于 problem code 43（驱动故障），程序不执行启用，只在 UI 提示交由设备管理器处理。
- 关闭：`WM_CLOSE` → 监测循环停止 → 注销 WLAN 通知 → 关闭 WLAN 句柄 → flush 日志 → 进程自行退出（无需强杀）。

## 16. 已知限制

- **需要管理员权限的两件事**（启用被禁用网卡、`runAsAdministrator` 的认证程序）必须通过 UAC 助手完成，
  首次触发会弹一次 UAC；拒绝后按冷却重试，不会反复弹窗。
- **位置权限**未开启时拿不到 BSSID/RSSI/信道（部分配置下连扫描也会被拒），只能退化为"Profile 名 + 信号质量"。
- 无线网卡的**真实吞吐**无法从 Native Wi-Fi 得到，探测只判断可达性（TCP/HTTP/DNS/ICMP），不做带宽测量。
- `manageInterfaceMetrics`（自动设置接口度量）默认关闭，且只在显式开启后才写入；程序不会改动用户既有路由。
- 硬件热插拔、休眠/唤醒、Fast Startup 依赖 PnP 通知与 `PowerModeChanged` 回退；这些路径在当前机器上无法完全
  按真实时序复现（未做真实休眠/拔出测试）。
- 扫描完成依赖驱动的 `wlan_notification_acm_scan_complete`，个别老驱动不回报时依赖配置的扫描超时兜底。
- 仅支持 x64；未做 ARM64 与 MSIX 打包（当前为 unpackaged self-contained）。
- 多网卡"避免同一 SSID"仅在多块网卡**同时**需要重连时才做统一分配；已在线的卡不会被重新分配。

## 17. 安全边界（明确不做）

不导出/显示/记录 Wi-Fi PSK；不绕过 UAC；不修改系统安全策略；不做隐蔽持久化（开机启动只写 HKCU `Run`）；
不修改未知路由；不自动加入陌生开放网络；不关闭防火墙；不改动 Windows 位置隐私限制；
不写入任何与保活无关的系统设置。
