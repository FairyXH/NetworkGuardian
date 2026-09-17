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
| 最终 | `portable\`（exe + helper + 发布说明） | **10.61 MB**（exe 7.80 MB） | 单文件、无 PDB、无 loose DLL/JSON |
| 最终 | `NetworkGuardian-0.9.0-win-x64.zip` | 5.15 MB | 压缩后传输包（解压后 exe 哈希与 `portable\` 一致） |

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
