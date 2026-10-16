using NetworkGuardian.Core.Policies;
using NetworkGuardian.Core.Serialization;
using System.Text.Json;

var model = NetworkBrain.Initialize();
var random = new Random(81427);
var data = Enumerable.Range(0, 6000).Select(_ => Enumerable.Range(0, 8).Select(_ => random.NextDouble()).ToArray()).ToArray();
foreach (var x in data) x[6] = x[6] > .2 ? 1 : 0;
for (var epoch = 0; epoch < 40; epoch++)
foreach (var x in data) NetworkBrain.Train(model, x, NetworkBrain.Teacher(x), .06);
var validation = Enumerable.Range(0, 1500).Select(_ => Enumerable.Range(0, 8).Select(_ => random.NextDouble()).ToArray()).ToArray();
foreach (var x in validation) x[6] = x[6] > .2 ? 1 : 0;
var mae = validation.Average(x => Math.Abs(NetworkBrain.Predict(model, x) - NetworkBrain.Teacher(x)));
if (mae > .04) throw new InvalidOperationException($"Teacher validation MAE {mae:F4} exceeds .04");
if (args.Length > 1)
{
    using var dataset = JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
    var rows = dataset.RootElement.GetProperty("rows").EnumerateArray().Select(row =>
        (X: row.GetProperty("features").EnumerateArray().Select(v => v.GetDouble()).ToArray(),
         Target: row.GetProperty("target").GetDouble(), Split: row.GetProperty("split").GetString())).ToArray();
    var train = rows.Where(r => r.Split == "train").ToArray();
    var validationRows = rows.Where(r => r.Split == "validation").ToArray();
    if (train.Length != 128 || validationRows.Length != 32 || rows.Any(r => r.X.Length != 8 || !double.IsFinite(r.Target) || r.Target < 0 || r.Target > 1))
        throw new InvalidDataException("Invalid teacher distillation dataset");
    for (var epoch = 0; epoch < 450; epoch++)
    foreach (var row in train)
        NetworkBrain.Train(model, row.X, .75 * row.Target + .25 * NetworkBrain.Teacher(row.X), .018);
    var teacherMae = validationRows.Average(r => Math.Abs(NetworkBrain.Predict(model, r.X) - r.Target));
    Console.WriteLine($"DeepSeek-v4-flash teacher distillation: 128 training / 32 held-out; held-out MAE={teacherMae:F4} (synthetic utility, not real-world accuracy)");
    if (teacherMae > .25) throw new InvalidOperationException("Distilled model does not fit teacher labels sufficiently");
    model.Provenance = "deepseek-v4-flash-synthetic-utility-distillation-v1; expert-anchor-25pct";
}
var path = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(path)!);
await File.WriteAllTextAsync(path, NetworkGuardianJson.Serialize(model));
Console.WriteLine($"8-8-1 utility model; {model.TrainingSteps} total steps; expert pretraining MAE before distillation={mae:F4}; {path}");
