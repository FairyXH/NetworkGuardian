using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Serialization;

namespace NetworkGuardian.Core.Policies;

public sealed class NetworkBrainModel
{
    public int Version { get; set; } = 1;
    public string Provenance { get; set; } = "synthetic-expert-policy-v1";
    public double[] InputWeights { get; set; } = new double[64];
    public double[] HiddenBias { get; set; } = new double[8];
    public double[] OutputWeights { get; set; } = new double[8];
    public double OutputBias { get; set; }
    public long TrainingSteps { get; set; }
    public long PersonalSteps { get; set; }
}

/// <summary>An 8-8-1 utility network; its output is a utility estimate, never a calibrated probability.</summary>
public static class NetworkBrain
{
    public static bool Valid(NetworkBrainModel? m) => m is { Version: 1, InputWeights.Length: 64, HiddenBias.Length: 8, OutputWeights.Length: 8 } &&
        m.InputWeights.Concat(m.HiddenBias).Concat(m.OutputWeights).Append(m.OutputBias).All(double.IsFinite);

    public static NetworkBrainModel Seed()
    {
        using var stream = typeof(NetworkBrain).Assembly.GetManifestResourceStream("NetworkGuardian.NetworkBrain.seed.json");
        if (stream is null) return Initialize();
        using var reader = new StreamReader(stream);
        var model = NetworkGuardianJson.Deserialize<NetworkBrainModel>(reader.ReadToEnd());
        return Valid(model) ? model! : Initialize();
    }

    public static NetworkBrainModel Initialize()
    {
        var random = new Random(7319);
        return new() { InputWeights = Enumerable.Range(0, 64).Select(_ => (random.NextDouble() - .5) * .4).ToArray(),
            OutputWeights = Enumerable.Range(0, 8).Select(_ => (random.NextDouble() - .5) * .4).ToArray() };
    }

    public static double[] Features(PreferenceOption o, bool online, double preference = .5) => new[]
    { Math.Clamp(o.Quality / 100, 0, 1), o.FailureRate ?? .1, Math.Min(1, (o.LatencyMs ?? 100) / 500),
        Math.Min(1, o.JitterMs / 100), o.Candidate.SignalQuality / 100d, o.Confidence, online ? 1d : 0, preference };

    public static double Predict(NetworkBrainModel model, double[] x)
    {
        if (!Valid(model) || x.Length != 8 || x.Any(v => !double.IsFinite(v))) return .5;
        var hidden = Hidden(model, x);
        return Sigmoid(model.OutputBias + hidden.Select((h, j) => h * model.OutputWeights[j]).Sum());
    }

    public static void Train(NetworkBrainModel model, double[] x, double target, double rate, bool personal = false)
    {
        if (!Valid(model) || x.Length != 8 || x.Any(v => !double.IsFinite(v)) || !double.IsFinite(target)) return;
        var hidden = Hidden(model, x);
        var predicted = Sigmoid(model.OutputBias + hidden.Select((h, j) => h * model.OutputWeights[j]).Sum());
        var error = (predicted - Math.Clamp(target, .01, .99)) * predicted * (1 - predicted);
        for (var j = 0; j < 8; j++)
        {
            var delta = error * model.OutputWeights[j] * (1 - hidden[j] * hidden[j]);
            for (var i = 0; i < 8; i++) model.InputWeights[j * 8 + i] = Math.Clamp(model.InputWeights[j * 8 + i] - rate * delta * x[i], -8, 8);
            model.HiddenBias[j] = Math.Clamp(model.HiddenBias[j] - rate * delta, -8, 8);
            model.OutputWeights[j] = Math.Clamp(model.OutputWeights[j] - rate * error * hidden[j], -8, 8);
        }
        model.OutputBias = Math.Clamp(model.OutputBias - rate * error, -8, 8);
        model.TrainingSteps++;
        if (personal) model.PersonalSteps++;
    }

    public static double Teacher(double[] x) => Math.Clamp((.5 * x[0] + .2 * x[6] + .15 * (1 - x[2]) +
        .1 * x[7] + .05 * x[4]) * (1 - .8 * x[1]) * (.25 + .75 * x[6]), .02, .98);

    private static double[] Hidden(NetworkBrainModel model, double[] x) => Enumerable.Range(0, 8)
        .Select(j => Math.Tanh(model.HiddenBias[j] + Enumerable.Range(0, 8).Sum(i => x[i] * model.InputWeights[j * 8 + i]))).ToArray();
    private static double Sigmoid(double x) => 1 / (1 + Math.Exp(-Math.Clamp(x, -30, 30)));
}
