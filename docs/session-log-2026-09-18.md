# NetworkGuardian 会话记录（2026-09-18）：小体积单文件改造

> 本文记录「抛弃 WinUI 3、改用自绘 UI、做成单文件便携版」这次的完整产出、实测数据、
> 真机验证证据，以及改造过程中暴露的 8 个缺陷与修复。
> 项目根：`D:\Files\Develop\Windows\NetworkGuardian`；方案背景见
> [`portable-small-build-plan.md`](portable-small-build-plan.md)（改造前的实测体积矩阵）。

---

## 1. 会话目标与结论

| 目标 | 结果 |
| --- | --- |
| 抛弃 WinUI 3，改用自绘 UI | 完成：`RegisterClassExW` + GDI 双缓冲自绘，五个页面功能对齐（总览 / 无线网卡 / 以太网 / 设置 / 日志） |
| 做成小体积单文件便携 exe | 完成：**7.80 MB 单文件**（原先 248.8 MB / 515 文件），提权助手 2.80 MB（原先 39.7 MB） |
| 与 WinUI 版功能等价 | 完成：策略/配置/日志/互操作层原样复用；UI 覆盖原界面的全部信息与操作（手工扫描/连接/断开、启用设备、保存并应用配置、日志筛选等） |
| 去掉 WinRT 与不可裁剪依赖 | 完成：无线电改走 `wlanapi`（`wlan_intf_opcode_radio_state`），目标框架从 `net8.0-windows10.0.19041.0` 降到 `net8.0-windows`，`Microsoft.Windows.SDK.NET.dll`（23.7 MB）不再被引用 |
| 完成测试 | 完成：196/196 单元测试通过；构建 0 警告；`tools\build-portable.ps1` 体积门限 8 MB 通过；`tools\verify-portable.ps1` 28/28 通过（含提权助手真实执行与关闭链路） |

**规模**：C# 文件 84 → 78（删掉 WinUI 工程 28 个文件，新增自绘 UI 15 个文件）；测试 161 → 196。

---

## 2. 交付物

### 2.1 代码结构

```
src/NetworkGuardian.Core            纯逻辑（模型/配置/策略/JSON 源生成序列化，无 Windows 依赖）
src/NetworkGuardian.Windows         原生互操作 + 领域胶水（wlanapi / cfgmgr32 / setupapi / iphlpapi、托盘、无线电）
src/NetworkGuardian.Infrastructure  配置存储、滚动文件日志、外部命令运行器
src/NetworkGuardian.Portable        【新】Native AOT 单文件应用（自绘 Win32 UI + 监测循环宿主 + 托盘 + 生命周期）
  Program.cs                        入口：单实例、--minimized/--page/--visible、启动失败留痕
  Host/PortableApp.cs               装配（无 DI）：配置、日志、host、窗口、托盘、电源广播、关闭语义
  Host/SimpleLoggerFactory.cs       20 行的 ILoggerFactory（替代 DI 版 LoggerFactory）
  Services/GuardianHostService.cs    从 WinUI 版移植（仅改命名空间与无线电装配，其余逐行保留）
  Interop/NativeMethods.cs          user32 / gdi32 / comdlg32 P/Invoke 与常量
  Ui/Canvas.cs                      双缓冲 GDI 画布（圆角卡片、文本、裁剪、命中区登记）
  Ui/Widgets.cs                     按钮/开关/数字步进/文本框/下拉/胶囊/状态点
  Ui/MainWindow.cs                  窗口过程、页头/导航/内容/Toast、滚动、内联文本编辑、下拉菜单
  Ui/Pages/*.cs                     五个页面
  Ui/Format.cs / Ui/Theme.cs        文案格式化、深色配色与字体缓存
  Ui/FileDialog.cs                  GetOpenFileNameW（AOT 下可用的文件选择）
src/NetworkGuardian.Helper          提权助手（Native AOT 单文件，requireAdministrator）
tests/NetworkGuardian.Tests         196 个测试
```

### 2.2 工具脚本（tools/）

| 脚本 | 用途 |
| --- | --- |
| `build-portable.ps1` | Release 构建（0 警告门限）+ 测试 + 助手 AOT + 主程序 AOT + 体积门限（≤8 MB）+ 可选 `-Zip`（含版本化顶层目录 + 解压哈希校验） |
| `verify-portable.ps1` | 发布包验证：中性目录 + 干净环境（PATH 无 dotnet、无 DOTNET_ROOT）+ 单文件/无符号校验 + 窗口类 + **窗口消息响应性** + 日志/配置副作用 + 提权助手真实执行 + 关闭链路 + 残留检查（28 项） |
| `smoke-run.ps1` | 隔离配置根下的真机冒烟（`-Recovery` 可开自动恢复） |
| `ui-screenshot.ps1` | 页面截图（`WM_PRINTCLIENT` + `PrintWindow`，不抢焦点、窗口被遮挡也能抓；自动隔离配置根与独立单实例） |
| `shutdown-test.ps1` | 发 `WM_CLOSE`，断言日志中的关闭序列与进程自退（不需要强杀） |
| `window-dump.ps1` | 列出某进程的全部顶层窗口（句柄/可见性/类名/标题） |
| `portable-notes.txt` | 打包进发布包的「发布说明.txt」源文件 |

### 2.3 文档

| 文件 | 内容 |
| --- | --- |
| `README.md` | 重写为便携版主文档：交付形态与体积、功能、编译/测试/打包、运行与 QA 开关、设计原则、校园网认证、物理网卡判定、原生 API 清单、UI 与托盘、权限、位置权限、配置/日志路径、Native AOT 约束、已知限制、安全边界、本机实测 |
| `docs/session-log-2026-09-18.md` | 本文 |
| `docs/session-log-2026-09-17.md` | WinUI 3 版本的会话记录（历史参考） |
| `docs/portable-small-build-plan.md` | 体积矩阵与方案分析（改造前），顶部追加了实施结果 |
| `docs/ui-*.png` | 便携版五个页面的实际窗口截图（由 `ui-screenshot.ps1` 抓取） |

---

## 3. 体积账（全部为本机实测，非估算）

| 阶段 | 制品 | 体积 | 变化原因 |
| --- | --- | --- | --- |
| 改造前 | `release\`（WinUI 3 自包含） | 248.8 MB / 515 文件 | WinUI 运行时 ~28 MB + WinRT 投影 23.7 MB + WinAppSDK ML 载荷 38.6 MB + .NET 运行时 ~60 MB + 其余 |
| 改造前 | `helper\NetworkGuardian.Helper.exe` | 39.7 MB | 自包含单文件（压缩） |
| 第一步 | Native AOT 骨架（Core/Windows/Infrastructure + 自绘窗口 + 总览页） | 8.31 MB | 去掉 WinUI/WinRT，改用 AOT；此时仍带 `System.Text.RegularExpressions` 与关联 PDB |
| 第二步 | 去掉正则引擎（自写 glob 与 token 边界扫描） | 7.80 MB | 正则引擎及其反射路径约 -0.5 MB |
| 第二步 | 提权助手改 Native AOT | 39.7 → **2.80 MB** | 不再随包复制第二份 .NET 运行时 |
| 最终 | `release\`（exe + helper + 发布说明） | **10.61 MB**（exe 7.80 MB） | 单文件、无 PDB、无 loose DLL/JSON |
| 最终 | `NetworkGuardian-0.9.0-win-x64.zip` | 5.15 MB | 压缩后传输包（解压后 exe 哈希与 `release\` 一致） |

占比最大的剩余项：TLS/HTTP 栈（HTTP(S) 探测需要）、`System.Text.Json` 源生成、BCL 中的 `Task`/套接字/注册表/进程。
按方案第 4.1 节的估算（6–8 MB）落在区间上沿；若砍掉 HTTPS 探测可再降到约 5 MB，但会失去「认证页重定向检测」能力，故保留。

AOT 属性（`src/NetworkGuardian.Portable/NetworkGuardian.Portable.csproj`）：
`PublishAot` `InvariantGlobalization` `UseSystemResourceKeys` `OptimizationPreference=Size`
`StackTraceSupport=false` `StripSymbols=true` `DebugType=none` `AllowedReferenceRelatedFileExtensions=none`。

---

## 4. 真机验证证据

环境：Windows 11 22631 x64、.NET SDK 10.0.302、MSVC 链接器可用；
硬件含 Intel AX201（PCIe）、MediaTek MT7961（USB）、AIC8800D80（USB）、Realtek PCIe/USB 有线网卡、VMware VMnet、蓝牙 PAN。

| 验证项 | 证据 |
| --- | --- |
| 构建与测试 | `dotnet build -c Release` = 0 警告 0 错误；`dotnet test` = 196/196 |
| 体积门限 | `build-portable.ps1`：exe 7.8 MB ≤ 8 MB，助手 2.8 MB，包合计 10.61 MB，zip 5.15 MB（解压 exe 哈希一致） |
| 中性目录 + 干净环境 | `verify-portable.ps1`：把包复制到 `%TEMP%`，`PATH=C:\Windows\system32;C:\Windows`、`DOTNET_ROOT` 未设置，进程存活、窗口类 `NetworkGuardianPortableWindow` 存在、**窗口线程能被 `SendMessageTimeout` 应答** |
| 副作用 | 首次运行生成配置（camelCase 键、无密钥字段）与滚动日志；日志含启动、host、设备枚举、探测、托盘创建 |
| 提权助手 | 未知设备 → `DeviceNotFound`；真实设备 `USB\VID_0BDA&PID_8153\001000001` → `Succeeded`、`helperElevated=True`、`problemCodeAfter=0` |
| 关闭链路 | `WM_CLOSE` → `Main window closed; shutting down` → `Guardian monitor loop stopped` → `Unregistered WLAN notifications` → `WLAN client handle closed` → `Guardian host disposed` → `NetworkGuardian stopped`，进程自退；日志只有**一个**文件（无「dispose 后写入」） |
| 设备枚举 | 21 个网络类节点 → 5 个物理、16 个正确过滤（VMware×2、WAN Miniport×8、蓝牙 PAN×2、Wi-Fi Direct 虚拟适配器×4） |
| PnP ↔ WLAN 关联 | `c7bd53a9-…`（AIC8800D80）与 `f6ea3477-…`（MT7961）命中 `rule=wlansvc-correlation` |
| 界面 | 五个页面截图见 `docs/ui-*.png`；无线页显示真实连接（ZhangAndroid，5 GHz ch40，100%/-59 dBm），设置页开关/步进/文本字段布局正常 |
| 单实例 | 用户正在运行的实例存在时，新进程只向既有窗口投递「显示窗口」消息后退出（并在 `startup-error.log` 留痕） |

---

## 5. 改造中暴露的缺陷与修复（全部已修复并回归）

| # | 现象 | 根因 | 修复 |
| --- | --- | --- | --- |
| 1 | 窗口存在但**不绘制**（截图全黑/单色），日志有一条 `UI action failed` | `DrawTextW` 被声明在 `gdi32.dll`，实际在 `user32.dll` → `EntryPointNotFoundException`；异常被窗口过程兜底 catch 吞掉，绘制中途放弃 | 修正 `DllImport` 到 user32；窗口过程的兜底 catch 保留（它正是定位这个问题的线索） |
| 2 | 窗口「未响应」（`IsHungAppWindow=True`，外部 `SetWindowPos`/`SendMessage` 永久阻塞） | `PortableApp.RunAsync` 是 async：`await` 之后的续体在线程池上运行，于是**窗口与消息循环被创建在池线程上**，主线程阻塞在 `GetAwaiter().GetResult()` | 改为同步 `PortableApp.Run`：窗口、托盘、消息循环、关闭全部在进程主线程；异步初始化用 `GetAwaiter().GetResult()` 等待（没有任何步骤需要 UI 线程） |
| 3 | 关闭时进程不退出（必须强杀） | `ShutdownAsync` 先把 `_window` 置空，之后 `ShutdownAndQuitAsync` 里的 `_window?.PostQuit()` 变成空操作 → 消息循环永不结束 | 先捕获窗口引用再关闭，再投递 `WM_APP_QUIT`；由窗口过程在自己线程上 `DestroyWindow` + `PostQuitMessage` |
| 4 | 关闭序列的日志被截断，并额外生成一个 `-001.log` | `GuardianHostService.DisposeAsync` 里 dispose 了**共享**的文件日志器，之后 app 再写日志时 provider 静默重开新文件 | 日志器所有权归应用层：host 不再 dispose 它；应用在写完 `NetworkGuardian stopped` 后最后 dispose |
| 5 | 截图脚本调用 `SetWindowPos`/`SetForegroundWindow` 时永久卡住 | 目标窗口线程无响应时，跨进程同步窗口调用会一直等待（这是发现缺陷 #1/#2 的最初入口） | 脚本改为 `WM_PRINTCLIENT` + `PrintWindow`：不抢焦点、窗口被遮挡/隐藏也能抓，且不再发送同步窗口消息 |
| 6 | 压缩包顶层目录名变成临时目录名 | `ZipFile.CreateFromDirectory(src, …, includeBaseDirectory: true)` 取的是**源目录名** | 以版本化目录 `<name>-<version>-win-x64` 本身作为源目录；并在脚本内解压校验 exe 哈希 |
| 7 | 打包脚本误判「有警告」 | 用 `\bwarning\b` 抓构建输出，命中了摘要行 `0 Warning(s)` | 改为解析 `^\s*(\d+)\s+Warning\(s\)` 摘要 |
| 8 | 便携版启动后立刻退出、且不写任何日志 | 用户正运行的旧版（WinUI release）实例持有单实例互斥体 | 行为本身正确；为验证脚本增加 `NETWORKGUARDIAN_INSTANCE_SUFFIX`，并在退出路径写入 `startup-error.log` 说明原因 |

架构性变化（非缺陷）：

- **无线电改原生**：`Windows.Devices.Radios`（CsWinRT，AOT 不可用，且带来 23.7 MB 投影）替换为
  `WlanQueryInterface` / `WlanSetInterface`（`wlan_intf_opcode_radio_state`）。写入后**读回校验**，
  硬件开关或策略接管时报失败而不是假成功；`WLAN_RADIO_STATE` 的 772 字节布局与 opcode=4 由测试锁定。
- **JSON 源生成**：`JsonSerializerContext` 成为唯一解析器（未注册类型直接抛错，不退化到反射）；
  `JsonStringEnumConverter` 是反射实现，改用 4 个封闭的 camelCase 枚举转换器，保持 `config.json` 的既有拼写。
- **去掉 DI 与日志工厂**：`Microsoft.Extensions.Logging` 的 `LoggerFactory` 基于 DI，AOT 分析器会告警；
  改为直接装配 `RollingFileLoggerProvider`（`SimpleLoggerFactory` 20 行），调用点（`ILogger<T>`）不变。
- **去掉正则**：SSID/设备名单的通配符匹配与虚拟厂商 token 扫描自写为线性扫描。

---

## 6. 关键设计决策

1. **单轨**：便携版**取代** WinUI 版，不保留双份 UI。WinUI 版可从 git 历史 `297b868` 取回。
2. **自绘 UI 的形态**：整屏重绘 + 绘制时登记命中区（不存在过期命中框）；文本输入用原生 `EDIT` 子窗口
   （点击字段时就地创建，失焦提交）；下拉用 `TrackPopupMenuEx`；文件选择用 `GetOpenFileNameW`；
   滚动用 `WS_VSCROLL` + 视口偏移。深色主题固定（浅色主题未实现，已记入已知限制）。
3. **复用而非重写**：`GuardianHostService`、配置存储、滚动日志、全部原生互操作层逐行保留，
   因此 2026-09-17 会话的全部真机结论（粘性、候选过滤、防风暴、位置权限退化）继续成立。
4. **体积门限进 CI 脚本**：exe > 8 MB 或构建有警告直接失败，避免体积悄悄回退。
5. **验证以「窗口能应答消息」为硬性检查**：自绘 UI 最容易掩盖「不绘制/不响应」这类失败，
   `verify-portable.ps1` 用 `SendMessageTimeout(WM_NULL)` 断言窗口线程在跑消息循环。

---

## 7. 提交历史（本次会话）

```
1fab3d7 feat(portable): native AOT Win32 shell with a self-drawn UI
64dcfb2 feat(core): source generated JSON with camelCase enums (AOT safe)
d3f8d14 refactor(windows)!: drive the Wi-Fi radio through wlanapi only
79619ea refactor!: remove the WinUI 3 app shell and its XAML-only tooling
```

（后续提交：本记录、README/文档重写、构建与验证脚本、助手 AOT、退出与日志所有权修复。）

---

## 8. 复现命令

```powershell
# 构建 + 测试
dotnet build NetworkGuardian.sln -c Release
dotnet test  tests\NetworkGuardian.Tests\NetworkGuardian.Tests.csproj -c Release

# 打包（含体积门限与可选传输压缩包）
pwsh -NoProfile -File tools\build-portable.ps1 -Zip

# 验证发布包（含提权助手真实执行；-InstanceId 用本机真实设备）
pwsh -NoProfile -File tools\verify-portable.ps1 -InstanceId 'USB\VID_0BDA&PID_8153\001000001'

# 真机冒烟与关闭链路
pwsh -NoProfile -File tools\smoke-run.ps1
pwsh -NoProfile -File tools\shutdown-test.ps1

# 页面截图（隔离配置根，不抢焦点）
pwsh -NoProfile -File tools\ui-screenshot.ps1 -Page dashboard -Out docs\ui-dashboard.png
```

---

## 9. 未验证 / 已知限制

- **未做真实验证**：休眠/唤醒（含 Fast Startup）后的恢复时序、USB 无线网卡物理拔插、
  被禁用网卡的提权启用（需要一块处于「已禁用」状态的物理无线网卡 + UAC 交互）、
  校园网认证接真实客户端端到端。
- 界面为深色主题，未实现浅色/跟随系统主题。
- 只测可达性（TCP/HTTP(S)/DNS/ICMP），不测带宽。
- 无线电写入被驱动拒绝时（部分老驱动不支持 `WlanSetInterface(radio_state)`），
  只能报告失败；此前的 WinRT 路径在个别机器上能改写状态——这是本次改造在能力上的唯一取舍，
  本机 AIC8800D80 / MT7961 上读路径正常。
- 发布包 exe 哈希每次构建会变化（编译期非确定性），未声明可复现构建。

---

## 10. 追加会话（同日）：点击命中链修复 + 真·单文件

后续一次会话报告了「构建产物左侧点击无法切换」，同时要求把提权助手并进主程序、最终只发一个文件。
本节的数字与结论取代第 2、3 节中关于「提权助手是第二个 exe」的描述。

### 10.1 缺陷：自绘 UI 的命中区域从未进入窗口

| 项 | 内容 |
| --- | --- |
| 现象 | 点击左侧导航不切换页面；所有按钮、开关、悬停高亮、手型光标全部无效，但界面渲染完全正常 |
| 根因 | `Canvas.Hit()` 把每个可点击区域登记到 `Canvas.Hits`，而 `MainWindow.PaintInto()` **从未把它复制到窗口自己的 `_hits`**。命中测试因此永远返回 -1 —— 一个渲染正确、交互全死的界面 |
| 为什么以前没发现 | 上文所有验证都在看「页面画得对不对」（截图、日志、关闭链路），没有任何一项断言「点下去会发生什么」 |
| 修复 | `MainWindow.PaintInto()` 在绘制结束后 `_hits.AddRange(canvas.Hits)`（提交 `9ff7064`） |
| 证据 | 发布产物上依次点击导航 → 命中索引 3/4/5，页面切到无线网卡 / 以太网 / 设置；设置页滚动到底后点击「保留天数 +」，命中 181 且数值 7 → 8 |

### 10.2 缺陷：滚动后的命中区域与内容错位

| 项 | 内容 |
| --- | --- |
| 现象 | 页面滚动后，控件要在「原来的位置」才点得到（滚动越远偏得越多） |
| 根因 | 滚动用 `SetViewportOrgEx` 平移绘制坐标，但 `Canvas.Hit()` 登记的是**布局坐标**，鼠标消息带的是**客户区坐标**；两者相差正好一个滚动偏移。同理，内联 `EDIT` 子窗口与下拉菜单也会出现在错误位置 |
| 修复 | `Canvas` 跟踪视口偏移（`PushOffset`/`PopOffset`），`Hit()`、`ToClient()`、`IsHovered()` 统一换算到客户区坐标；`Widgets.Field` / `Widgets.Dropdown` 用 `ToClient()` 定位原生子控件与弹出菜单（提交 `84a7477`） |

### 10.3 可观测性：自绘控件的失败必须是可追溯的

自绘 UI 没有控件树，点击落空与点击空区域在外部完全无法区分。因此 `WM_LBUTTONDOWN/UP` 现在以
debug 级别记录鼠标坐标、命中索引、按下/抬起是否一致、当前目标数与滚动量（`GuardianHostService.LogUiDebug`）。
本次两个缺陷都是靠这条日志定位的（日志显示「hit -1，targets 189，scroll 7632」→ 先发现命中链断开，
再发现滚动偏移）。`verify-portable.ps1` 用 debug 级别运行主程序，这条日志也可用于后续回归。

### 10.4 QA 工具的坑：模拟输入的坐标会被 DPI 换算

`tools/qa-ui-click.ps1`（`PrintWindow` 截图 + 模拟点击）第一次运行时，发送的 (674,141) 在窗口里变成 (843,177)：
脚本线程与目标窗口的 DPI 上下文不同，跨进程投递鼠标消息时 Windows 按 `windowDpi/96` 换算坐标。
脚本因此先做一次标定点击（发送 (4,4)，读窗口日志里的实际到达值），后续坐标按测得的因子反算，
使送达坐标与请求坐标一致。`PrintWindow(hwnd, hdc, PW_CLIENTONLY=1)` 是可靠的抓图方式；
自己直接发 `WM_PRINTCLIENT` 到临时 DC 会得到全黑位图（窗口对 `WM_PRINTCLIENT` 的处理没问题，是调用方式的差异）。

### 10.5 提权助手并入主程序：真正的一个 exe

| 项 | 改前 | 改后 |
| --- | --- | --- |
| 包内文件 | `NetworkGuardian.exe` 7.80 MB + `helper\NetworkGuardian.Helper.exe` 2.80 MB + `发布说明.txt` | `NetworkGuardian.exe` **7.82 MB** + `发布说明.txt` |
| 包合计 / zip | 10.61 MB / 5.15 MB | **7.82 MB / 3.79 MB** |
| 提权方式 | 启动第二个 exe（`requireAdministrator`）+ 请求/响应 JSON | 同一个 exe 以 `--helper` 提权启动（`asInvoker` + `runas`），完成一个操作即退出 |

- 实现放在 `NetworkGuardian.Windows/Helper/HelperEntry.cs`，`--helper` 与独立 `NetworkGuardian.Helper.exe` 共用同一份代码
  （独立 exe 保留，作为分离部署选项，但不再进发布包）。
- **`--helper` 必须在单实例互斥体之前处理**：用户实例正在运行时，提权进程若走到互斥体判断就会以
  「已有实例」退出，UAC 那侧永远等不到响应文件。`verify-portable.ps1` 专门在 GUI 实例运行期间调用
  `--helper` 来锁住这条约束。
- `HelperClient.ResolveInvocation()` 的顺序：`NETWORKGUARDIAN_HELPER_PATH`（测试）→ 自身 exe + `--helper` →
  目录中的独立 `NetworkGuardian.Helper.exe`。
- `build-portable.ps1` 现在断言**包内只有一个 exe**；体积门限仍为 8 MB。

### 10.6 顺带修掉的既有测试缺陷

`LocationPermissionServiceTests.ConsentDeniedWithoutADeniedApi_...` 经 `service.Read()` 断言，
而 `Read()` 会把本机真实注册表 `HKCU\...\ConsentStore\location\Value` 合并进 `AppLocationAllowed`。
本机位置权限为 `Allow` 时该测试必然失败 —— 在干净 HEAD 的独立 worktree 上复现确认与本次改动无关，
已改为直接把快照喂给纯函数 `BuildGuidance`（提交 `2cd8fd8`）。

### 10.7 本轮验证证据

| 检查 | 结果 |
| --- | --- |
| `dotnet build NetworkGuardian.sln -c Release` | 0 警告 / 0 错误 |
| `dotnet test` | **196 / 196 通过** |
| `tools\build-portable.ps1 -Zip` | 单文件 7.82 MB，门限 8 MB 通过，zip 3.79 MB 且解压哈希一致 |
| `tools\verify-portable.ps1 -InstanceId 'USB\VID_0BDA&PID_8153\001000001'` | **35 / 35 通过**（含「包内只有一个 exe」「GUI 实例运行时 `--helper` 仍应答」「真实设备 query-status = Succeeded」） |
| 发布产物点击回归（`tools\qa-ui-click.ps1`） | 导航切换、滚动后按钮点击均生效（见 10.1 / 10.2 证据） |

仍未验证：真实 UAC 弹窗交互（自动化会弹窗，需人工点一次）、提权启用「已禁用」无线网卡的端到端。

---

## 11. 追加会话（同日）：网卡自动恢复的真实行为 + 一个 SSID 一张网卡

用户报告两件事：① 在 Windows 网络设置里禁用的无线网卡没有被自动打开；② 两张无线网卡不应连接同一个 Wi-Fi，
若已重复连接应断开信号较差的那张。

### 11.1 先诊断：「被关闭」和「启动失败」是两回事

真机（本机 3 张物理无线网卡）：

| 网卡 | 真实状态 | 旧版程序的行为 |
| --- | --- | --- |
| Intel Wi-Fi 6 AX201（PCI） | `Status=Error`、`CM_PROB_FAILED_START`(**10**)，`ConfigFlags=0`（**未被禁用**） | 只写一条 UI note，日志里**完全没有痕迹** |
| AIC8800D80（USB） | 正常，连接 HXXY-WiFi | 正常保活 |
| MediaTek MT7961（USB） | 正常但驱动报 `powered down`，连不上 AP | 正常（无动作） |

- 旧设计只对 problem code **22/21（真正被禁用）** 发 `CM_Enable_DevNode`；10/43 这类"驱动没起来"被判定为
  "不是禁用，启用也没用"，只记一条注释。这条注释既不进日志，也不给出下一步，所以用户看到的就是"什么也没发生"。
- 实测（提权助手，手工请求 `restart`）：对 AX201 做**禁用+启用**后仍是 `CM_PROB_FAILED_START`，
  说明它是驱动层故障，不是"开关没打开"。
- **顺带发现假成功**：`CM_Enable_DevNode` 返回 `CR_SUCCESS` 时旧代码一律报 `Succeeded`，
  即使设备根本没启动（本机实测就复现了 `success=true, startedAfter=false, problemCodeAfter=10`）。

### 11.2 改动

| 项 | 内容 |
| --- | --- |
| 故障设备重启 | 新增 `RestartWifiDeviceAction`：对"存在但未运行"的物理无线网卡执行禁用+启用。每设备限流（≤3 次/小时、间隔 ≥120 秒、连续 2 次失败即停），动作后插入稳定等待 |
| 新开关 | `general.autoRestartFaultedWifiDevices`（默认开）+ 设置页开关「故障无线网卡自动重启」 |
| 如实报告 | `DeviceNodeOperations` 在 `CR_SUCCESS` 后再轮询最多 4 秒等 `DN_STARTED`；仍未启动则报 `Failed`，并写明"需要重装/回滚驱动，禁用/启用无法修复驱动故障" |
| 一个 SSID 一张网卡 | `wifi.allowSameSsidOnMultipleAdapters` 默认改为 **false**，并新增 v4 迁移（`CurrentVersion=4`）把已有配置一并关掉 |
| 冲突断开 | 决策引擎检测同一 SSID 被多张网卡连接，保留信号最强的一张（信号相同则按描述稳定取舍），对最弱的一张发 `DisconnectWifiAction`（限流 12 次/小时、间隔 ≥60 秒） |
| 不许立刻重连 | `CandidateSelector` 的 `excludeSsid`（单值）扩展为 `excludeSsids`（集合）：被别人占用的 SSID 不再是候选，被断开的网卡会改用其它已保存网络，没有别的网络就保持空闲 |
| 可观测性 | `GuardianHostService` 把决策 notes 写进日志（按"去掉数字后的文本"归类，同类每分钟最多一条），并在 `WM_*` 之外新增"重启设备"执行分支的信息/警告日志 |

### 11.3 真机验证（发布产物 7.85 MB，`release\NetworkGuardian.exe`）

**① 被禁用的网卡能自动启用**（直接验证用户投诉点）：手工 `Disable-PnpDevice` 禁用 MediaTek 网卡
（`Status=Error, CM_PROB_DISABLED`），启动程序后 3 秒内：

```
09:31:03.830 Process is elevated; enabling USB\VID_0E8D&PID_7961\000000000 directly
09:31:06.138 CM_Enable_DevNode(USB\VID_0E8D&PID_7961\000000000) returned CR_SUCCESS
09:31:06.138 Enabled physical Wi-Fi device USB\VID_0E8D&PID_7961\000000000: Device started.
```

**② 故障网卡尝试重启并如实报失败**（AX201）：

```
10:00:43.605 Restarting physical Wi-Fi device PCI\VEN_8086&DEV_7A70… (Intel(R) Wi-Fi 6 AX201 160MHz);
             problem code 10 means the driver did not start, so enabling alone would not help
10:00:49.400 Restarting … did not bring it back: Failed … the device did not start within 4.0s
             (problem code 10). … reinstall or roll back the adapter driver in Device Manager.
```

之后同一设备被限流（`minimum interval 120s not elapsed`），证明不会每轮反复动硬件。

**③ 日志刷屏修复**：第一版 notes 日志按整行去重，但限流器的原因里带动态计数（"last run 22s ago"），
4 分钟产生 266 条；改为"去掉数字后归类 + 同类每分钟至多一条"后，75 秒只有 6 条。

**④ 配置迁移**：用户既有 `config.json`（version 3）启动后被迁移为 version 4，
`allowSameSsidOnMultipleAdapters=false`、`autoRestartFaultedWifiDevices=true`。

### 11.4 仍未在真机验证

- **两张网卡连同一个 SSID 的断开行为**：本机只有 AIC8800D80 能连上该 AP（MediaTek 驱动报 `powered down`，
  AX201 驱动启动失败），无法构造真实重复连接场景。该分支由 5 个单元测试锁定
  （保留强者/断开弱者、信号相同时的稳定取舍、允许共享时不动作、不同 SSID 不干扰、被断开的网卡不会立刻重连回去）。
  需要两台都能连上同一 AP 的网卡（或在别的机器上）做一次端到端确认。
- **普通权限下的 UAC 交互**：本机会话是已提权的，`--helper` 直接执行、没有弹窗。真实用户环境下
  每次提权动作都需要在 UAC 上点一次「是」——**这就是"没有自动打开"最可能的第二种原因**：
  如果当时 UAC 弹窗未被确认（或程序未运行），网卡不会恢复。README 与发布说明已明确写出这一点，
  并说明"要无人值守生效需要以管理员身份常驻（任务计划程序），程序不会自行配置"。

---

## 12. 追加会话（同日）：真正没打开的是「每块网卡的 Wi-Fi 分开关」

用户进一步澄清：**不是驱动问题，也不是网络共享中心的适配器设置**，而是
Windows 设置里 WLAN 页面中**每块无线网卡各自的开关**（例如「WLAN」「WLAN 3」）没有打开。

### 12.1 定位：三种"开关"是三个不同的层

| 层 | 现象 | 本机实测 |
| --- | --- | --- |
| 适配器启用/禁用（PnP） | 设备管理器里的「启用设备」，problem code 22 | 第 11 节已验证可自动启用 |
| 驱动启动失败 | AX201 `CM_PROB_FAILED_START` | 禁用/启用无效，需要重装驱动 |
| **设置页的每块网卡 Wi-Fi 开关** | `netsh` 显示 `Radio status: Software Off` | **本节要修的**：程序从未打开过它 |

`netsh wlan show interfaces` 在 MediaTek 网卡上一直显示 `Software Off`（此前被误判为"驱动 powered down"），
正是用户在设置里看到的关闭状态。Windows 设置的分开关对应 **WinRT `Windows.Devices.Radios` 的
每网卡一个 Radio 对象**（名字与网卡一致：`WLAN`、`WLAN 3`），也就是 `radio_state`。

### 12.2 三条写入路径的实际结论（都做了实验）

1. **`wlanapi` `wlan_intf_opcode_radio_state`（旧实现）**：读只读**第一个** WLAN 接口
   （`NativeRadioAccess` 里的注释就写着 "use the first available interface"），所以被关掉的第二块网卡
   完全不可见；写则**两块 USB 网卡都返回 87（ERROR_INVALID_PARAMETER）**——用 driver 自己返回的
   `WLAN_RADIO_STATE` 做读-改-写、接口 connected 或 disconnected、尺寸 76/772 都试过，全部 87。
   即：这条路径在本机只能读、不能写。
2. **手写 WinRT ABI（`Windows.Devices.Radios`）**：激活与枚举都能成功
   （`RoGetActivationFactory` → `IRadioStatics.GetRadiosAsync` 返回的对象类名确实是
   `Windows.Devices.Radios.Radio`），但**返回的 `IAsyncOperation` 指针的 vtable 不含继承的 `IAsyncInfo`
   成员**：直接调用 slot 6 崩溃，必须 `QueryInterface(IAsyncInfo)`（`00000036-…`）后才能
   `get_Id/get_Status/get_ErrorCode`；而取结果所需的 `IAsyncOperation` 视图是**参数化接口**（PIID 非固定），
   `GetResults` 拿不到。Native AOT 又不能用 CsWinRT。**结论：这条路放弃**，相关代码已删除。
3. **Windows 无线电管理器（Win32 COM，`um/RadioMgr.h`）**：`IMediaRadioManager` →
   `IRadioInstanceCollection` → `IRadioInstance`，**全部同步**，且 `GetInstanceSignature` 直接返回
   **WLAN 接口 GUID**（与 WLAN API 同一标识，不需要按名字匹配），`SetRadioState(DRS_RADIO_ON, 5)` 成功。

coclass 在 SDK 头里没有声明，是在注册表里按类描述找到的：
`HKLM\SOFTWARE\Classes\CLSID` → `{833A69FB-5E17-4893-85A5-1EF469217372}` = "Wlan Radio Manager"
（"Radio Management API" `{581333F6-…}` 返回 `E_NOINTERFACE`，不是它）。

### 12.3 改动

| 项 | 内容 |
| --- | --- |
| 新 interop | `src/NetworkGuardian.Windows/Radio/RadioManagerInterop.cs`：`IMediaRadioManager`/`IRadioInstanceCollection`/`IRadioInstance` 的 vtable 调用（`IRadioInstance` = IUnknown + GetRadioManagerSignature/GetInstanceSignature/GetFriendlyName/GetRadioState/**SetRadioState**/IsMultiComm/IsAssociatingDevice） |
| 逐块读取 | `IRadioStateAccess.ReadRadioInstances()`：得到每块网卡的开关状态；`RadioInstanceInfo` 把 `DEVICE_RADIO_STATE`（0 开、1 软件关、2 硬件关、3 两者）映射成 `IsOn/IsSoftwareOff/IsHardwareOff` |
| 逐块打开 | `IRadioStateAccess.SetInstanceRadioOn(Guid)`：只对指定接口写入，写后回读校验；硬件开关关闭时返回"软件无法打开"而不是反复重试 |
| 汇总与自动开启 | `WifiRadioController`：快照改为**所有网卡汇总**（任一关闭即为 Off，这才看得见被关掉的那块）；"打开 Wi-Fi"改为**逐块打开**，部分成功如实报告（"已打开 N 块，但仍有失败：…"） |
| COM 公寓 | 在 MTA 线程上执行（UI 线程是 STA，`CoInitializeEx` 不能改公寓）；`CoUninitialize` 只在 `CoInitializeEx` 返回 `S_OK` 时配对调用——`S_FALSE` 也配对会导致公寓计数下溢，实测表现为下一次调用访问非法内存 |
| 测试 | 4 个真机互操作测试（读取报告、已开时为幂等 no-op、未知接口失败、硬件开关如实报告）+ 3 个控制器测试（逐块打开且只碰关闭的那块、部分失败如实报告、某块关闭时汇总状态为 Off）。共 210/210 |

### 12.4 真机验证（发布产物，7.87 MB）

用 QA 脚本按 Windows 设置的方式关掉「WLAN」的开关（`Settings → Wi-Fi` 等价操作）：

    powershell -File tools\qa-winrt-radio.ps1 -Name 'WLAN' -State Off
    netsh wlan show interfaces   →  Radio status: Hardware On / Software Off

启动发布产物后 44 毫秒内：

    12:22:35.040 [INF] GuardianHostService: Wi-Fi radio is off at startup; requesting it to be turned on
    12:22:35.084 [INF] WifiRadioController: Turned the software radio of 1 Wi-Fi adapter(s) back on

验证结果：`netsh` 变为 **`Radio status: Hardware On / Software On`** —— 用户在设置里能看到那块网卡的开关被打开了。
**这一项不需要管理员权限、不弹 UAC**（radio 是用户级设置），与第 11 节的 PnP 提权路径互补。

### 12.5 仍未在真机验证

- 硬件开关关闭（`DRS_HW_RADIO_OFF`）的情形：本机两块网卡都是"硬件开、软件关"，只能由单元测试覆盖
  报错路径（`HardwareOffRadios_AreReportedAsUnchangeableBySoftware`）。
- 不同 Windows 版本上 "Wlan Radio Manager" 的 CLSID：目前是硬编码已验证值；若某版本不同，会走
  `ReadRadioInstances()` 为空 → `wlanapi` 兜底路径，并在日志里写明原因。
