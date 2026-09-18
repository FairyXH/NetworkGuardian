# 自维护无线网络库（802.1X / EAP）

本文记录「程序自己维护一张无线网络账号表、并在连接企业级 Wi-Fi 时使用它」这一功能的实现依据、
真机判定结果与已验证边界。配套代码：`src/NetworkGuardian.Core/Wlan`、`src/NetworkGuardian.Infrastructure/Configuration/WifiNetworkVault.cs`、
`src/NetworkGuardian.Windows/Wlan/WifiProfileApplier.cs`、`src/NetworkGuardian.Portable/Ui/Pages/CredentialsPage.cs`。

## 1. 需求 → 实现 → 证据

| 需求 | 实现 | 证据 |
| --- | --- | --- |
| 支持 EAP 网络，账号密码由程序自己维护 | `WifiNetworkCredential` + `WifiNetworkVault`（`wifi-networks.json`）+ 「网络凭据库」页面 | `WifiNetworkVaultTests`（磁盘上无明文、DPAPI 往返、损坏恢复）；真机 `WifiEapConnectHardwareTests` |
| 独立于系统的无线网络库 | 库里保存权威账号；系统侧只是「生成物」——连接前由 `WifiProfileApplier` 写入，库中没有的 SSID 绝不去猜 | `EapConnectFlowTests`（库中无账号 → 拒绝连接并说明原因）、§3 的复用/重写规则 |
| 启用无线功能后保证所有网卡软开关打开 | 「无线电看门狗」独立循环，默认每 3 秒读全部网卡的软开关并立即打开 | 真机 `tools/qa-radio-watchdog.ps1`：2.29 s / 2.30 s，日志有看门狗行 |
| 在已保存网络中继续连接处理 | 沿用原有的「只连接已保存配置」流程，EAP 候选在库中有账号时照常参与择优 | `CandidateSelectorTests`、`EapConnectFlowTests` |
| 遇到 EAP 认证就用库中的信息 | 连接前按库中参数写配置 + 写 EAP 用户凭据，Windows 不再提示 | 真机写入 `success=True profileWritten=True userDataWritten=True` |
| 失败重试 5 次 | `EapConnectRetryPolicy`，上限 `wifi.eapConnectMaxAttempts`（默认 5），按「网卡 + SSID」计数 | `EapConnectFlowTests`；真机 5 次真实失败后 `Failures=5` |
| 5 次失败后对该网卡临时放弃（下次启动继续尝试） | 计数只在内存；达到上限后该网卡上该 SSID 不再被选中，日志与界面写明「本次运行已临时放弃」 | `EapConnectFlowTests`；库中修改该网络会立刻清零（真机 `ClearEapRetries` 返回 1） |

## 2. 数据与存储

`wifi-networks.json`（`%LOCALAPPDATA%\NetworkGuardian\`，可用 `NETWORKGUARDIAN_CONFIG_ROOT` 重定向）：
原子写入 + `.bak` + 解析失败时隔离为 `.invalid.json`（与 `config.json` 同一套存储语义）。

| 字段 | 含义 |
| --- | --- |
| `ssid` / `profileName` | 目标网络；`profileName` 留空表示用 SSID 作系统配置名（多个适配器用同一份配置是正常的） |
| `auth` | `Wpa2Enterprise`(AES) / `WpaEnterprise`(TKIP) / `Wpa3Enterprise`（见 §3 的实测结论） |
| `eap` | `PeapMschapv2` / `Tls` / `CustomXml` |
| `identity` / `anonymousIdentity` / `domain` | MSCHAPv2 身份（`domain\user` 或 `user@realm`）；PEAP 外层可匿名 |
| `passwordProtected` | DPAPI 密文（Base64）。**`password` 字段带 `[JsonIgnore]`，永不序列化** |
| `serverNames` / `trustedRootCaThumbprints` | 服务器名校验与根证书固定（SHA-1 40 位十六进制，空格/分号都可） |
| `certificateThumbprint` | EAP-TLS 客户端证书固定（写进 EAP 用户凭据的 `UserCert`） |
| `profileXmlOverride` | 生成器覆盖不到的场景（EAP-TTLS、厂商方案、证书选择、MAC 随机化）原样写入 |
| `appliedFingerprint` / `lastAppliedUtc` | 记录「这份配置是我们按当前条目写进去的」，用于判断是否需要重写 |

`WifiNetworkVault` 有三条硬规则（单元测试锁定）：

1. **没有保护器就拒绝保存密码**（`InvalidOperationException`），绝不明文落盘；
2. `Entries` / `Find` 返回**副本**：界面与宿主改的都是副本，保存时按字段签名比对，才能发现「密码被改了」（早期版本返回活对象，改动在比较前就已生效，导致改密码后不重写配置——`WifiNetworkVaultTests` 专门覆盖）；
3. 任何字段（含密码）变化都会清掉 `appliedFingerprint` → 下次连接重写配置与凭据；只有 `appliedFingerprint`/`lastAppliedUtc` 变化不清除。

## 3. 生成的两份文档，以及真机接受/拒绝矩阵

Windows 把 802.1X 拆成两份文档，凭据**不在**配置 XML 里：

1. **连接配置**（`WlanSetProfile`，逐用户配置）：`<name>`/`<SSIDConfig>`/`<connectionType>` + MSM（`authentication`、`encryption`、`useOneX=true`）+ `<OneX><authMode>user</authMode><EAPConfig>…`；
2. **EAP 用户凭据**（`WlanSetProfileEapXmlUserData`）：`EapHostUserCredentials` → `EapMethod`（EAP 类型 25/13）+ `Credentials` → `Eap`（`BaseEapUserPropertiesV1`，先 `<Type>`）→ 具体方法的 `EapType`（`MsChapV2UserPropertiesV1` 的 `Username`/`Password`/`LogonDomain`，或 `EapTlsUserPropertiesV1` 的 `UserCert`）。

判定依据不是猜的，而是本机资源：**`C:\Windows\schemas\EAPHost\*.xsd` 与 `C:\Windows\schemas\EAPMethods\*.xsd`**，
以及把本机已存在且能工作的企业级配置原样导出来对照（`tools/dump-wlan-profile.ps1`）。

| 形态 | 结果 |
| --- | --- |
| PEAP + MSCHAPv2，WPA2-Enterprise（AES） | **接受**（`WlanSetProfile` + EAP 用户凭据均成功，读回 `WPA2` / EAP 类型 25） |
| PEAP + MSCHAPv2，WPA-Enterprise（TKIP） | **接受** |
| 隐藏网络（`nonBroadcast=true`，手动连接） | **接受** |
| EAP-TLS（`CredentialsSource` + `ServerValidation`） | **接受**；用户凭据（`UserCert` 固定证书）**接受** |
| 凭据写进配置 XML（`UserName`/`Password` 或 `EAPConfig` 内的用户字段） | **拒绝**：`WlanSetProfile` 原因码 524289（schema 不合法）——凭据必须走 EAP 用户数据 |
| WPA3-Enterprise：`WPA3`/`WPA3ENT`/`WPA3ENT192` × `AES`/`GCMP256`/`GCMP`，三种网卡（AIC8800D80 / MT7961 / Intel AX201） | **全部拒绝**：原因码 1206。因此生成器**拒绝**自动生成 WPA3-Enterprise，明确要求填自定义 XML（不猜测、不静默产出无效配置） |
| EAP 用户数据里 `<Type>` 放在 `EapMethod` 之前 / 命名空间用前缀 / `Credentials` 里的 `Eap` 不带命名空间 | **拒绝**：`EAP_E_EAPHOST_XML_MALFORMED`（0x80420019） |
| EAP-TLS 把 `ServerValidation` 放在 `CredentialsSource` 之前 | **拒绝**：原因码 1206（XSD 要求 `CredentialsSource` 在前） |

## 4. 什么时候会写系统配置

`WifiProfileInspector.Evaluate` 返回四种判定：`Missing`（没有该配置）/ `NotEnterprise`（有但不是 802.1X）/
`LibraryChanged`（`appliedFingerprint` 缺失或与当前条目生成的指纹不同）/ `UpToDate`。
只有 `UpToDate` 才跳过写入，因此：手动改过系统配置、或换了一张网卡，都会按库中参数重写一次。
指纹是自写的 128 位 FNV-1a（带 `ng1-` 前缀），只用于「是不是同一份文档」这一判断——不需要密码学强度。
注意：实测把它从 `SHA256.HashData` 换过来**并没有减小体积**（前后都是 8.24 MB），保留它的理由是不给
变更检测引入密码学依赖，而不是省体积。

## 5. 连接与重试

1. 决策引擎每轮拿到 `WifiEapCatalog`（库里启用的 SSID 集合）；
2. 企业级网络若不在库中 → 不产生连接动作，日志/界面写明原因（`[802.1X]` 前缀的拒绝理由**不受** `verboseNetwork` 开关限制，因为它就是要给人看的）；若该「网卡 + SSID」已被放弃 → 同样不出现在候选里；
3. 连接前 `EnsureLibraryProfileAsync`：查库 → 写配置 + 写凭据 → 记录 `appliedFingerprint`；
4. `WlanConnect` 失败时按 `requiredEap` 记入 `EapConnectRetryPolicy`；达到上限（5）后标记放弃并写一条恢复动作记录（日志/界面可见）；
5. 放弃只影响本次运行；库中修改该网络、或用户在托盘点「清除失败记录与冷却」都会清零。

失败既包含「连接尝试本身失败」，也包含「配置/凭据准备失败」（此时不会真的发起连接，但同样计数，避免每轮空转重写）。

## 6. 软开关看门狗

- 每块网卡的软开关通过 **Windows 无线电管理器**读取与写入；`wlanapi` 的 `radio_state` 在本机三块网卡上写操作都返回 87，只能读；
- 看门狗是**独立循环**（默认 3 秒），不依赖监测循环的巡检周期；
- 它**不受**决策引擎的无线电动作限流（`12 次/小时`、最小间隔 `30 秒`）约束——真机验证时第 1 轮 0.26 s 由「无线电状态变化通知 + 引擎动作」完成，第 2、3 轮（限流已生效）仍能在 2.3 s 内恢复，正是看门狗的贡献；
- 硬件开关关闭、或系统策略禁止写入时如实报告失败，不反复重试；
- 用户「暂停自动恢复」期间看门狗不干预网卡（与其他自动动作保持一致），日志在 debug 级别说明。

## 7. 真机证据

环境：Windows 11 22631，三块 USB/PCIe 无线网卡；企业级网络 `HXXY-WiFi`（WPA2-Enterprise，PEAP/MSCHAPv2）。

```
# 1. 按库中参数写入配置与账号（xUnit 真机用例）
$env:NETWORKGUARDIAN_WIFI_HARDWARE_TESTS=1
dotnet test tests\NetworkGuardian.Tests\NetworkGuardian.Tests.csproj -c Debug `
  --filter 'FullyQualifiedName~WifiProfileHardwareTests' --logger 'console;verbosity=detailed'
#   → peap-mschapv2 / peap-mschapv2-wpa-tkip / peap-hidden-manual / eap-tls / eap-tls-pinned-cert
#     全部 apply success=True，第二次 apply「无需更新」，改条目后判定为 LibraryChanged，测试配置已删除

# 2. 真实 802.1X 失败计数（错误密码，连接真实 AP）
dotnet test ... --filter 'FullyQualifiedName~WifiEapConnectHardwareTests'
#   → apply success=True profileWritten=True userDataWritten=True
#   → attempt 1..5 全部 success=False（每次 18 s 超时）
#   → after 5 failures: abandoned=True
#   → cleanup: deleteSuccess=True removed=True；user profile intact: True
#   → 磁盘上 grep 不到密码明文

# 3. 软开关看门狗
pwsh -NoProfile -File tools\qa-radio-watchdog.ps1 -RadioIndex 2 -Rounds 3
#   → round 1 restored after 0.26s（通知路径）
#   → round 2 restored after 2.29s，日志：无线电看门狗：已把软件关闭的无线网卡重新打开
#   → round 3 restored after 2.30s，同上
```

真机用例做过三件防误伤的事：生成的配置用独立名字（`NG-SELFTEST-HXXY`）且不自动连接、账号用假身份
（`selftest@example.invalid`，不会锁真实校园账号）、结束前删除配置并断言用户自己的同名配置仍在。

## 8. 体积影响（Native AOT，同一工具链实测）

| 提交 | 内容 | exe |
| --- | --- | --- |
| `289ca18` | 本功能之前 | 7.87 MB |
| `a8e6ce0` | 库模型 / 配置 / 配置 XML 与 EAP 用户数据生成器（未接线） | 7.98 MB |
| `06cc66d` | Windows 写入层 + 库存储 + DPAPI（仍未接线，被裁剪） | 7.98 MB |
| `d296834` | 接线：库装载、连接前写配置、失败计数、网络凭据库页面 | 8.24 MB |

把「网络凭据库」页面从页面列表里去掉再发布，只省 0.03 MB——**增长来自功能代码本身，不是界面**。
因此 `tools/build-portable.ps1` 的体积门限由 8 MB 调整为 **8.5 MB**（理由与逐步实测写在脚本头部）。
`OptimizationPreference=Size`、`StripSymbols`、`UseSystemResourceKeys`、`InvariantGlobalization` 早已开启。

## 9. 开发中暴露的问题与根因

0. **用户配置会异步扩散到所有网卡，单卡删除会留下真实残留。** 真机测量
   （`WifiProfileVisibilityExploration`）：刚写入时只有目标网卡的 `WlanGetProfile` 与
   `netsh ... interface=` 能看到；数秒后另外两张网卡也会看到（每用户配置存储是机器级的，各接口视图异步跟进，
   真机上 90 秒的连接测试结束时三张网卡都有）。另有坑：不带 `interface=` 的 `netsh wlan show profiles`
   会按网卡把同一份用户配置列表重复打印，看起来像三份。结论：**清理必须逐网卡删除并读回确认**
   （`WifiProfileApplier.RemoveEverywhere`，界面「从系统删除配置」按钮）；
   硬件测试也必须串行执行——并行跑两个硬件测试时 `WlanSetProfile(overwrite: true)` 会返回
   `ERROR_183`（文件已存在），共享 `wifi-hardware` 集合后恢复正常。

1. **密码不能写在配置 XML 里**：凭据放 `EAPConfig` 会被 `WlanSetProfile` 以原因码 524289 拒绝；必须用
   `WlanSetProfileEapXmlUserData` 单独写。
1b. **`WlanSetProfile(overwrite: true)` 仍可能返回 `ERROR_183`（文件已存在）**：当同名配置正被网卡使用
   （例如上一轮运行遗留、网卡还在用它认证）时就会这样。此时先 `WlanDeleteProfile` 再写入（`overwrite: false`）
   才能成功——否则一份旧配置会永久挡住新账号。真机上就是靠这个 fallback 才让测试从 ERROR_183 恢复。
2. **EAP 用户数据 XML 无文档可循**：6 种手写形态全被 `0x80420019` 拒。正解是系统自带的 XSD
   （`C:\Windows\schemas\…`）：`EapMethod` 的类型元素在 `EapCommon` 命名空间、`Credentials` 里的 `Eap`
   在 `BaseEapUserPropertiesV1` 命名空间且**先 `<Type>` 再具体方法元素**、`Username` 大小写敏感。
3. **EAP-TLS 元素顺序**：`CredentialsSource` 必须在 `ServerValidation` 之前，否则原因码 1206。
4. **WPA3-Enterprise 在本机不可用**：三种取值 × 三种网卡全部 1206；不猜、不硬编码，改为要求自定义 XML。
5. **`WlanSetInterface(radio_state)` 写不了**：三块网卡都返回 87，写入只能走无线电管理器（前一轮已验证）。
6. **库返回活对象导致「改密码不重写」**：`SaveAsync` 比较签名时改动已生效；改为返回副本。
7. **引擎的无线电限流让「通知路径」不可靠**：`12 次/小时` + `30 秒`最小间隔，短时间内第二次关掉软开关时
   引擎不会再动手——这正是需要独立看门狗循环的原因（真机第 2/3 轮验证）。
8. **占位性假设被证伪**：早期版本假设能用 `WlanGetProfileEapUserDataInfo` + `WLAN_PROFILE_USER`
   读回已存凭据大小，对照 `wlanapi.h` 后确认假设错误，相关代码已删除（不保留「看起来能工作」的死代码）。

## 10. 未验证边界

- WPA3-Enterprise（本机 WLAN 服务拒绝；需支持该模式的网卡/驱动）；
- EAP-TTLS、PEAP-TLS 等第三方 EAP 方法（需系统安装对应 EAP 方法，只能填自定义 XML）；
- EAP-TLS 携带**真实客户端证书**完成认证（本机无可用证书，只验证了配置与凭据被接受）；
- `wifi-networks.json` 跨用户/跨机器复制（DPAPI 作用域所限，程序会提示重新填写，不静默失败）；
- 多张网卡同时连同一个企业级 SSID（`wifi.allowSameSsidOnMultipleAdapters=false` 的默认行为沿用原逻辑）。
