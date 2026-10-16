using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using NetworkGuardian.Core.Serialization;
using NetworkGuardian.Infrastructure.Mcp;
using NetworkGuardian.Portable.Host;
using NetworkGuardian.Portable.Services;

namespace NetworkGuardian.Portable.Mcp;

internal sealed class GuardianMcpBackend(PortableApp app, GuardianHostService host) : IMcpToolBackend
{
    private readonly SemaphoreSlim _writes = new(1, 1);
    private static readonly JsonSerializerOptions StrictOptions = new(NetworkGuardianJson.Options)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly JsonTypeInfo<GuardianConfig> StrictConfigInfo =
        (JsonTypeInfo<GuardianConfig>)StrictOptions.GetTypeInfo(typeof(GuardianConfig));
    private static JsonNode? Data<T>(T value) => JsonNode.Parse(NetworkGuardianJson.Serialize(value));
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    private static string Text(JsonObject args, string key) => args[key]?.GetValue<string>() ?? throw new ArgumentException($"Missing {key}");
    private static Guid Adapter(JsonObject args) => Guid.TryParse(Text(args, "adapter"), out var guid) && guid != Guid.Empty
        ? guid : throw new ArgumentException("adapter 必须是设备查询返回的有效接口 GUID");
    private static JsonObject Done() => new() { ["success"] = true };

    public async Task<JsonNode?> InvokeAsync(string name, JsonObject args, CancellationToken token)
    {
        var write = !McpTools.All.First(t => t.Name == name).ReadOnly;
        if (write) await _writes.WaitAsync(token).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        try { return await InvokeCoreAsync(name, args, token).ConfigureAwait(false); }
        finally
        {
            host.LogUiDebug($"MCP tool {name} finished in {watch.ElapsedMilliseconds} ms (arguments omitted)");
            if (write) _writes.Release();
        }
    }

    private async Task<JsonNode?> InvokeCoreAsync(string name, JsonObject args, CancellationToken token)
    {
        switch (name)
        {
            case "guardian_status": return new JsonObject { ["snapshot"] = Data(host.Snapshot), ["mcp"] = app.McpStatus };
            case "quality_report": return new JsonObject { ["report"] = Data(NetworkQualityReporter.Build(host.Snapshot,
                host.LinkQuality, host.Config, host.Brain, DateTimeOffset.UtcNow)), ["measurements"] = Data(host.LinkQuality.ToArray()) };
            case "devices_list": return new JsonObject { ["devices"] = Data(host.Devices.ToArray()),
                ["keptDisabled"] = Strings(host.Config.DisabledDeviceInstanceIds) };
            case "device_set_state":
                var deviceId = Text(args, "deviceId");
                var state = Text(args, "state");
                var device = host.Devices.FirstOrDefault(d => string.Equals(d.Record.DeviceInstanceId, deviceId, StringComparison.OrdinalIgnoreCase));
                if (state != "release" && (device is null || !device.Classification.IsPhysical || !device.Record.IsPresent))
                    throw new ArgumentException("设备未识别为当前存在的物理网卡；请先刷新查询");
                await app.UpdateConfigAsync(c =>
                {
                    c.DisabledDeviceInstanceIds.RemoveAll(id => string.Equals(id, deviceId, StringComparison.OrdinalIgnoreCase));
                    if (state == "kept_disabled") c.DisabledDeviceInstanceIds.Add(deviceId);
                }).ConfigureAwait(false);
                if (state == "release") return Done();
                var operation = state == "enabled" ? await host.EnableDeviceAsync(deviceId, token).ConfigureAwait(false)
                    : await host.DisableDeviceAsync(deviceId, token).ConfigureAwait(false);
                return new JsonObject { ["success"] = operation.Success, ["ruleSaved"] = true, ["operation"] = Data(operation) };
            case "device_restart": return Data(await host.RestartDeviceAsync(Text(args, "deviceId"), token).ConfigureAwait(false));
            case "wifi_radio_set": return Data(await host.SetRadioAsync(args["enabled"]!.GetValue<bool>(), token).ConfigureAwait(false));
            case "command_run": return Data(await host.RunConfiguredCommandAsync(Text(args, "id"), token).ConfigureAwait(false));
            case "wifi_list": return new JsonObject { ["adapters"] = Data(host.Snapshot.WifiAdapters.ToArray()) };
            case "wifi_scan":
                if (args["adapter"] is not null)
                {
                    var scan = await host.ScanAdapterAsync(Adapter(args), token).ConfigureAwait(false);
                    return new JsonObject { ["success"] = !scan.Failed, ["scan"] = Data(scan) };
                }
                var scans = new JsonArray();
                var scansOk = host.Snapshot.WifiAdapters.Count > 0;
                foreach (var adapter in host.Snapshot.WifiAdapters)
                {
                    var result = await host.ScanAdapterAsync(adapter.InterfaceGuid, token).ConfigureAwait(false);
                    scansOk &= !result.Failed;
                    scans.Add(Data(result));
                }
                return new JsonObject { ["success"] = scansOk, ["scans"] = scans, ["completed"] = true };
            case "wifi_connect": return Data(await host.ConnectAsync(Adapter(args), Text(args, "profile"), token).ConfigureAwait(false));
            case "wifi_disconnect": return Data(await host.DisconnectAsync(Adapter(args), token).ConfigureAwait(false));
            case "wifi_bind":
                var ssid = Text(args, "ssid");
                var owner = args["adapter"] is null ? (Guid?)null : Adapter(args);
                if (owner is { } id && !host.Devices.Any(d => Guid.TryParse(d.Record.NetCfgInstanceId, out var actual) &&
                    actual == id && d.Classification.Category == DeviceCategory.PhysicalWifi))
                    throw new ArgumentException("指定目标不是已识别的物理无线网卡");
                await app.UpdateConfigAsync(c =>
                {
                    if (owner is { } guid) c.Wifi.CampusWifiAdapterAssignments[ssid] = guid.ToString("D");
                    else c.Wifi.CampusWifiAdapterAssignments.Remove(ssid);
                }).ConfigureAwait(false);
                return Done();
            case "campus_set":
                var campusSsid = Text(args, "ssid");
                await app.UpdateConfigAsync(c =>
                {
                    c.Wifi.CampusNetworkSsids.RemoveAll(s => string.Equals(s, campusSsid, StringComparison.OrdinalIgnoreCase));
                    if (args["enabled"]!.GetValue<bool>()) c.Wifi.CampusNetworkSsids.Add(campusSsid);
                }).ConfigureAwait(false);
                return Done();
            case "config_get":
                var configNode = JsonNode.Parse(ConfigJson.Serialize(host.Config))!.AsObject();
                if (args["section"] is not null)
                {
                    var section = Text(args, "section");
                    if (!configNode.ContainsKey(section)) throw new ArgumentException("未知配置 section");
                    return new JsonObject { [section] = configNode[section]?.DeepClone() };
                }
                return configNode;
            case "config_patch":
                await app.UpdateConfigAsync(c =>
                {
                    if (args["patch"]!.AsObject().ContainsKey("version")) throw new ArgumentException("version 是只读配置字段");
                    var merged = JsonNode.Parse(ConfigJson.Serialize(c))!.AsObject();
                    Merge(merged, args["patch"]!.AsObject());
                    var target = JsonSerializer.Deserialize(merged.ToJsonString(), StrictConfigInfo)!;
                    var issues = new ConfigValidator().Normalize(target);
                    if (issues.Count > 0) throw new ArgumentException("配置超出范围或存在无效值：" + string.Join("；", issues));
                    CopyConfig(target, c);
                }).ConfigureAwait(false);
                return new JsonObject { ["success"] = !host.Config.Mcp.Enabled || app.McpStatus["listening"]!.GetValue<bool>(),
                    ["saved"] = true, ["mcp"] = app.McpStatus };
            case "config_reload": await app.ReloadConfigAsync().ConfigureAwait(false); return Done();
            case "guardian_pause": host.SetPaused(args["paused"]!.GetValue<bool>()); return Done();
            case "guardian_refresh": host.RequestImmediateCycle(); return new JsonObject { ["accepted"] = true, ["completed"] = false };
            case "connectivity_test": return Data(await host.RunConnectivityTestAsync(token).ConfigureAwait(false));
            case "recovery_reset": host.ResetDerating(); return Done();
            case "credentials_list":
                var includeSecrets = args["includeSecrets"]?.GetValue<bool>() == true;
                var list = new JsonArray();
                foreach (var entry in host.WifiLibraryEntries)
                {
                    var node = Data(entry)!.AsObject();
                    node.Remove("passwordProtected");
                    node["hasPassword"] = !string.IsNullOrEmpty(entry.Password) || !string.IsNullOrEmpty(entry.PasswordProtected);
                    node["passwordDecryptionFailed"] = entry.PasswordDecryptionFailed;
                    if (includeSecrets) node["password"] = entry.Password;
                    list.Add((JsonNode)node);
                }
                return new JsonObject { ["entries"] = list, ["kindValues"] = "0=enterprise,1=personal,2=open",
                    ["securityValues"] = Strings(Enum.GetNames<WifiSecurity>()), ["eapValues"] = Strings(Enum.GetNames<WifiEapMethod>()) };
            case "credentials_upsert":
                var patch = args["entry"]!.AsObject();
                if (patch.ContainsKey("password") || patch.ContainsKey("passwordProtected"))
                    throw new ArgumentException("密码请使用顶层 password 参数，不能直接修改加密字段");
                var entries = host.WifiLibraryEntries.ToList();
                var existing = patch["id"] is { } entryId ? entries.FirstOrDefault(e => e.Id == entryId.GetValue<string>())
                    : entries.FirstOrDefault(e => string.Equals(e.Ssid, patch["ssid"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase));
                var entryNode = Data(existing ?? new WifiNetworkCredential())!.AsObject();
                Merge(entryNode, patch);
                var credential = NetworkGuardianJson.Deserialize<WifiNetworkCredential>(entryNode.ToJsonString())!;
                credential.Password = args["password"]?.GetValue<string>() ?? existing?.Password;
                if (string.IsNullOrWhiteSpace(credential.Ssid)) throw new ArgumentException("SSID 不能为空");
                if (existing is not null) entries.Remove(existing);
                entries.Add(credential);
                await host.SaveWifiLibraryAsync(entries, token).ConfigureAwait(false);
                return new JsonObject { ["success"] = true, ["saved"] = true, ["id"] = credential.Id, ["ssid"] = credential.Ssid,
                    ["note"] = "已保存并尝试应用；不代表每张网卡配置成功或外网可用，请核对 credentials_apply 与日志" };
            case "credentials_delete":
                var remaining = host.WifiLibraryEntries.ToList();
                if (remaining.RemoveAll(e => e.Id == Text(args, "id")) == 0) throw new ArgumentException("凭据 id 不存在");
                await host.SaveWifiLibraryAsync(remaining, token).ConfigureAwait(false); return Done();
            case "credentials_apply":
                if (!host.WifiLibraryEntries.Any(e => e.Id == Text(args, "id"))) throw new ArgumentException("凭据 id 不存在");
                var applied = await host.ApplyWifiLibraryEntryAsync(Text(args, "id"), args["adapter"] is null ? null : Adapter(args), token).ConfigureAwait(false);
                return new JsonObject { ["success"] = !applied.Any(s => s.Contains("失败") || s.Contains("无法") || s.Contains("没有可用")), ["results"] = Strings(applied) };
            case "wifi_profile_delete":
                if (args["adapter"] is not null) return Data(host.RemoveWifiProfile(Adapter(args), Text(args, "profile")));
                var removed = host.RemoveWifiProfileEverywhere(Text(args, "profile"));
                return new JsonObject { ["success"] = removed.Success, ["attempted"] = removed.Attempted,
                    ["removed"] = removed.Removed, ["remaining"] = removed.Remaining, ["failures"] = Strings(removed.Failures) };
            case "learning_status": return new JsonObject { ["personalSteps"] = host.Brain.PersonalSteps,
                ["trainingSteps"] = host.Brain.TrainingSteps, ["totalAnswers"] = host.Survey.TotalAnswers,
                ["answers"] = Data(host.Survey), ["preferences"] = Data(host.Preferences), ["currentQuestion"] = Data(host.SurveyQuestion) };
            case "learning_question":
                if (args["next"]?.GetValue<bool>() == true || host.SurveyQuestion is null) host.NextSurveyQuestion();
                return new JsonObject { ["question"] = Data(host.SurveyQuestion), ["learningEnabled"] = host.Config.Quality.LearningEnabled };
            case "learning_answer": await host.AnswerSurveyAsync(Text(args, "questionId"), Text(args, "value")).ConfigureAwait(false); return Done();
            case "learning_options": return new JsonObject { ["options"] = Data(host.GetPreferenceOptions().ToArray()) };
            case "learning_choose":
                var option = host.GetPreferenceOptions().FirstOrDefault(o => o.Key == Text(args, "key")) ?? throw new ArgumentException("偏好选项已失效，请重新查询");
                await host.ChoosePreferenceAsync(option, args["remember"]!.GetValue<bool>(), token).ConfigureAwait(false); return Done();
            case "learning_delete_answer":
                if (!host.Survey.Answers.Any(a => a.Question.Id == Text(args, "questionId"))) throw new ArgumentException("答题记录不存在");
                await host.RemoveSurveyAnswerAsync(Text(args, "questionId")).ConfigureAwait(false); return Done();
            case "learning_reset": await host.ResetPreferencesAsync().ConfigureAwait(false); return Done();
            case "learning_export":
                await host.ExportSurveyAsync().ConfigureAwait(false);
                await host.ExportPreferencesAsync().ConfigureAwait(false);
                return new JsonObject { ["success"] = true, ["directory"] = GuardianPaths.Root };
            case "debug_logs":
                var search = args["search"]?.GetValue<string>();
                var category = args["category"]?.GetValue<string>();
                var minimum = args["minimumLevel"] is { } level ? Enum.Parse<GuardianLogLevel>(level.GetValue<string>(), true) : GuardianLogLevel.Trace;
                return new JsonObject { ["logs"] = Data(host.LogSink.Snapshot().Where(r => search is null ||
                    r.Message.Contains(search, StringComparison.OrdinalIgnoreCase) || r.Category.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .Where(r => r.Level >= minimum && (category is null || r.Category.Contains(category, StringComparison.OrdinalIgnoreCase)))
                    .TakeLast(args["limit"]?.GetValue<int>() ?? 100).ToArray()) };
            case "debug_clear_logs": host.LogSink.Clear(); return Done();
            case "debug_model": return new JsonObject { ["valid"] = NetworkBrain.Valid(host.Brain), ["model"] = Data(host.Brain) };
            case "debug_decision": return new JsonObject { ["notes"] = Strings(host.Snapshot.Notes),
                ["pending"] = Strings(host.Snapshot.PendingActions.Select(a => a.Describe())),
                ["recent"] = Strings(host.RecentTasks.Select(t => $"{t.At:O}: {t.Action.Describe()}")) };
            case "debug_bundle":
                using (var process = Process.GetCurrentProcess())
                    return new JsonObject { ["version"] = typeof(PortableApp).Assembly.GetName().Version?.ToString(),
                        ["processId"] = Environment.ProcessId, ["workingSetBytes"] = process.WorkingSet64,
                        ["handles"] = process.HandleCount, ["snapshot"] = Data(host.Snapshot), ["mcp"] = app.McpStatus,
                        ["quality"] = Data(host.LinkQuality.ToArray()), ["modelValid"] = NetworkBrain.Valid(host.Brain),
                        ["personalSteps"] = host.Brain.PersonalSteps, ["npcap"] = host.NpcapStatus.Detail };
            default: throw new ArgumentException("Unknown tool");
        }
    }

    private static void Merge(JsonObject target, JsonObject patch, string path = "")
    {
        foreach (var (key, value) in patch)
        {
            var dynamicKeys = path == "wifi.campusWifiAdapterAssignments";
            if (!target.ContainsKey(key) && !dynamicKeys) throw new ArgumentException($"未知字段：{key}");
            if (value is null && dynamicKeys) { target.Remove(key); continue; }
            if (value is null && target[key] is JsonObject or JsonArray) throw new ArgumentException($"配置分组和数组不能设为 null：{key}");
            if (value is JsonObject child)
            {
                if (target[key] is not JsonObject current) throw new ArgumentException($"字段类型不匹配：{key}");
                Merge(current, child, path.Length == 0 ? key : path + "." + key);
            }
            else target[key] = value?.DeepClone();
        }
    }

    private static void CopyConfig(GuardianConfig source, GuardianConfig target)
    {
        target.General = source.General; target.ConnectionMigration = source.ConnectionMigration; target.Recovery = source.Recovery;
        target.Probe = source.Probe; target.Wifi = source.Wifi; target.Quality = source.Quality; target.Mcp = source.Mcp;
        target.Ethernet = source.Ethernet; target.CampusAuth = source.CampusAuth; target.OfflineCommands = source.OfflineCommands;
        target.Startup = source.Startup; target.Logging = source.Logging; target.ProbeEndpoints = source.ProbeEndpoints;
        target.InterfaceDenyList = source.InterfaceDenyList; target.DisabledDeviceInstanceIds = source.DisabledDeviceInstanceIds;
    }
}
