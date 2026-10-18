using System.Security.Cryptography;
using System.Text;
using NetworkGuardian.Core.Serialization;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Policies;

public sealed class DistillationExample
{
    public InterfaceKind? NetworkKind { get; set; }
    public string InterfaceId { get; set; } = "";
    public string NetworkId { get; set; } = "";
    public string NetworkName { get; set; } = "";
    public string Id { get; set; } = "";
    public double[] Features { get; set; } = Array.Empty<double>();
    public string Source { get; set; } = "synthetic";
    public DateTimeOffset? ObservedAtUtc { get; set; }
    public double? Target { get; set; }
    public double Confidence { get; set; }
    public string Rationale { get; set; } = "";
}

public sealed class DistillationJob
{
    public string Id { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string Teacher { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string State { get; set; } = "labeling";
    public string? Error { get; set; }
    public string BaseHash { get; set; } = "";
    public NetworkBrainModel Baseline { get; set; } = new();
    public NetworkBrainModel Candidate { get; set; } = new();
    public NetworkBrainModel? Rollback { get; set; }
    public string? DeployedHash { get; set; }
    public int Epoch { get; set; }
    public int Epochs { get; set; } = 120;
    public List<DistillationExample> Examples { get; set; } = new();
    public DistillationEvaluation? Evaluation { get; set; }
}

public sealed record DistillationEvaluation(int TrainCount, int ValidationCount, double BaselineTeacherMae,
    double CandidateTeacherMae, double BaselineExpertMae, double CandidateExpertMae, bool Passed, string Reason);

/// <summary>Server-owned examples and splits; teacher labels are auxiliary utility targets.</summary>
public static class BrainDistillation
{
    public static NetworkBrainModel Clone(NetworkBrainModel model) =>
        NetworkGuardianJson.Deserialize<NetworkBrainModel>(NetworkGuardianJson.Serialize(model))!;

    public static string Hash(NetworkBrainModel model) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(NetworkGuardianJson.Serialize(model))));

    public static string ExampleId(double[] features) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join(",", features.Select(x => x.ToString("F6", System.Globalization.CultureInfo.InvariantCulture))))));

    // Identical feature vectors always belong to the same split, including across jobs.
    public static bool IsValidation(DistillationExample row) => Convert.ToByte(row.Id[..2], 16) % 5 == 0;

    public static List<DistillationExample> Synthetic(int count, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count).Select(_ =>
        {
            var x = Enumerable.Range(0, 8).Select(_ => random.NextDouble()).ToArray();
            x[6] = random.Next(5) > 0 ? 1 : 0;
            return new DistillationExample { Id = ExampleId(x), Features = x };
        }).ToList();
    }

    public static void TrainEpoch(DistillationJob job)
    {
        foreach (var row in job.Examples.Where(r => r.Target is not null && !IsValidation(r)))
        {
            var teacherWeight = .75 * row.Confidence;
            var target = teacherWeight * row.Target!.Value + (1 - teacherWeight) * NetworkBrain.Teacher(row.Features);
            NetworkBrain.Train(job.Candidate, row.Features, target, .018);
        }
        job.Epoch++;
    }

    public static DistillationEvaluation Evaluate(DistillationJob job)
    {
        var validation = job.Examples.Where(r => r.Target is not null && IsValidation(r)).ToArray();
        var trainCount = job.Examples.Count(r => r.Target is not null && !IsValidation(r));
        if (trainCount < 48 || validation.Length < 16) throw new InvalidOperationException("至少需要 48 条训练标签和 16 条留出标签；请继续标注。");
        var baseline = validation.Average(r => Math.Abs(NetworkBrain.Predict(job.Baseline, r.Features) - r.Target!.Value));
        var candidate = validation.Average(r => Math.Abs(NetworkBrain.Predict(job.Candidate, r.Features) - r.Target!.Value));
        var guard = Synthetic(256, 954172);
        var baselineExpert = guard.Average(r => Math.Abs(NetworkBrain.Predict(job.Baseline, r.Features) - NetworkBrain.Teacher(r.Features)));
        var candidateExpert = guard.Average(r => Math.Abs(NetworkBrain.Predict(job.Candidate, r.Features) - NetworkBrain.Teacher(r.Features)));
        var passed = NetworkBrain.Valid(job.Candidate) && candidate <= .20 && baseline - candidate >= .0005 &&
            candidateExpert <= .12 && candidateExpert <= baselineExpert + .015;
        return new(trainCount, validation.Length, baseline, candidate, baselineExpert, candidateExpert, passed,
            passed ? "留出教师标签误差降低且基础专家策略未明显退化；不代表真实联网准确率提高。" :
            "未满足部署门槛：留出 MAE ≤0.20、改善 ≥0.0005，专家 MAE ≤0.12 且退化 ≤0.015。保留当前模型。");
    }
}
