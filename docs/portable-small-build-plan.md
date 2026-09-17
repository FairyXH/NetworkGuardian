# NetworkGuardian 小体积单文件便携版方案

> 目标：把当前 **248.8 MB / 515 个文件**的自包含发布包，做成 **~10 MB 以内的单文件便携 exe**，
> 在没有任何开发环境（无 .NET、无 Windows App Runtime、无 VS）的 Windows 10 2004+/x64 机器上直接双击运行。
>
> 结论先说：**能，预计 6–8 MB 单文件；但必须放弃 WinUI 3 与 WinRT，UI 改为自绘 Win32。**

---

## 1. 实测数据（本机真实 publish 产物，非估算）

环境：Windows 11 22631 x64、.NET SDK 10.0.302、MSVC 链接器可用。
探针工程：`D:\Files\Develop\Windows\PortableProbe\`（5 个独立工程，每个都能单独复现）。

| # | 构建方式（均含 P/Invoke，除特别注明） | 单文件 exe | 目标机是否需装运行时 |
| --- | --- | --- | --- |
| 1 | Native AOT：JSON(源生成) + Socket + **HttpClient/HTTPS(TLS)** | **4.72 MB** | 不需要 |
| 2 | Native AOT：JSON(源生成) + Socket（无 HTTPS） | **2.09 MB** | 不需要 |
| 3 | 裁剪自包含（`PublishTrimmed=full`）：JSON + Socket + HttpClient | 10.74 MB | 不需要 |
| 4 | 裁剪自包含（仅 1 个 P/Invoke + Console.WriteLine） | 9.93 MB | 不需要 |
| 5 | WinForms 自包含 + 单文件 + 压缩（不裁剪） | 68.25 MB | 不需要 |
| 6 | WinForms **强制**裁剪（SDK 明确不支持） | 47.02 MB | 不需要 |
| 7 | **当前 WinUI 3 自包含发布包** | 248.8 MB（515 文件） | 不需要 |

实测运行：#1 的 exe 直接执行输出 `NetworkGuardian adapters=16383 tls=HttpRequestException`、退出码 0
（TLS 栈已链接并真实走到 HTTPS 握手失败，因为本机 127.0.0.1:9 无监听）。

复现命令：

```powershell
cd D:\Files\Develop\Windows\PortableProbe
dotnet publish BareProbe\BareProbe.csproj   -c Release -o bare-out        # 9.93 MB
dotnet publish FloorProbe\FloorProbe.csproj -c Release -o floor-out       # 10.74 MB
dotnet publish AotProbe\AotProbe.csproj     -c Release -o aot-out2        # 4.72 MB
dotnet publish WinFormsProbe\WinFormsProbe.csproj -c Release -o wf-out    # 68.25 MB
```

---

## 2. 当前发布包 248.8 MB 的构成

| 体积来源 | 大小 | 说明 / 能否去掉 |
| --- | --- | --- |
| `onnxruntime.dll` + `DirectML.dll` | 20.73 + 17.84 = **38.6 MB** | Windows App SDK 1.8 自包含载荷里自带的 ML 组件，本项目完全不使用。SDK 的 `WindowsAppSDKMLPassthroughOnnxRuntime` 只作用于 **C++** 工程（`ClCompile`），C# 侧无受支持开关；手工删除属于改运行时布局，有目标机偶发加载失败风险 |
| `Microsoft.Windows.SDK.NET.dll` | **23.7 MB** | WinRT 投影（CsWinRT）。只要目标框架是 `net8.0-windows10.0.19041.0` 就会被引用；不用 WinRT API 才能去掉 |
| `Microsoft.ui.xaml.dll` / `Microsoft.WinUI.dll` / `Microsoft.UI.Xaml.Controls.dll` | 14.39 + 6.98 + 6.29 = **27.7 MB** | WinUI 3 运行时，换 UI 框架才能去掉 |
| `System.Private.CoreLib.dll` + `coreclr.dll` + BCL 其余 | **~60 MB** | .NET 运行时。只有 Native AOT 能去掉 |
| Windows App Runtime 其余（Bootstrap、MRM、DWrite 等） | ~40 MB | 同上 |
| 83 个语言资源目录（`zh-CN`、`en-US` …） | **仅 3.25 MB** | "不要多语言"省不了多少，不是主因 |
| 提权助手（单文件自包含压缩） | 39.7 MB | 已在上一轮优化中从 95.2 MB 降下来 |

即：**三个大头是 WinUI 运行时、WinRT 投影、WinAppSDK 的 ML 载荷**，合计约 90 MB，且都不可用受支持的方式裁剪；
剩下的 ~60 MB 是 .NET 运行时，只有 AOT 能消掉。所以"10 MB 且保留 WinUI"在物理上不成立——
WinUI 3 的自包含 hello-world 就要约 150 MB。

---

## 3. 为什么三条路都走不通（或代价过高）

| 路线 | 结果 | 原因 |
| --- | --- | --- |
| 保留 WinUI 3 + 自包含 | 248.8 MB | 见上；无受支持的裁剪手段 |
| 保留 WinUI 3 + 框架依赖单文件 | exe 约 3–6 MB，但**目标机必须安装 .NET 8 运行时 + Windows App Runtime** | 违背"零依赖便携"；且本机已装运行时版本低于 SDK 要求时会直接弹 "This application could not be started" |
| WPF（自包含） | ~120 MB，裁剪官方不支持 | WPF 不支持 trimming |
| **WinForms（自包含，不裁剪）** | **68.25 MB** | SDK 直接拒绝 `PublishTrimmed`（`NETSDK1175`：Windows Forms is not supported or recommended with trimming）。强制绕过（`_SuppressWinFormsTrimError`）后 47 MB，且属不受支持组合，运行期有裁剪缺失风险 |
| 裁剪自包含（非 AOT，自绘 UI） | ≥ 9.93 MB 起步 | **仅一个 `Console.WriteLine` + 一个 P/Invoke 的裸程序就是 9.93 MB**——.NET 运行时作为单文件已经吃满 10 MB 预算，加上业务代码必然超标 |
| **Native AOT + 自绘 UI** | **4.72 MB（含 TLS/HTTP/JSON/Socket）** | 唯一能真正落到 10 MB 以内的路线 |

---

## 4. 推荐方案：Native AOT + 自绘 Win32 UI

### 4.1 体积账（预计 6–8 MB）

| 组成 | 预计 |
| --- | --- |
| Native AOT 基线（P/Invoke + JSON 源生成 + Socket + HttpClient/TLS + Task） | 4.72 MB（已实测） |
| 业务逻辑（Core + Windows + Infrastructure 约 18.9k 行，其中大量是 P/Invoke 与字符串/结构体处理） | +0.5 ~ 1.5 MB |
| 自绘 Win32 UI + 托盘 + 滚动日志 | +0.3 ~ 0.8 MB |
| **合计** | **约 6 ~ 8 MB** |
| 若砍掉 HTTPS 探测（只保留 TCP/DNS/ICMP） | 约 4 ~ 5 MB |

`<OptimizationPreference>Size</OptimizationPreference>`、`StripSymbols=true`、`InvariantGlobalization=true`
（中文界面不受影响，只影响文化敏感的排序/格式化，程序内不使用）、`StackTraceSupport=false` 已在探针中启用。

### 4.2 复用与替换清单

| 模块 | 处理 | 说明 |
| --- | --- | --- |
| `NetworkGuardian.Core`（策略/配置模型/决策引擎） | **原样复用** | 目标框架 `net8.0`，无 Windows 依赖，AOT 友好 |
| `NetworkGuardian.Infrastructure`（配置存储、日志、外部命令） | 小改 | `System.Text.Json` 改源生成（`JsonSerializerContext`）；日志改手工装配 |
| `NetworkGuardian.Windows` 的 Native/ 层（wlanapi/cfgmgr32/setupapi/iphlpapi/netioapi） | **原样复用** | 全部是 P/Invoke 与 `Marshal`，AOT 完全支持 |
| `NetworkGuardian.Windows` 的 Tray/`TrayIcon.cs` | **原样复用** | 本来就是纯 Win32 `Shell_NotifyIcon` + 消息循环 |
| `NetworkGuardian.Windows` 的 Radio/ | **去 WinRT** | 见 4.3，原生读写几乎已就位 |
| `GuardianHostService` | 移植 | 唯一改动：把 `DispatcherQueue.TryEnqueue` 换成 `SynchronizationContext.Post`（约 10 处） |
| ViewModels / XAML | **不复用** | WinUI 耦合（`Visibility`、绑定、`x:Bind`），便携版改由自绘渲染器直接读 `GuardianSnapshot` |
| `Microsoft.Extensions.DependencyInjection` | **去掉** | 反射构造注入在 AOT 下需源生成，直接手工装配更简单（依赖图很小） |
| `Microsoft.Extensions.Logging` 工厂 | 去掉 | 保留自写的 `RollingFileLoggerProvider`，改为直接的 `ILogger` 实现（接口来自 `Abstractions`，AOT 友好） |
| `Microsoft.Win32.SystemEvents`（休眠/唤醒） | 去掉 | 改为在自己窗口里处理 `WM_POWERBROADCAST`，无需额外包 |

### 4.3 去 WinRT：无线电的原生读写（关键前置）

现状（好消息）：**原生读路径已经实现**，只缺原生写。

- `WlanApiNative.cs` 已声明：`WlanIntfOpcodeRadioState = 4`、`Dot11RadioStateUnknown/On/Off = 0/1/2`、
  `WLAN_PHY_RADIO_STATE`、`WLAN_RADIO_STATE`（`fixed uint PhyRadioState[64 * 3]`，每 PHY 3 个 uint = 12 字节，
  与原生布局一致）、`WlanNotificationMsmRadioStateChange = 7`。
- `Radio/NativeRadioAccess.cs` 已通过 `WlanQueryInterface(opcode=4)` 读取各 PHY 的软件/硬件无线电状态。
- 唯一使用 WinRT 的地方是 `Radio/WifiRadioController.cs`：`Radios.Radio.GetRadiosAsync()`、
  `radio.SetStateAsync(...)`、`radio.StateChanged`、`Radios.RadioKind.WiFi`。

头文件依据（`C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\wlanapi.h`）：

```c
// 663-667
typedef enum _DOT11_RADIO_STATE { dot11_radio_state_unknown = 0, dot11_radio_state_on, dot11_radio_state_off };
// 671
#define WLAN_MAX_PHY_INDEX 64
// 673-677
typedef struct _WLAN_PHY_RADIO_STATE { DWORD dwPhyIndex; DOT11_RADIO_STATE dot11SoftwareRadioState; DOT11_RADIO_STATE dot11HardwareRadioState; };
// 679-682
typedef struct _WLAN_RADIO_STATE { DWORD dwNumberOfPhys; WLAN_PHY_RADIO_STATE PhyRadioState[WLAN_MAX_PHY_INDEX]; };
// WLAN_INTF_OPCODE 顺序：autoconf_start=0, autoconf_enabled=1, background_scan_enabled=2,
//                       media_streaming_mode=3, radio_state=4, bss_type=5, interface_state=6, current_connection=7
```

要做的：

1. 新增 `WlanSetRadioState(handle, interfaceGuid, bool on)`：`WlanQueryInterface` 先读回当前
   `WLAN_RADIO_STATE` 以保留 `dwPhyIndex` 与硬件状态，把每个 PHY 的 `dot11SoftwareRadioState` 改为
   `dot11_radio_state_on/off`，再用 `WlanSetInterface(opcode=4, size=sizeof(WLAN_RADIO_STATE), data)` 写回。
2. `WifiRadioController` 改为只走原生：状态变化事件改用已有的 WLAN 通知
   `wlan_notification_msm_radio_state_change`（`WlanNotificationMsmRadioStateChange = 7`）触发重新读取。
3. **真机验证必需**：读 → 关 → 读 → 开 → 读，确认硬件开关存在时返回失败而不是误报成功；
   若 `WlanSetInterface` 返回 `ERROR_ACCESS_DENIED`，说明该 opcode 需要提权——那就把"打开无线电"也交给提权助手
   （助手里调用同样的 `WlanSetInterface`）。
4. 完成后 `NetworkGuardian.Windows` 的目标框架可从 `net8.0-windows10.0.19041.0` 降为 `net8.0-windows`，
   WinRT 投影（23.7 MB）彻底消失。

### 4.4 UI：自绘 Win32 的形态

不引入任何托管 UI 框架，直接 `CreateWindowEx` + GDI：

- 主窗口：无边框或系统边框皆可；客户区用 `GDI+`/GDI 自绘深色卡片（`RoundRect` + 双层描边模拟玻璃边缘）。
- 布局：左侧 6 项导航（总览 / 无线网卡 / 以太网 / 设置 / 日志 / 关于），右侧内容区；自绘滚动条或用
  `WS_VSCROLL` 原生滚动。
- 交互控件：开关/按钮/下拉均自绘（命中测试 + `WM_LBUTTONDOWN`），文本用 `DrawTextW`；输入用
  `CreateWindowEx("EDIT")` 原生子窗口（可省下大量自绘工作）。
- 托盘：直接复用现有 `TrayIcon.cs`（Win32 消息 + `Shell_NotifyIcon`）。
- 线程模型：所有绘制在 UI 线程；`GuardianHostService` 通过 `SynchronizationContext.Post` 投递快照，
  再 `InvalidateRect` 触发重绘。
- 语言：界面字符串全部内联中文（可选 `en-US` 由编译常量切换），不再有 83 个资源目录。

预期观感：深色、圆角、卡片式，接近但不等于 WinUI Fluent（无 Mica/Acrylic 合成）。

### 4.5 实施阶段与门限

| 阶段 | 内容 | 出口条件（可验证） |
| --- | --- | --- |
| 0 | 新建 `src/NetworkGuardian.Portable`（WinExe + `net8.0-windows` + AOT），接入 Core/Infrastructure，最小窗口显示 `GuardianSnapshot`（外网/以太网/默认路由/Wi-Fi 计数）、托盘菜单、配置读写 | 真实 publish 出单文件 exe，**实测体积 ≤ 8 MB**，能启动并显示实时状态 |
| 1 | 原生无线电写路径 + `WifiRadioController` 去 WinRT；`NetworkGuardian.Windows` 目标框架降到 `net8.0-windows` | 真机上读/关/开状态可复现；exe 中不再含 WinRT 投影 |
| 2 | 自绘完整页面：总览、无线网卡（含按网卡扫描/连接）、以太网、日志（实时 + 级别/分类筛选）、设置（阈值与离线命令编辑） | 五个页面功能与 WinUI 版等价（可用 `--page` 逐页截图核对） |
| 3 | 装配收口：JSON 源生成、去掉 DI 容器、`WM_POWERBROADCAST` 替代 `SystemEvents` | `dotnet publish` 0 警告；AOT 分析器无 IL2xxx 阻塞项 |
| 4 | 打包与验证：`tools/build-portable.ps1`、`tools/verify-portable.ps1`（复用现有 verify 思路：中性目录 + 干净 PATH + 无 `DOTNET_ROOT`） | 15 项校验通过 + 体积门限 + 真机冒烟（扫描/连接/粘性/关闭链路） |

### 4.6 Native AOT 约束清单（实现时须遵守）

- 不使用反射：`JsonSerializerContext` 源生成；不 `Activator.CreateInstance`；不动态 `Type.GetType`。
- 不使用 WinRT/CsWinRT：所有 Windows 能力走 P/Invoke（本项目天然如此，除无线电外）。
- 不使用 WinForms/WPF：均不支持 AOT。
- `Assembly.GetName().Version` 可用；`Assembly.Location` 在 AOT 下为空，代码中不要依赖它。
- `Marshal.PtrToStructure`、`fixed` 缓冲区、`unsafe` 全部支持；现有原生层无需改动。
- 第三方包需选 AOT 兼容版本；当前只剩 `System.Text.Json`（源生成后可 AOT）。
- 构建机需要 MSVC 链接器（本机已验证可用）。

### 4.7 风险与回退

| 风险 | 影响 | 缓解 / 回退 |
| --- | --- | --- |
| `wlan_intf_opcode_radio_state` 写操作被拒（需提权或驱动不支持） | 无法用程序打开 Wi-Fi 无线电 | 把该动作交给提权助手；再不行则只做状态**检测与提示**，不自动开关（其余保活能力不受影响） |
| 自绘 UI 观感不如 WinUI | 用户偏好落差 | 双轨：`release/` 保留 WinUI 完整版，便携版作为"轻量分发版"并存 |
| AOT 下某个第三方/BCL 路径不可用 | 局部功能缺失 | 阶段 0 先跑通最小切片，再逐页接入，避免一次性重写后才发现问题 |
| 体积超预期（>10 MB） | 未达目标 | 已实测基线 4.72 MB；若超标，砍 HTTPS 探测（-2.6 MB）或去掉 HTTP 请求的 `Encoding` 等次要依赖 |
| 双份代码维护成本 | 长期负担 | 便携版不复制业务逻辑，只复制 UI 壳；Core/Windows/Infrastructure 单一来源 |

---

## 5. 复现与探针工程说明

`D:\Files\Develop\Windows\PortableProbe\`（仓库外，实验用，可随时删除）：

| 工程 | 关键配置 | 产物 |
| --- | --- | --- |
| `BareProbe` | `PublishTrimmed=full` + 单文件 + 压缩 + `InvariantGlobalization` | 9.93 MB |
| `FloorProbe` | 同上 + JSON/Socket/HttpClient | 10.74 MB |
| `AotProbe` | `PublishAot=true` + `OptimizationPreference=Size` + `StripSymbols` + JSON 源生成 | 2.09 / 4.72 MB |
| `WinFormsProbe` | `UseWindowsForms=true`；不带/带 `_SuppressWinFormsTrimError` | 68.25 / 47.02 MB |

---

## 6. 待确认的决策项

1. **是否接受放弃 WinUI 观感**（自绘 Win32 深色卡片，无 Mica/Acrylic）——这是 ≤10 MB 单文件的硬前提。
2. **单轨还是双轨**：便携版取代现有 WinUI 版，还是两者并存（`release/` 与 `portable/`）。
3. **HTTPS 探测是否必须**：保留则体积 +2.6 MB（预计 6–8 MB），砍掉可到 4–5 MB 但只能 TCP/DNS/ICMP 探测。
4. **是否保留提权助手**：AOT 版可把助手也做成 AOT 单文件（预计 +2–3 MB），或让主程序按需自提权重启（不加体积但需设计）。
