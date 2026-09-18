# NetworkGuardian

Windows 网络保活工具（C# / .NET 8 / **Native AOT 单文件** + 自绘 Win32 UI）。
持续监测物理以太网 / 无线网卡的链路、IP、路由与外网可达性，在连接**真正失效**时才做最小必要的恢复动作：
打开 Wi-Fi 无线电、启用被禁用的物理无线网卡、按网卡单独扫描、只连接已保存过的配置文件、
以及在校园网未认证时运行用户自己配置的认证程序或命令行。

核心原则是 **稳定优先**：已经正常工作的连接不会被主动切换。

> 文档导航：本文件是主文档；本次「小体积单文件」改造的完整记录见
> [`docs/session-log-2026-09-18.md`](docs/session-log-2026-09-18.md)，
> 改造前的实测体积矩阵与方案分析见 [`docs/portable-small-build-plan.md`](docs/portable-small-build-plan.md)，
> 更早的 WinUI 3 版本会话记录见 [`docs/session-log-2026-09-17.md`](docs/session-log-2026-09-17.md)（仅历史参考），
> 全部文档索引见 [`docs/README.md`](docs/README.md)。

---

## 1. 交付形态：单文件便携版

| 制品 | 体积 | 说明 |
| --- | --- | --- |
| `release\NetworkGuardian.exe` | **8.24 MB** | **单个原生 exe**：主程序 + 内置提权助手（`NetworkGuardian.exe --helper`），内置 .NET 运行时（Native AOT），目标机无需安装 .NET / Windows App Runtime / VS |
| `release\发布说明.txt` | — | 面向使用者的运行 / 权限 / 位置权限说明 |
| `NetworkGuardian-0.9.0-win-x64.zip` | 3.79 MB | 可选传输压缩包（内含版本号顶层目录） |

整个包只有这两个文件、一个 exe：早期版本把提权助手做成第二个 exe（包合计 10.61 MB），
现在助手编译进主程序，只有需要提权时才以 `--helper` 二次启动同一个 exe。

对比：改造前的 WinUI 3 自包含发布包是 **248.8 MB / 515 个文件**。
差异全部来自三个不可裁剪项——WinUI 运行时（约 28 MB）、WinRT 投影（23.7 MB）、Windows App SDK 的 ML 载荷（38.6 MB），
以及约 60 MB 的 .NET 运行时；只有「换 UI 框架 + Native AOT」能同时去掉它们。
完整实测数据见 [`docs/session-log-2026-09-18.md`](docs/session-log-2026-09-18.md) 第 3 节。

## 2. 功能概览

| 能力 | 说明 |
| --- | --- |
| 物理网卡识别 | PnP / Configuration Manager 枚举网络类设备，按总线、枚举器、驱动服务、NDIS media type、`wlansvc` 关联综合判定，绝不操作虚拟网卡 |
| 粘性连接 | 已连接且可用的无线网卡不参与任何择优、不会被更强的信号抢走 |
| 按网卡独立扫描 | 每块物理无线网卡拥有自己的扫描状态与结果，不做「全局一次扫描」 |
| 已保存配置枚举 | `WlanGetProfileList`，只使用 Profile 名；连接企业级网络时按自维护无线网络库（§7.1）写入配置与账号 |
| 自维护无线网络库 | 独立于系统的 802.1X 账号库（`wifi-networks.json`）：账号、密码（DPAPI 密文）、EAP 方法、服务器名校验，连接时自动写入系统并重写变更；密码不落明文、不进日志 |
| 互联网可达性 | 多端点 TCP / HTTP(S) / DNS / 可选 ICMP 组合探测，能识别 Captive Portal 重定向 |
| 校园网认证 | 用户自行配置可执行文件或命令行；带最小间隔、每小时上限、连续次数上限、超时与「已在运行则跳过」 |
| 断网自定义命令 | `offlineCommands` 列表：断网时按阈值执行用户定义的命令行 |
| Wi-Fi 无线电（总开关） | 关闭时读写 Windows 的 Wi-Fi 总开关；无法控制时如实报权限被拒 / 策略限制 / 硬件开关 |
| 每块网卡的 Wi-Fi 分开关 | Windows 设置里每块无线网卡各有一个 Wi-Fi 开关（本例中的「WLAN」「WLAN 3」）。程序通过 **Windows 无线电管理器**（`IMediaRadioManager` / `IRadioInstance`）逐块读取，发现某块被关掉就把它打开（硬件开关关闭的会如实说明无法用软件打开）。`wlanapi` 的 `wlan_intf_opcode_radio_state` 在本机两块网卡上都返回 87（参数错误），只能读不能写，因此不再用于写入 |
| 软开关看门狗 | 独立循环每 3 秒（可配）检查全部无线网卡的软开关并立即打开，不受决策引擎限流影响，见 §7.2 |
| 802.1X 失败重试 | 企业级网络用库中账号连接失败时按「网卡 + SSID」计数，达到上限（默认 5 次）后本次运行临时放弃，重启后继续尝试；改库密码立即清零计数 |
| 启用被禁用网卡 | 仅对确认物理、且处于「已禁用」（CM problem code 22/21）的无线网卡调用 `CM_Enable_DevNode`，由内置助手以 `--helper` 提权执行（普通权限下这一步会弹一次 UAC 确认） |
| 故障网卡重启 | 驱动启动失败（problem code 10/43…，Windows 不显示为「已禁用」）的物理无线网卡会尝试一次「禁用+启用」重启：每设备限流（≤3 次/小时、间隔 ≥120 秒、连续 2 次失败即停）；设备仍未启动时如实报失败并提示重装/回滚驱动 |
| 一个 SSID 一张网卡 | 默认不允许两张网卡连接同一个 SSID；检测到重复连接时断开信号较弱的那张，并阻止它立刻重连回去（设置页可改回允许） |
| 恢复状态机 | 显式状态机 + 事件驱动 + 低频兜底巡检，含去抖、恢复计数、冷却、指数退避与限流 |
| 配置 / 日志 | JSON 配置（校验、迁移、原子写入、损坏隔离与备份回退）；滚动文本日志 + 界面实时日志（按级别/通道筛选） |
| 界面 | 总览 / 无线网卡 / 网络凭据库 / 以太网 / 设置 / 日志 六个页面，深色卡片式 UI，托盘常驻 |
| 休眠唤醒 | 自绘窗口直接处理 `WM_POWERBROADCAST`，唤醒后先稳定网络再判断 |

## 3. 系统要求

- Windows 10 2004（build 19041）或更高，x64（已在 Windows 11 22631 上验证）
- **目标机无需任何运行时**：不装 .NET、不装 Windows App Runtime、不装 Visual Studio 也能双击运行
- 主程序不需要管理员权限

编译机要求：.NET SDK 8 或更高（开发机 10.0.302）、Windows SDK（`10.0.26100.0` 或本地可用版本）、
**MSVC 链接器**（Native AOT 需要 `cl.exe`/`link.exe`，来自 VS Build Tools）。

## 4. 编译、测试与打包

```powershell
# 全量构建 + 单元测试（255 个用例，策略/配置/互操作布局全覆盖；另有真机用例，见 §7.1 文档）
dotnet build NetworkGuardian.sln -c Release
dotnet test  tests\NetworkGuardian.Tests\NetworkGuardian.Tests.csproj -c Release

# 一键：Release 构建（0 警告）→ 测试 → 主程序 AOT（助手内置）→ 体积门限 → 可选压缩包
pwsh -NoProfile -File tools\build-portable.ps1            # 产出 release\（已 gitignore）
pwsh -NoProfile -File tools\build-portable.ps1 -Zip       # 额外产出 NetworkGuardian-0.9.0-win-x64.zip
pwsh -NoProfile -File tools\build-portable.ps1 -SkipTests # 只重新打包

# 发布包验证：中性目录 + 干净环境（PATH 无 dotnet、无 DOTNET_ROOT）+ 内置助手真实执行 + 关闭链路
pwsh -NoProfile -File tools\verify-portable.ps1
pwsh -NoProfile -File tools\verify-portable.ps1 -InstanceId 'USB\VID_0BDA&PID_8153\001000001'
```

`build-portable.ps1` 的门限：`NetworkGuardian.exe` 必须 ≤ **8.5 MB**（默认 `-MaxExeMb 8.5`）且构建 0 警告、测试全绿，
否则直接失败——体积回退不会被静默接受。

## 5. 运行

```powershell
# 直接双击，或：
release\NetworkGuardian.exe                  # 默认最小化到托盘（可在设置页关闭）
release\NetworkGuardian.exe --page wireless  # dashboard | wireless | credentials | ethernet | settings | logs
release\NetworkGuardian.exe --visible        # 强制显示窗口（即使配置为启动时最小化）
release\NetworkGuardian.exe --minimized      # 明确以托盘方式启动
```

自查 / 自动化用的环境变量：

| 变量 | 作用 |
| --- | --- |
| `NETWORKGUARDIAN_CONFIG_ROOT` | 整体替换配置根目录（测试用，绝不碰 `%LOCALAPPDATA%`） |
| `NETWORKGUARDIAN_INSTANCE_SUFFIX` | 单实例互斥体后缀，允许验证脚本与用户正在运行的实例并存 |

## 6. 核心设计原则

### 6.1 粘性连接（不会主动切换）

`GuardianDecisionEngine` 对每块无线网卡独立判断，只有满足以下**全部**条件时才会重新选网：

1. 该网卡当前**未连接**；或
2. 该网卡已连接，但绑定的连通性探测连续失败达到 `recovery.wifiFailureThreshold`（默认 3 次），
   且 `wifi.recoverStaleConnections` 为 true；或
3. 处于「连接中/正在获取 DHCP」等过渡态且已超过宽限时间。

### 6.2 恢复动作的选择顺序

```
无线电关闭 ──（autoEnableWifiRadio）→ 打开无线电
物理网卡被禁用 ──（autoEnableWifiDevices）→ 提权启用设备 → 等待 WLAN 接口重新出现
以太网链路 Up 但外网不可达 ──→ 运行校园网认证 / offlineCommands → 等待 → 重新探测
某网卡掉线 ──→ 扫描该网卡 → 过滤（仅已保存 Profile）→ 按该网卡自己的信号排序 → 连接最佳候选
多网卡掉线 ──→ 分别扫描 → 统一分配候选（可配置禁止同一 SSID 被多块网卡使用）
以太网恢复 ──→ 保持 Wi-Fi 现状，不打断
```

### 6.3 防风暴

| 风险 | 防护 |
| --- | --- |
| 扫描风暴 | 单网卡最小扫描间隔（默认 25s）+ 扫描超时（默认 12s，驱动不回报完成事件也能结束） |
| 连接风暴 | 指数退避 + 抖动 + 冷却 + 单轮最大尝试次数 + 连接失败短名单 |
| 认证风暴 | 最小间隔、每小时上限、连续次数上限、执行超时、`skipIfAlreadyRunning`、认证后等待与连续验证 |
| 设备启用风暴 | 每设备独立限流 + `maxDeviceEnablePerHour` + 启用后等待；**只对 problem code 22/21 的设备生效** |
| 探测风暴 | 低频巡检（默认 20s）+ 事件触发；探测端点并发受限、单个端点失败不影响整体结论 |
| CPU | 不使用 100ms 轮询；周期以秒为单位，事件驱动为主 |

### 6.4 状态机

`Initializing → Healthy / EthernetNoInternet / Authenticating → WaitingForAuthentication /
WifiRadioOff / EnablingWifiRadio / EnablingWifiDevices / WifiScanning / WifiConnecting /
WaitingForDHCP / VerifyingInternet / Recovering / Cooldown / Paused / Degraded / Error`，
全部转换经 `RecoveryStateMachine`，带原因、时间与转换日志。

## 7. 校园网认证与「断网时运行命令行」

校园网有两种形态，本项目都支持：

1. **网页重定向式**：探测能识别 Captive Portal 重定向（`probeEndpoints[].kind = http` + 重定向检测），
   可用 `campusAuth.runOnCaptivePortal` 触发认证程序。
2. **软件客户端式**：未认证时直接阻断外网，没有重定向页面。此时把客户端的可执行文件填进 `campusAuth`，
   或把任意命令行填进 `offlineCommands`，由「外网判定失败」触发。

```jsonc
{
  "campusAuth": {
    "enabled": true,
    "name": "校园网认证",
    "kind": "executable",              // executable | shell
    "executablePath": "D:\\Campus\\AuthClient.exe",
    "arguments": "--auto",
    "runAsAdministrator": true,
    "triggerAfterConsecutiveFailures": 3,
    "minIntervalSeconds": 300,
    "maxRunsPerHour": 6,
    "maxConsecutiveRuns": 3,
    "executionTimeoutSeconds": 60,
    "skipIfAlreadyRunning": true,
    "runOnCaptivePortal": true,
    "requireEthernetLink": true
  },
  "offlineCommands": [
    {
      "id": "portal-login",
      "name": "校园网命令行登录",
      "kind": "shell",                 // shell = cmd.exe /c <executablePath 即整条命令行>
      "executablePath": "curl -s -X POST http://10.0.0.1/login -d \"user=...&pass=...\"",
      "executionTimeoutSeconds": 30,
      "minIntervalSeconds": 600,
      "maxRunsPerHour": 3,
      "maxConsecutiveRuns": 2
    }
  ]
}
```

说明：认证程序路径不硬编码，全部来自配置（设置页可「浏览…」选择）；认证只在「链路 Up（或存在默认路由）
但外网判定失败」时触发，不会因为一次丢包就启动；日志只记录可执行文件路径、退出码、耗时、是否超时，
**不记录参数内容**（避免把口令写进日志）。

第三种形态是 **企业级 Wi-Fi（802.1X/EAP）**：账号密码不是网页登录，而是无线层的 EAP 认证。
程序为此维护自己的账号库，见 §7.1。

### 7.1 自维护无线网络库（802.1X / EAP 账号）

「网络凭据库」页面维护一张**独立于系统**的表：每个企业级 SSID 一条，含认证方式、EAP 方法、账号、
密码、域、匿名身份、服务器名校验、受信任根证书、客户端证书、可选的完整配置 XML。连接时由程序按库中的
参数在网卡上生成/更新系统配置并写入账号，**Windows 不再需要自己保存或提示密码**。

| 事项 | 行为 |
| --- | --- |
| 存储位置 | `%LOCALAPPDATA%\NetworkGuardian\wifi-networks.json`（可用 `NETWORKGUARDIAN_CONFIG_ROOT` 整体重定向；原子写入 + `.bak` + 损坏隔离为 `.invalid.json`） |
| 密码保护 | DPAPI（`CryptProtectData`，当前用户 + 固定应用熵），只落盘密文；`password` 字段**从不序列化**，界面默认显示「已保存 N 位」，日志与界面都不写明文 |
| 生成的文档 | ① WLAN 连接配置（OneX/EAPConfig，逐用户配置，`WlanSetProfile`）② EAP 用户凭据（`EapHostUserCredentials`，`WlanSetProfileEapXmlUserData`）——**密码不能写在配置 XML 里**，WLAN 服务会以原因码 524289 拒绝 |
| 何时重写 | 网卡上没有该配置 / 现有配置不是 802.1X / 库中条目在上次写入后被修改过。库中任何字段（含密码）变化都会清除「已写入」标记，因此改密码后立刻重写；不想自动写入可关 `wifi.applyEapProfileOnConnect` |
| 支持的生成方式 | PEAP + MSCHAPv2（账号密码）、EAP-TLS（客户端证书，可选按指纹固定）；WPA2-Enterprise(AES)、WPA-Enterprise(TKIP)。其他（EAP-TTLS、厂商方案、证书选择、MAC 随机化）填「自定义配置 XML」，程序原样写入 |
| 失败重试 | 每个「网卡 + SSID」独立计数，失败达到 `wifi.eapConnectMaxAttempts`（默认 5）后**本次运行放弃该网络**（仅内存，重启后继续尝试）。在库中修改该网络（例如改正密码）会立刻清零计数 |
| 库中没有账号 | 不连接，并在日志/界面写明「需要 802.1X 认证，但自维护无线网络库中没有该网络的账号」 |
| 库整体关闭 | `wifi.useCredentialLibraryForEap = false`：企业级网络仍按普通已保存配置连接，账号交给 Windows 自己处理 |

```jsonc
{
  "wifi": {
    "useCredentialLibraryForEap": true,   // 企业级网络用库中的账号
    "applyEapProfileOnConnect": true,     // 库中参数变化时自动重写系统配置
    "eapConnectMaxAttempts": 5            // 失败多少次后本次运行放弃该网络
  }
}
```

设计取舍与真机证据见 [`docs/eap-credential-library.md`](docs/eap-credential-library.md)。

### 7.2 每块网卡的软开关（无线电看门狗）

Windows 的 Wi-Fi 开关是**每块网卡一个**：某块在「设置 → 网络和 Internet → Wi-Fi」里被关掉后，
`wlanapi` 的 `wlan_intf_opcode_radio_state` 只能读到它，写操作在本机两块 USB 网卡上都返回 87（参数错误），
因此写入走 **Windows 无线电管理器**（`IMediaRadioManager` / `IRadioInstance.SetRadioState`）。

主监测循环的心跳是低频巡检，不足以及时发现「刚被关掉的开关」，所以另有一条**独立循环**：
每 `general.radioWatchdogSeconds`（默认 3 秒）读取全部无线网卡的软开关，发现被软件关闭就立即打开；
硬件开关关闭的只如实报告、不反复重试。它不受决策引擎里无线电动作限流（`12 次/小时`、`30 秒`最小间隔）
的影响——这正是看门狗存在的理由。用户「暂停自动恢复」期间看门狗同样不干预网卡。

```jsonc
{
  "general": {
    "radioWatchdogEnabled": true,   // 总开关
    "radioWatchdogSeconds": 3       // 检查间隔（立即打开，不等待下一轮）
  }
}
```

真机验证：`tools/qa-radio-watchdog.ps1`（先启动程序，再用 `tools/qa-wifi-radio-winrt.ps1` 像设置页那样
关掉一块网卡的软开关，测量恢复时间并检查日志中的看门狗行）。

## 8. 物理 / 虚拟无线网卡如何区分

判定顺序（`NetworkDeviceClassifier`，规则名会写进日志）：

1. **拒绝**：instance id 以 `ROOT\`、`SWD\`、`SW\`、`BTHENUM\` 等软件枚举器开头；
2. **拒绝**：instance id 命中 `{5d624f94-...}\vwifimp*`（Microsoft Wi-Fi Direct 虚拟适配器）；
3. **拒绝**：硬件 ID 命中虚拟厂商/驱动 token（VMware、Hyper-V/VMBus、VirtualBox、QEMU、TAP、TUN、Wintun、WireGuard、OpenVPN、Loopback、Npcap…）；
4. **拒绝**：`vwifibus` / `vwifimp` / `BthPan` 等服务，或 media type 为空/0 且无 WLAN 关联；
5. **接受**：`wlansvc` 关联——PnP 设备的 `NetCfgInstanceId` 命中当前 Native Wi-Fi 接口 GUID（`rule=wlansvc-correlation`）；
6. **接受**：真实总线（`PCI`/`USB`/`SD`/`SDIO`/`ACPI` 等）且 NDIS `PhysicalMediaType` 为 9（Native 802.11）或 14（802.3）；
7. 其余一律**不接受**，并写出原因。

PnP 节点与 Native Wi-Fi 接口的关联：读取 `CM_Get_DevNode_Registry_PropertyW(CM_DRP_DRIVER)` 得到驱动键，
再到 `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-...}\<NNNN>` 读取 `NetCfgInstanceId`、
`*PhysicalMediaType`、`*IfType`。`CM_DRP_*` 与 `SPDRP_*` **并不相同**（`CM_DRP_DRIVER=0x0A` 而 `SPDRP_DRIVER=0x09`），
用错会静默读到别的属性——单元测试 `DevicePropertySelectorTests` 专门锁住这些取值。
通配符匹配（`interfaceDenyList`、SSID 名单）由自写 glob 实现（`*`/`?`，不区分大小写），不再依赖正则引擎。

## 9. 使用的 Windows 原生 API

| 领域 | API |
| --- | --- |
| Native Wi-Fi | `WlanOpenHandle` `WlanCloseHandle` `WlanEnumInterfaces` `WlanRegisterNotification` `WlanScan` `WlanGetAvailableNetworkList` `WlanGetNetworkBssList` `WlanGetProfileList` `WlanGetProfile` `WlanQueryInterface` `WlanSetInterface`(radio_state) `WlanConnect` `WlanDisconnect` `WlanFreeMemory`（`wlanapi.dll`，negotiated version 2） |
| 设备管理 | `CM_Get_Device_ID` `CM_Get_DevNode_Status` `CM_Get_DevNode_Registry_PropertyW` `CM_Get_Parent` `CM_Locate_DevNodeW` `CM_Enable_DevNode`（`cfgmgr32.dll`），`SetupDiGetClassDevs` / `SetupDiEnumDeviceInfo`（`setupapi.dll`），`CM_Register_Notification` |
| 网络与路由（只读） | `GetAdaptersAddresses` `GetIpForwardTable2`（`iphlpapi.dll`）；仅在显式开启接口度量管理时才使用 `GetIpInterfaceEntry` / `SetIpInterfaceEntry`（带 `sizeof(MIB_IPINTERFACE_ROW)=176` 断言） |
| 无线电 | 原生 `WlanQueryInterface` / `WlanSetInterface`（opcode `radio_state`，`WLAN_RADIO_STATE` 772 字节），结果读回校验 |
| 无线电（写入） | Windows 无线电管理器 COM（`{833A69FB-...}` Wlan Radio Manager、`IMediaRadioManager`、`IRadioInstance.SetRadioState`），逐块打开软开关 |
| WLAN 配置与凭据 | `WlanSetProfile` `WlanDeleteProfile` `WlanSetProfileEapXmlUserData` `WlanReasonCodeToString`（`wlanapi.dll`）；密码保护用 `CryptProtectData` / `CryptUnprotectData`（`crypt32.dll`） |
| 自绘 UI | `RegisterClassExW` `CreateWindowExW` `GetMessageW` / `DispatchMessageW` `BeginPaint` `InvalidateRect` `SetScrollInfo` `SetTimer` `WM_POWERBROADCAST`；GDI：`CreateCompatibleDC` `BitBlt` `RoundRect` `DrawTextW`(user32) `CreateFontW` `SetTextColor`；原生子窗口 `EDIT`；`TrackPopupMenuEx`；`GetOpenFileNameW` |
| 系统集成 | `Shell_NotifyIcon`（托盘）、HKCU `Run` 键（开机启动，仅当前用户） |
| 提权 | 主程序自身以 `--helper` 二次启动（`asInvoker` + `runas` 触发 UAC，请求/响应走 JSON 文件，完成单个操作即退出）；`NetworkGuardian.Helper.exe` 仍可单独构建，作为分离部署选项 |

所有 P/Invoke 结构体都对照本机 SDK 头文件（`10.0.26100.0` 的 `wlanapi.h` / `cfgmgr32.h` / `setupapi.h` /
`netioapi.h` / `iphlpapi.h`）逐字段核对，并有结构体尺寸断言（例如 `MIB_IPFORWARD_ROW2` 必须是原生 104 字节、
`DOT11_SSID` 36 字节、`WLAN_RADIO_STATE` 772 字节）。原生内存一律 `WlanFreeMemory` /
`Marshal.FreeHGlobal` / `FreeMibTable` 释放，句柄用 `SafeHandle`。

## 10. UI 与托盘

自绘 Win32（`src/NetworkGuardian.Portable/Ui`），没有 XAML、没有 WinForms/WPF：

- **总览**：外网状态、以太网链路、默认路由、Wi-Fi 无线电、物理无线网卡数量与各自 SSID/信号、
  恢复状态机状态、最近恢复动作、连续失败计数、校园网认证统计、位置权限提示、本轮计划动作。
- **无线网卡**：每块物理网卡一张卡片（状态、SSID、BSSID、信号、RSSI、频段、MAC、IP、InterfaceGUID、
  DeviceInstanceId、已保存配置、上次扫描/连接/失败），可手动扫描/连接/断开；下方为全部网络设备
  （含被过滤的虚拟网卡及其拒绝原因）。
- **以太网**：全部接口的链路、地址、网关、DNS、度量、默认路由与探测结果，以及物理以太网设备列表（只读）。
- **网络凭据库**：企业级（802.1X/EAP）网络的账号表：认证方式、EAP 方法、账号、密码（默认隐藏，可显示/清除）、域、匿名身份、服务器名校验、根证书指纹、客户端证书、自定义 XML、启用/自动连接/隐藏等开关，以及每条网络的「系统配置是否已写入」「本次运行 802.1X 失败次数/是否已临时放弃」状态与「写入到所有无线网卡」按钮。
- **设置**：所有开关与阈值、探测端点、离线命令；编辑的是工作副本，点「保存并应用」才写入并生效。
- **日志**：实时缓冲（最近 200 条），级别/分类/关键字筛选，自动滚动（仅在视图已在底部时跟随），
  一键打开日志目录、清空缓冲。
- **托盘**：打开 NetworkGuardian / 暂停（恢复）自动恢复 / 运行连通性测试 / 重新扫描 Wi-Fi /
  清除失败记录与冷却 / 打开日志目录 / 退出。关闭窗口的行为由 `startup.closeToTray` 决定。
- 渲染细节：双缓冲内存位图，`WM_PAINT` 时整屏重绘并**在绘制过程中登记可点击区域**（不存在过期命中框），
  登记时即换算成客户区坐标，所以页面滚动后命中区域随内容移动，与内联 `EDIT`、下拉菜单位置一致；
  每次按下/抬起都在 debug 级别记录命中索引（自绘控件失败是静默的，必须可追溯）；
  文本编辑用原生 `EDIT` 子窗口（点击字段时放在该字段位置，失焦提交）；下拉用 `TrackPopupMenuEx`；
  文件选择用 `GetOpenFileNameW`；DPI 由 `GetDpiForWindow` 驱动，布局尺寸按比例缩放；
  支持 `WM_PRINTCLIENT`，因此截图/校验工具可以在窗口被遮挡或隐藏时抓图且不抢焦点。
  界面为深色主题（浅色主题未实现，见「已知限制」）。

## 11. 管理员权限

- **主程序不需要管理员权限**，正常以当前用户运行。
- 只有两件事需要提权，且都由内置助手完成（同一个 exe 以 `--helper` 提权启动，完成一个操作即退出）：
  `CM_Enable_DevNode` 启用被禁用的物理无线网卡；配置里显式要求 `runAsAdministrator` 的认证程序/离线命令。
- 触发时会出现一次 UAC 提示；拒绝提权不会导致程序异常，只在日志与 UI 里记录 `ERROR_ACCESS_DENIED` 并进入冷却。
  **注意**：普通权限运行时，任何提权动作（包括自动启用被禁用网卡）都需要你在 UAC 弹窗上点「是」；
  若无人确认，网卡不会被打开——这是 Windows 的限制，程序不会绕过。要让它无人值守地生效，需要让程序以
  管理员身份常驻（例如用任务计划程序以最高权限启动），这一点程序不会自行配置。
- 不修改 UAC 设置、不写系统安全策略、不做隐蔽持久化。

## 12. Windows 位置权限（Wi-Fi API 的限制）

Windows 11 把 SSID / BSSID / RSSI 等网络标识放在位置隐私权限之后。没有权限时扫描通常可用、
BSS 细节被拒（`ERROR_ACCESS_DENIED`），部分配置下连扫描也会被拒。程序会区分「驱动失败」与「位置权限被拒」，
不崩溃、不重试风暴，继续用能拿到的信息工作，并在总览页给出中文指引（区分三种受限情形）；
**绝不尝试绕过**该权限。

开启方式：设置 → 隐私和安全性 → 位置 → 打开「定位服务」→ 打开「让桌面应用访问你的位置」→ 重启 NetworkGuardian。

## 13. 配置与日志位置

| 内容 | 路径 |
| --- | --- |
| 配置目录 | `%LOCALAPPDATA%\NetworkGuardian\` |
| 配置 | `config.json`（camelCase，含注释与尾随逗号容错） |
| 配置备份 | `config.backup.json`（每次保存前的上一版） |
| 损坏隔离 | `config.invalid.json`（无法解析时保留原文件，并用默认配置启动） |
| 日志 | `Logs\networkguardian-YYYYMMDD-NNN.log` |
| 自维护无线网络库 | `wifi-networks.json`（802.1X 账号；密码为 DPAPI 密文，另有 `.bak` 与 `.invalid.json`） |
| 提权助手临时文件 | `helper\request-*.json` / `response-*.json`（用完即删） |
| 启动异常留痕 | `startup-error.log`（进程在窗口创建前失败时唯一的线索） |
| 测试/隔离覆盖 | 环境变量 `NETWORKGUARDIAN_CONFIG_ROOT` 可整体替换上述根目录 |

配置保证：每个字段都有默认值与合法区间（加载与保存时 `ConfigValidator` 会把越界值夹回并记录 note）；
`ConfigMigrator` 处理版本升级（老文档补默认值，绝不因缺字段启动失败）；保存使用「临时文件 + `File.Replace`」原子替换；
解析失败 → 隔离 + 备份回退 + 默认值启动；配置里**不存在**任何 Wi-Fi 密钥字段（`ConfigJsonTests` 有断言），
账号密码只存在于无线网络库里且以 DPAPI 密文落盘（见 §7.1）。

JSON 读写全部走源生成的 `JsonSerializerContext`（`NetworkGuardian.Core.Serialization`）——
Native AOT 没有反射序列化器，未注册的类型会直接抛错而不是悄悄退化到反射。

## 14. 日志

- 级别 `trace` `debug` `information` `warning` `error`；文件按大小滚动 + 按天数/个数清理（LoggerFactory 已移除，直接装配单个文件提供器）。
- 记录的关键事件：网卡发现与接受/拒绝（含规则名）、无线电状态、扫描开始/完成/失败、连接尝试与结果、
  断开原因、外网丢失/恢复、认证启动与结果、PnP 启用请求与结果、异常、配置加载与校验 note。
- **隐私**：不记录 Wi-Fi 密钥、不记录无线网络库中的密码、不记录认证命令的参数字符串，只记录路径与退出码。
  802.1X 相关只记录 SSID、账号（身份）、是否写入配置/凭据、WLAN 原因码。

## 15. 开发与调试

```powershell
# 隔离环境跑一次（不改动真实 %LOCALAPPDATA% 配置，也不改动机器网络）
pwsh -NoProfile -File tools\smoke-run.ps1                 # 只观察（automaticRecovery=false）
pwsh -NoProfile -File tools\smoke-run.ps1 -Recovery       # 带自动恢复

# 页面截图（PrintWindow，不抢焦点；自动使用隔离配置根与独立单实例）
pwsh -NoProfile -File tools\ui-screenshot.ps1 -Page wireless -Out docs\ui-wireless.png
pwsh -NoProfile -File tools\ui-screenshot.ps1 -Page credentials -Out docs\ui-credentials.png

# 软开关看门狗真机验证：启动程序后用无线电管理器关掉一块网卡的软开关，测量恢复时间（默认 3 秒轮询）
pwsh -NoProfile -File tools\qa-radio-watchdog.ps1
pwsh -NoProfile -File tools\qa-radio-watchdog.ps1 -RadioIndex 2 -Rounds 3

# 读取/翻转单块网卡的软开关（Windows 设置里的那个开关；必须在 Windows PowerShell 5.1 下运行）
powershell -NoProfile -File tools\qa-wifi-radio-winrt.ps1 -Action query

# 导出一块网卡上已保存的配置 XML（对照 802.1X 配置结构时用；凭据为密文，工具不解析内容）
pwsh -NoProfile -File tools\dump-wlan-profile.ps1 -Profile HXXY-WiFi

# 关闭链路验证（WM_CLOSE → 循环停止 → 注销通知 → 关闭句柄 → flush → 进程自退）
pwsh -NoProfile -File tools\shutdown-test.ps1

# 列出某进程的顶层窗口（句柄/可见性/类名/标题）
pwsh -NoProfile -File tools\window-dump.ps1 -ProcessId 1234
```

架构分层（策略与原生代码解耦，便于测试）：

```
src/NetworkGuardian.Core            纯逻辑：模型、配置（校验/迁移）、策略（粘性、候选、退避、限流、状态机、决策引擎）、
                                    JSON 源生成序列化（不引用任何 Windows API）
src/NetworkGuardian.Windows         原生互操作 + 领域胶水：wlanapi / cfgmgr32 / setupapi / iphlpapi、PnP 清单、
                                    设备分类器、设备启用、Wi-Fi 无线电、连通性探测、位置权限、托盘、
                                    提权助手实现（Helper/HelperEntry.cs）
src/NetworkGuardian.Infrastructure  配置存储、滚动文件日志、外部命令运行器
src/NetworkGuardian.Portable        Native AOT 单文件应用：自绘 Win32 UI、GuardianHostService（监测循环 + 动作执行）、
                                    托盘、生命周期、单实例、--helper 提权模式
src/NetworkGuardian.Helper          独立的助手 exe（分离部署选项；实现与 --helper 共用 HelperEntry）
tests/NetworkGuardian.Tests         255 个单元测试（含 3 个真机用例：企业级配置写入/读取/删除、
                                    企业级配置分类、真实 802.1X 失败计数，默认跳过，用
                                    NETWORKGUARDIAN_WIFI_HARDWARE_TESTS=1 开启）
```

`GuardianHostService` 是唯一把策略与原生操作连起来的地方：按周期采集 `GuardianInput`，交给纯函数式的
`GuardianDecisionEngine` 得到动作列表，逐个执行并发布 `GuardianSnapshot` 给 UI。策略层不依赖任何 Windows API。
便携版按 Native AOT 的约束手工装配依赖（无 DI 容器、无 `LoggerFactory`、无 WinRT、无 `SystemEvents`）。

## 16. Native AOT 约束（改动时须遵守）

- 不使用反射：`JsonSerializerContext` 源生成；不 `Activator.CreateInstance`；不动态 `Type.GetType`。
- 不使用 WinRT/CsWinRT（`Windows.Devices.Radios` 等已被 wlanapi 取代）、不使用 WinForms/WPF/WinUI。
- `Assembly.Location` 在 AOT 下为空，不要依赖；`Environment.ProcessPath` 可用。
- 不使用 `Microsoft.Extensions.DependencyInjection` 与 `Microsoft.Extensions.Logging`（DI/工厂），只用 `Logging.Abstractions`。
- 不使用正则表达式（引擎约 0.5 MB 且带反射路径）——通配符匹配已自写。
- `Marshal.PtrToStructure`、`fixed` 缓冲区、`unsafe` 全部支持，现有原生层无需改动。
- 三方包需选 AOT 兼容版本；新增包前先确认它在 AOT 下不引入反射序列化/DI。

## 17. 已知限制

- 只测可达性（TCP/HTTP(S)/DNS/ICMP），不测带宽。
- 界面为深色主题；未实现浅色/跟随系统主题（WinUI 版有，自绘版为简化而去掉）。
- 扫描完成依赖驱动回报 `scan_complete`，个别老驱动靠 `scanTimeoutSeconds` 兜底。
- 位置权限未开启时退化：拿不到 BSSID / RSSI / 信道，只能按 Profile 名 + 信号质量工作。
- 需要管理员权限的两件事必须通过 UAC 助手完成，首次触发会弹一次 UAC。
- **未做真实验证**：休眠/唤醒（含 Fast Startup）、USB Wi-Fi 物理拔插、被禁用网卡的提权启用（需要一块处于
  「已禁用」状态的物理无线网卡 + UAC 交互）、校园网认证接真客户端。
- **未验证的 802.1X 场景**：`WPA3-Enterprise`（本机三块网卡上 WLAN 服务都以原因码 1206 拒绝，程序因此
  拒绝生成该配置，要求填自定义 XML）、EAP-TTLS / PEAP-TLS 等第三方 EAP 方法（需系统安装对应 EAP 方法，
  只能填自定义 XML）、EAP-TLS 携带真实客户端证书完成认证（本机无可用客户端证书，
  只验证了配置与「证书固定」凭据被 WLAN 服务接受）、`wifi-networks.json` 跨用户/跨机器复制时密码
  无法解密（DPAPI 作用域所限，程序会提示重新填写而不会静默失败）。
- 仅 x64；未做 ARM64 与 MSIX 打包。
- Windows 位置权限、硬件开关、Airplane 模式等外部因素仍可能让恢复动作失败——程序会如实记录失败原因。
- 发布包 exe 哈希每次构建会变化（编译期非确定性），未声明可复现构建。

## 18. 安全边界（明确不做）

不导出/显示/记录 Wi-Fi PSK；不绕过 UAC；不修改系统安全策略；不做隐蔽持久化（开机启动只写 HKCU `Run`）；
不修改未知路由；不自动加入陌生开放网络；不关闭防火墙；不改动 Windows 位置隐私限制；
不写入任何与保活无关的系统设置；不上传任何数据（无遥测、无网络回传，除用户自己配置的认证/命令外不发起外部请求）。

无线网络库的密码只以 DPAPI（当前用户）密文保存；程序**只在用户明确把某个 SSID 加入库后**才为该网络写入
802.1X 配置，不会为库中不存在的企业级网络猜测或探测凭据；连接失败达到上限后本次运行放弃该网络而不是
无限重试（避免把校园账号打到锁定）。

---

## 19. 本机实测结果（Windows 11 22631）

| 项目 | 结果 |
| --- | --- |
| 设备枚举 | 21 个网络类节点 → 5 个物理（Realtek PCIe 有线、Intel AX201 PCIe Wi-Fi、MediaTek MT7961 USB Wi-Fi、AIC8800D80 USB Wi-Fi、Realtek USB 有线），16 个正确过滤 |
| PnP ↔ WLAN 关联 | `c7bd53a9-…`（AIC8800D80）与 `f6ea3477-…`（MT7961）命中 `rule=wlansvc-correlation` |
| 探测 | 多端点 TCP/HTTP(S)/DNS 组合，按接口分别探测（本机 3–5 个端点全部按预期返回） |
| 连接 | 按「信号 + 5 GHz 加成」选中已保存的 `ZhangAndroid` 并保持（粘性，不主动切换） |
| 关闭链路 | `WM_CLOSE` → 监测循环停止 → 注销 WLAN 通知 → 关闭 WLAN 句柄 → host disposed → 日志 flush → 进程自退 |
| 便携包 | 单文件 8.24 MB；`tools\verify-portable.ps1` 全部检查通过（见 `docs/session-log-2026-09-18.md` 第 4 节） |
| 自维护无线网络库 | 真机验证：`HXXY-WiFi`（WPA2-Enterprise, PEAP/MSCHAPv2）按库中账号写入配置 + EAP 凭据成功；错误密码连续 5 次真实失败后计数到 5 并放弃该网络，测试配置已删除、用户自己的同名配置完好；`wifi-networks.json` 中密码为 DPAPI 密文，磁盘上无明文 |
| 软开关看门狗 | 真机验证：程序运行中把一块网卡的软开关关掉（无线电管理器，等同设置页操作），**2.29 s / 2.30 s** 被看门狗打开（第 1 轮 0.26 s 由更快的通知路径完成，因此第 2、3 轮才是看门狗证据），日志出现「无线电看门狗：已把软件关闭的无线网卡重新打开」 |
