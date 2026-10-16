using System.Text.Json.Nodes;
using NetworkGuardian.Core.Policies;
using NetworkGuardian.Core.Serialization;

namespace NetworkGuardian.Infrastructure.Mcp;

/// <summary>Durable training independent of HTTP requests. Each checkpoint is an atomic job file.</summary>
public sealed class DistillationService : IAsyncDisposable
{
    private readonly string _directory;
    private readonly Func<NetworkBrainModel> _current;
    private readonly Func<string, NetworkBrainModel, Task> _replace;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DistillationJob> _jobs = new(StringComparer.Ordinal);
    private Task? _worker;
    private bool _stopping;

    public DistillationService(string directory, Func<NetworkBrainModel> current,
        Func<string, NetworkBrainModel, Task> replace)
    {
        _directory = directory; _current = current; _replace = replace;
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            DistillationJob? job = null;
            foreach (var path in new[] { file, file + ".bak" })
            {
                try
                {
                    if (new FileInfo(path).Length > 2 * 1024 * 1024) continue;
                    var loaded = NetworkGuardianJson.Deserialize<DistillationJob>(File.ReadAllText(path));
                    if (loaded is null || !Guid.TryParseExact(loaded.Id, "N", out _) ||
                        Path.GetFileName(file) != loaded.Id + ".json" || !NetworkBrain.Valid(loaded.Baseline) ||
                        !NetworkBrain.Valid(loaded.Candidate) || loaded.Examples is null || loaded.Examples.Count > 512 ||
                        loaded.Examples.Any(r => r.Features is not { Length: 8 } || r.Features.Any(x => !double.IsFinite(x) || x < 0 || x > 1) ||
                            r.Id != BrainDistillation.ExampleId(r.Features) || r.Target is { } t && (!double.IsFinite(t) || t < 0 || t > 1)) ||
                        loaded.Examples.Select(r => r.Id).Distinct().Count() != loaded.Examples.Count ||
                        loaded.Epoch < 0 || loaded.Epochs < 20 || loaded.Epochs > 1000 || loaded.Epoch > loaded.Epochs ||
                        loaded.BaseHash != BrainDistillation.Hash(loaded.Baseline)) continue;
                    job = loaded; break;
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or ArgumentException) { }
            }
            if (job is null) throw new InvalidDataException($"训练任务损坏且无有效备份：{Path.GetFileName(file)}；请保留文件进行诊断。");
            if (job.State == "running") job.State = "paused";
            if (job.State == "deploying") job.State = _current().Provenance == job.Candidate.Provenance ? "deployed" : "ready";
            if (job.State == "rolling_back") job.State = _current().Provenance == job.Rollback?.Provenance ? "rolled_back" : "deployed";
            _jobs.Add(job.Id, job);
        }
    }

    private void Save(DistillationJob job)
    {
        var path = Path.Combine(_directory, job.Id + ".json");
        File.WriteAllText(path + ".tmp", NetworkGuardianJson.Serialize(job));
        if (File.Exists(path)) File.Replace(path + ".tmp", path, path + ".bak");
        else File.Move(path + ".tmp", path);
    }

    private static JsonObject Summary(DistillationJob j) => new()
    {
        ["jobId"] = j.Id, ["requestId"] = j.RequestId, ["teacher"] = j.Teacher,
        ["createdAtUtc"] = j.CreatedAtUtc.ToString("O"), ["state"] = j.State, ["epoch"] = j.Epoch,
        ["epochs"] = j.Epochs, ["examples"] = j.Examples.Count,
        ["labeled"] = j.Examples.Count(r => r.Target is not null), ["baseHash"] = j.BaseHash,
        ["candidateHash"] = BrainDistillation.Hash(j.Candidate), ["deployedHash"] = j.DeployedHash,
        ["evaluation"] = j.Evaluation is null ? null : JsonNode.Parse(NetworkGuardianJson.Serialize(j))!["evaluation"]!.DeepClone(),
        ["error"] = j.Error,
    };

    public async Task<JsonObject> InvokeAsync(string action, JsonObject args, IEnumerable<DistillationExample>? observations = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopping) throw new InvalidOperationException("训练服务正在退出");
            if (action == "status")
            {
                if (args["jobId"] is { } id) return Summary(Get(id.GetValue<string>()));
                return new() { ["currentModelHash"] = BrainDistillation.Hash(_current()),
                    ["jobs"] = new JsonArray(_jobs.Values.OrderByDescending(j => j.CreatedAtUtc).Select(j => (JsonNode)Summary(j)).ToArray()),
                    ["featureOrder"] = new JsonArray("quality01", "failureRate", "latencyDiv500", "jitterDiv100", "signal01", "measurementConfidence", "online01", "preference01"),
                    ["limits"] = "64 jobs, 512 examples/job, 64 labels/batch, 20..1000 epochs; no autonomous external API calls" };
            }
            if (action == "create")
            {
                var request = Required(args, "requestId", 128);
                var existing = _jobs.Values.FirstOrDefault(j => j.RequestId == request);
                if (existing is not null) return Summary(existing);
                if (_jobs.Count >= 64) throw new InvalidOperationException("训练任务已达 64 个，请删除不再需要的任务。");
                var teacher = Required(args, "teacher", 200);
                var count = args["count"]?.GetValue<int>() ?? 160;
                var epochs = args["epochs"]?.GetValue<int>() ?? 120;
                if (count < 96 || count > 512 || epochs < 20 || epochs > 1000) throw new ArgumentException("count=96..512; epochs=20..1000");
                var baseline = BrainDistillation.Clone(_current());
                var job = new DistillationJob { Id = Guid.NewGuid().ToString("N"), RequestId = request, Teacher = teacher,
                    CreatedAtUtc = DateTimeOffset.UtcNow, Baseline = baseline, Candidate = BrainDistillation.Clone(baseline),
                    BaseHash = BrainDistillation.Hash(baseline), Epochs = epochs };
                job.Candidate.Provenance = $"mcp-distillation:{job.Id}";
                var measured = (observations ?? Array.Empty<DistillationExample>()).Take(64).ToArray();
                job.Examples = measured.Concat(BrainDistillation.Synthetic(count, Random.Shared.Next())).DistinctBy(r => r.Id).Take(count).ToList();
                Save(job); _jobs.Add(job.Id, job);
                return Summary(job);
            }
            var selected = Get(Required(args, "jobId", 32));
            switch (action)
            {
                case "examples":
                    var offset = args["offset"]?.GetValue<int>() ?? 0;
                    var limit = args["limit"]?.GetValue<int>() ?? 32;
                    if (offset < 0 || limit < 1 || limit > 64) throw new ArgumentException("offset>=0; limit=1..64");
                    var unlabeled = args["unlabeledOnly"]?.GetValue<bool>() ?? true;
                    var rows = selected.Examples.Where(r => !unlabeled || r.Target is null).ToArray();
                    return new() { ["jobId"] = selected.Id, ["remaining"] = rows.Length,
                        ["examples"] = new JsonArray(rows.Skip(offset).Take(limit).Select(r => (JsonNode)new JsonObject
                        { ["id"] = r.Id, ["features"] = new JsonArray(r.Features.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                            ["source"] = r.Source, ["observedAtUtc"] = r.ObservedAtUtc?.ToString("O"), ["target"] = r.Target }).ToArray()) };
                case "label":
                    var labels = args["labels"] as JsonObject ?? throw new ArgumentException("labels 必须是以样例 id 为键的对象");
                    if (labels.Count is < 1 or > 64) throw new ArgumentException("每批 1..64 条标签");
                    var updates = new List<(DistillationExample Row, double Target, double Confidence, string Rationale)>();
                    foreach (var (id, value) in labels)
                    {
                        var row = selected.Examples.FirstOrDefault(r => r.Id == id) ?? throw new ArgumentException("未知样例 id");
                        var label = value as JsonObject ?? throw new ArgumentException("标签必须是对象");
                        if (label.Any(p => p.Key is not ("target" or "confidence" or "rationale"))) throw new ArgumentException("标签含未知字段");
                        var target = label["target"]?.GetValue<double>() ?? double.NaN;
                        var confidence = label["confidence"]?.GetValue<double>() ?? double.NaN;
                        var rationale = Required(label, "rationale", 500);
                        if (!double.IsFinite(target) || target < 0 || target > 1 || !double.IsFinite(confidence) || confidence < 0 || confidence > 1)
                            throw new ArgumentException("target/confidence 必须是 0..1 有限数值");
                        if (selected.State != "labeling" && (row.Target != target || row.Confidence != confidence || row.Rationale != rationale))
                            throw new InvalidOperationException("训练开始后数据集已冻结；请创建新任务。");
                        updates.Add((row, target, confidence, rationale));
                    }
                    foreach (var (row, target, confidence, rationale) in updates)
                    { row.Target = target; row.Confidence = confidence; row.Rationale = rationale; }
                    Save(selected); return Summary(selected);
                case "start":
                    if (selected.State == "running" || selected.State is "ready" or "deployed" or "rolled_back") return Summary(selected);
                    if (selected.State is not ("labeling" or "paused" or "failed")) throw new InvalidOperationException("当前状态不能训练");
                    if (_worker is { IsCompleted: false }) throw new InvalidOperationException("已有训练任务运行，请稍后重试。");
                    if (selected.Examples.Any(r => r.Target is null)) throw new InvalidOperationException("请先标注全部样例，避免挑选验证结果。");
                    _ = BrainDistillation.Evaluate(selected);
                    selected.State = "running"; selected.Error = null; Save(selected);
                    _worker = Task.Run(() => TrainAsync(selected));
                    return Summary(selected);
                case "pause":
                    if (selected.State == "running") { selected.State = "paused"; Save(selected); }
                    return Summary(selected);
                case "evaluate":
                    if (selected.State is not ("ready" or "deployed" or "rolled_back")) throw new InvalidOperationException("请先完成训练");
                    selected.Evaluation = BrainDistillation.Evaluate(selected); Save(selected); return Summary(selected);
                case "deploy":
                    if (selected.State == "deployed") return Summary(selected);
                    if (selected.State != "ready") throw new InvalidOperationException("只有训练完成的候选模型可部署");
                    selected.Evaluation = BrainDistillation.Evaluate(selected);
                    if (!selected.Evaluation.Passed) { Save(selected); return new() { ["success"] = false, ["job"] = Summary(selected) }; }
                    selected.Rollback = BrainDistillation.Clone(selected.Baseline);
                    selected.State = "deploying"; Save(selected);
                    try { await _replace(selected.BaseHash, selected.Candidate).ConfigureAwait(false); }
                    catch { selected.State = "ready"; Save(selected); throw; }
                    selected.DeployedHash = BrainDistillation.Hash(selected.Candidate); selected.State = "deployed";
                    Save(selected); return Summary(selected);
                case "rollback":
                    if (selected.State == "rolled_back") return Summary(selected);
                    if (selected.State != "deployed" || selected.Rollback is null) throw new InvalidOperationException("该任务没有已部署模型可回滚");
                    var expected = Required(args, "expectedHash", 64);
                    // Explicit compare-and-swap prevents silently discarding subsequent personal learning.
                    if (expected != selected.DeployedHash) throw new InvalidOperationException("部署后模型已有变化；请保留新数据并创建新任务，不覆盖后续个人学习。");
                    selected.State = "rolling_back"; Save(selected);
                    try { await _replace(expected, selected.Rollback).ConfigureAwait(false); }
                    catch { selected.State = "deployed"; Save(selected); throw; }
                    selected.State = "rolled_back"; Save(selected); return Summary(selected);
                case "delete":
                    if (selected.State is "running" or "deployed" or "deploying" or "rolling_back")
                        throw new InvalidOperationException("运行中或仍需回滚的部署任务不能删除");
                    File.Delete(Path.Combine(_directory, selected.Id + ".json"));
                    File.Delete(Path.Combine(_directory, selected.Id + ".json.bak"));
                    _jobs.Remove(selected.Id); return new() { ["success"] = true };
                default: throw new ArgumentException("未知训练动作");
            }
        }
        finally { _gate.Release(); }
    }

    private DistillationJob Get(string id) => _jobs.GetValueOrDefault(id) ?? throw new ArgumentException("训练 jobId 不存在");
    private static string Required(JsonObject args, string key, int max)
    {
        var value = args[key]?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(value) && value.Length <= max ? value : throw new ArgumentException($"{key} 必须为非空字符串，最多 {max} 字符");
    }

    private async Task TrainAsync(DistillationJob job)
    {
        try
        {
            while (true)
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_stopping || job.State != "running")
                    { if (job.State == "running") { job.State = "paused"; Save(job); } return; }
                    var end = Math.Min(job.Epochs, job.Epoch + 5);
                    while (job.Epoch < end) BrainDistillation.TrainEpoch(job);
                    if (job.Epoch == job.Epochs)
                    { job.Evaluation = BrainDistillation.Evaluate(job); job.State = "ready"; }
                    Save(job);
                    if (job.State == "ready") return;
                }
                finally { _gate.Release(); }
                await Task.Delay(10).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { job.State = "failed"; job.Error = ex.Message; Save(job); }
            catch (IOException) { /* Preserve the previous durable checkpoint when the disk is unavailable. */ }
            finally { _gate.Release(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _stopping = true; }
        finally { _gate.Release(); }
        if (_worker is not null) await _worker.ConfigureAwait(false);
    }
}
