using System.Text.Json.Nodes;

namespace NetworkGuardian.Infrastructure.Mcp;

public sealed record McpTool(string Name, string Description, bool ReadOnly, string Properties, params string[] Required)
{
    public JsonObject Descriptor() => new()
    {
        ["name"] = Name, ["description"] = Description,
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = JsonNode.Parse(Properties),
            ["required"] = new JsonArray(Required.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()), ["additionalProperties"] = false },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = ReadOnly, ["destructiveHint"] = !ReadOnly, ["openWorldHint"] = false },
    };
}

public static class McpTools
{
    public static IReadOnlyList<McpTool> All { get; } = new McpTool[]
    {
        new("guardian_status", "运行概览、各接口探测、路由、待执行任务及服务器状态。", true, "{}"),
        new("quality_report", "自然语言质量报告与实测数据；不会将模型效用当作成功概率。", true, "{}"),
        new("devices_list", "所有网络设备的 PnP 标识、类型、接口 GUID、启用状态及保持禁用规则。", true, "{}"),
        new("device_set_state", "启用、保持禁用或取消保持禁用；支持物理无线和有线设备，可能请求 UAC。", false,
            """{"deviceId":{"type":"string"},"state":{"type":"string","enum":["enabled","kept_disabled","release"]}}""", "deviceId", "state"),
        new("wifi_list", "无线接口、当前连接、已保存配置及最近扫描；先查询以取得接口 GUID。", true, "{}"),
        new("wifi_scan", "扫描指定无线接口；省略 adapter 则扫描全部，返回完成后的扫描结果。", true, """{"adapter":{"type":"string"}}"""),
        new("wifi_connect", "连接已保存的 profile；遵守 SSID 指定和保持禁用规则，返回真实操作结果。", false,
            """{"adapter":{"type":"string"},"profile":{"type":"string"}}""", "adapter", "profile"),
        new("wifi_disconnect", "断开指定无线接口。自动恢复开启时之后可能按策略重连；长期禁用请用 device_set_state。", false,
            """{"adapter":{"type":"string"}}""", "adapter"),
        new("wifi_bind", "指定 SSID 只允许在 adapter 连接；省略 adapter 则取消指定。", false,
            """{"ssid":{"type":"string"},"adapter":{"type":"string"}}""", "ssid"),
        new("campus_set", "设置或取消某个 SSID 的校园网标记，立即保存。", false,
            """{"ssid":{"type":"string"},"enabled":{"type":"boolean"}}""", "ssid", "enabled"),
        new("config_get", "读取全部配置或指定顶层 section。凭据库另有工具；命令参数可能含用户填写的敏感信息。", true,
            """{"section":{"type":"string"}}"""),
        new("config_patch", "递归合并配置并立即保存应用；数组整体替换，拒绝未知字段或错误类型。可设置校园认证、探测、学习、恢复、命令、启动和 MCP。", false,
            """{"patch":{"type":"object"}}""", "patch"),
        new("config_reload", "重新读取并应用磁盘配置。", false, "{}"),
        new("guardian_pause", "暂停或恢复自动恢复；已配置的保持禁用仍生效。", false,
            """{"paused":{"type":"boolean"}}""", "paused"),
        new("guardian_refresh", "请求重新枚举设备和探测；返回请求已受理，不冒充刷新完成。", true, "{}"),
        new("connectivity_test", "立即逐接口执行连通性探测，可能需要数十秒。", true, "{}"),
        new("recovery_reset", "清除失败降权、重试和冷却状态。", false, "{}"),
        new("credentials_list", "列出应用凭据库；默认隐藏密码和加密密文，includeSecrets=true 时显式读取内存中的密码。", true,
            """{"includeSecrets":{"type":"boolean"}}"""),
        new("credentials_upsert", "创建或更新一条 Wi-Fi 凭据并保存应用。entry 使用凭据模型字段；password 单独传入，省略则保留原密码。", false,
            """{"entry":{"type":"object"},"password":{"type":"string"}}""", "entry"),
        new("credentials_delete", "从应用凭据库删除 id；不会擅自删除系统配置。", false,
            """{"id":{"type":"string"}}""", "id"),
        new("credentials_apply", "将指定凭据应用到指定网卡或全部网卡；不等于连接成功。", false,
            """{"id":{"type":"string"},"adapter":{"type":"string"}}""", "id"),
        new("wifi_profile_delete", "删除系统 WLAN profile；省略 adapter 则从所有网卡删除。", false,
            """{"profile":{"type":"string"},"adapter":{"type":"string"}}""", "profile"),
        new("learning_status", "模型更新次数、偏好参数、问答集摘要和当前问题；不虚构准确率。", true, "{}"),
        new("learning_question", "读取当前选择题及实时背景；next=true 则跳过并换题。", true,
            """{"next":{"type":"boolean"}}"""),
        new("learning_answer", "回答当前选择题，验证 questionId/value，记录本机答题时网络快照，并按可信度学习。", false,
            """{"questionId":{"type":"string"},"value":{"type":"string"}}""", "questionId", "value"),
        new("learning_choose", "对当前可选网络提供偏好反馈，可选择只临时生效或长期记忆；使用 learning_options 返回的 key。", false,
            """{"key":{"type":"string"},"remember":{"type":"boolean"}}""", "key", "remember"),
        new("learning_options", "当前可供偏好选择的网络与网卡、实测质量及 key。", true, "{}"),
        new("learning_delete_answer", "删除指定答题记录并基于保留记录重新训练。", false,
            """{"questionId":{"type":"string"}}""", "questionId"),
        new("learning_reset", "重置本地答题、偏好和模型至内置种子。", false, "{}"),
        new("learning_export", "将问答和偏好数据导出到本机数据目录，返回两个文件路径。", false, "{}"),
        new("debug_logs", "按级别、分类和关键字查询内存日志，最多 500 条。不会记录 MCP 工具参数或密码。", true,
            """{"limit":{"type":"integer","minimum":1,"maximum":500},"search":{"type":"string"}}"""),
        new("debug_clear_logs", "清空内存日志缓冲，不删除日志文件。", false, "{}"),
        new("debug_bundle", "汇总版本、进程资源、探测、路由、设备、失败记录和设置；不包含凭据密码、命令参数或完整用户答题集。", true, "{}"),
        new("debug_model", "查看本地神经网络权重、有效性和训练次数，供离线诊断。", true, "{}"),
        new("debug_decision", "返回最近实际决策说明、任务与策略。只读，不重新驱动引擎或下发网络动作。", true, "{}"),
    };
}

public interface IMcpToolBackend
{
    Task<JsonNode?> InvokeAsync(string name, JsonObject arguments, CancellationToken token);
}
