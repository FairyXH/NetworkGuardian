# WFP 出口切换阻断

## 实现约束

- 仅调用 Windows 自带 `Fwpuclnt.dll` 用户态管理接口，不安装驱动。
- 与重启旧网卡互斥；保留现有仅清理 IPv4 TCP 的兼容选项，默认不启用新行为。
- 新默认出口生效后，仅阻断旧物理网卡地址上的 TCP/UDP，覆盖 IPv4/IPv6。
- 使用动态会话，禁止持久或开机规则；部分创建失败时回滚，退出时释放。
- 提权助手监视主进程生存、取消信号、出口恢复及最长租期；不能依赖 UI 正常退出才能清理。
- 默认阻断 5 秒；可观测到原 TCP 连接或明确绑定旧地址的原 UDP 端点仍存在时延长，最长 60 秒。
- UDP 端点存在只表示 socket 尚未关闭，不能证明仍在通信；通配地址绑定也不能可靠归属旧网卡。到期释放并记录观察限制，不无限阻断。
- 测试只对测试进程的专用端口下发规则，验证 TCP/UDP、IPv4/IPv6、正常释放和进程终止清理，不阻断真实业务。

## 官方依据

- [用户态过滤接口](https://learn.microsoft.com/en-us/windows/win32/api/fwpmu/nf-fwpmu-fwpmfilteradd0)
- [动态会话及对象生命周期](https://learn.microsoft.com/en-us/windows/win32/fwp/object-management)
- [各层可用过滤条件](https://learn.microsoft.com/en-us/windows/win32/fwp/filtering-conditions-available-at-each-filtering-layer)
- [UDP 端点枚举](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedudptable)

动态规则随所属进程会话结束清除，且不持久化到下次开机；强制关机不会使这批规则在重启后继续阻断。运行时仍应检查当前租期、主进程和出口状态，及时释放无必要规则。
