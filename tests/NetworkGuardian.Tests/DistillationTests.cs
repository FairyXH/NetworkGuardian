using System.Text.Json.Nodes;
using NetworkGuardian.Core.Policies;
using NetworkGuardian.Infrastructure.Configuration;
using NetworkGuardian.Infrastructure.Mcp;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class DistillationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ng-training-" + Guid.NewGuid().ToString("N"));
    private NetworkBrainModel _current = NetworkBrain.Seed();

    private DistillationService Service() => new(Path.Combine(_root, "jobs"), () => _current, (hash, model) =>
    {
        if (hash != BrainDistillation.Hash(_current)) throw new InvalidOperationException("stale base");
        _current = BrainDistillation.Clone(model); return Task.CompletedTask;
    });

    private static JsonObject Args(string id) => new() { ["jobId"] = id };
    private static async Task<string> Create(DistillationService service, string request = "test", int epochs = 120) =>
        (await service.InvokeAsync("create", new() { ["requestId"] = request, ["teacher"] = "test-expert", ["count"] = 160, ["epochs"] = epochs }))["jobId"]!.GetValue<string>();

    private static double Expert(double[] x) => NetworkBrain.Teacher(x);
    private static async Task LabelAll(DistillationService service, string id, Func<double[], double>? teacher = null)
    {
        while (true)
        {
            var page = await service.InvokeAsync("examples", new() { ["jobId"] = id, ["limit"] = 64 });
            var examples = page["examples"]!.AsArray();
            if (examples.Count == 0) return;
            var labels = new JsonObject();
            foreach (var item in examples)
            {
                var x = item!["features"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
                labels[item["id"]!.GetValue<string>()] = new JsonObject { ["target"] = (teacher ?? Expert)(x), ["confidence"] = 1, ["rationale"] = "isolated test teacher" };
            }
            await service.InvokeAsync("label", new() { ["jobId"] = id, ["labels"] = labels });
            // Retrying a batch must not duplicate or alter dataset sizes.
            await service.InvokeAsync("label", new() { ["jobId"] = id, ["labels"] = labels.DeepClone() });
        }
    }

    private static async Task<JsonObject> Complete(DistillationService service, string id)
    {
        await service.InvokeAsync("start", Args(id));
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            var status = await service.InvokeAsync("status", Args(id));
            if (status["state"]!.GetValue<string>() != "running") return status;
            await Task.Delay(20);
        }
        throw new TimeoutException("training did not complete");
    }

    [Fact]
    public async Task BackgroundTrainingDeploysRollsBackAndSurvivesReload()
    {
        var baseline = BrainDistillation.Hash(_current);
        string id;
        await using (var service = Service())
        {
            id = await Create(service, epochs: 300);
            Assert.Equal(id, await Create(service));
            await LabelAll(service, id);
            var done = await Complete(service, id);
            Assert.Equal("ready", done["state"]!.GetValue<string>());
            Assert.True(done["evaluation"]!["passed"]!.GetValue<bool>(), done.ToJsonString());
            Assert.Equal(baseline, BrainDistillation.Hash(_current));
            var deployed = await service.InvokeAsync("deploy", Args(id));
            Assert.Equal("deployed", deployed["state"]!.GetValue<string>());
            Assert.NotEqual(baseline, BrainDistillation.Hash(_current));
            await service.InvokeAsync("deploy", Args(id));
        }
        await using (var service = Service())
        {
            var deployed = await service.InvokeAsync("status", Args(id));
            await service.InvokeAsync("rollback", new() { ["jobId"] = id, ["expectedHash"] = deployed["deployedHash"]!.GetValue<string>() });
            Assert.Equal(baseline, BrainDistillation.Hash(_current));
        }
    }

    [Fact]
    public async Task PauseCheckpointRestartsAndDatasetIsFrozen()
    {
        string id;
        int checkpoint;
        await using (var service = Service())
        {
            id = await Create(service, epochs: 120);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.InvokeAsync("start", Args(id)));
            await LabelAll(service, id);
            await service.InvokeAsync("start", Args(id));
            var paused = await service.InvokeAsync("pause", Args(id));
            Assert.Equal("paused", paused["state"]!.GetValue<string>());
            checkpoint = paused["epoch"]!.GetValue<int>();
            var examples = await service.InvokeAsync("examples", new() { ["jobId"] = id, ["unlabeledOnly"] = false, ["limit"] = 1 });
            var first = examples["examples"]![0]!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.InvokeAsync("label", new()
            {
                ["jobId"] = id, ["labels"] = new JsonObject { [first["id"]!.GetValue<string>()] =
                    new JsonObject { ["target"] = .999, ["confidence"] = 1, ["rationale"] = "change sealed labels" } },
            }));
        }
        await using (var service = Service())
        {
            var restored = await service.InvokeAsync("status", Args(id));
            Assert.Equal(checkpoint, restored["epoch"]!.GetValue<int>());
            Assert.Equal("paused", restored["state"]!.GetValue<string>());
            var completed = await Complete(service, id);
            Assert.Equal(120, completed["epoch"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task MixedInvalidLabelBatchIsAtomic()
    {
        await using var service = Service();
        var id = await Create(service);
        var examples = await service.InvokeAsync("examples", new() { ["jobId"] = id, ["limit"] = 1 });
        var first = examples["examples"]![0]!["id"]!.GetValue<string>();
        await Assert.ThrowsAsync<ArgumentException>(() => service.InvokeAsync("label", new()
        {
            ["jobId"] = id, ["labels"] = new JsonObject
            {
                [first] = new JsonObject { ["target"] = .5, ["confidence"] = .8, ["rationale"] = "test" },
                ["unknown"] = new JsonObject { ["target"] = 3, ["confidence"] = .8, ["rationale"] = "test" },
            },
        }));
        Assert.Equal(0, (await service.InvokeAsync("status", Args(id)))["labeled"]!.GetValue<int>());
    }

    [Fact]
    public async Task StaleBaseCannotOverwriteNewPersonalLearning()
    {
        await using var service = Service();
        var id = await Create(service, epochs: 300);
        await LabelAll(service, id);
        Assert.True((await Complete(service, id))["evaluation"]!["passed"]!.GetValue<bool>());
        NetworkBrain.Train(_current, new double[8], .4, .01, personal: true);
        var latest = BrainDistillation.Hash(_current);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InvokeAsync("deploy", Args(id)));
        Assert.Equal(latest, BrainDistillation.Hash(_current));
    }

    [Fact]
    public void ContradictoryTeacherCannotPassExpertRegressionGate()
    {
        var baseline = NetworkBrain.Seed();
        var job = new DistillationJob { Baseline = baseline, Candidate = BrainDistillation.Clone(baseline), Examples = BrainDistillation.Synthetic(256, 791) };
        foreach (var row in job.Examples) { row.Target = 1 - Expert(row.Features); row.Confidence = 1; }
        for (var epoch = 0; epoch < 300; epoch++) BrainDistillation.TrainEpoch(job);
        Assert.False(BrainDistillation.Evaluate(job).Passed);
    }

    [Fact]
    public async Task HistorySwapIsDurableAndGuardsAgainstConcurrentLearning()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "history.json");
        var store = new NetworkHistoryStore(path);
        await store.LoadAsync(default);
        var before = BrainDistillation.Hash(store.Brain);
        var model = BrainDistillation.Clone(store.Brain);
        model.Provenance = "test-deployment";
        await store.ReplaceBrainAsync(before, model);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplaceBrainAsync(before, NetworkBrain.Seed()));
        var restored = new NetworkHistoryStore(path);
        await restored.LoadAsync(default);
        Assert.Equal(BrainDistillation.Hash(model), BrainDistillation.Hash(restored.Brain));
    }

    [Fact]
    public async Task BrokenPrimaryJobRecoversFromAtomicBackup()
    {
        string id;
        await using (var service = Service()) { id = await Create(service); await LabelAll(service, id); }
        File.WriteAllText(Path.Combine(_root, "jobs", id + ".json"), "invalid-json");
        await using var recovered = Service();
        Assert.Equal(id, (await recovered.InvokeAsync("status", Args(id)))["jobId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ObsoleteDeploymentCanBeCleanedWithoutDeletingCurrentModel()
    {
        await using var service = Service();
        var id = await Create(service, epochs: 300);
        await LabelAll(service, id);
        Assert.True((await Complete(service, id))["evaluation"]!["passed"]!.GetValue<bool>());
        await service.InvokeAsync("deploy", Args(id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InvokeAsync("delete", Args(id)));
        _current = BrainDistillation.Clone(_current);
        _current.Provenance = "subsequent-deployment";
        var currentHash = BrainDistillation.Hash(_current);
        Assert.True((await service.InvokeAsync("delete", Args(id)))["success"]!.GetValue<bool>());
        Assert.Equal(currentHash, BrainDistillation.Hash(_current));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
