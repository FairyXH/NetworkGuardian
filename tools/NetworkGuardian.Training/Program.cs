using NetworkGuardian.Core.Policies;
using NetworkGuardian.Core.Serialization;

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
var path = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(path)!);
await File.WriteAllTextAsync(path, NetworkGuardianJson.Serialize(model));
Console.WriteLine($"8-8-1 utility model; synthetic expert teacher; {model.TrainingSteps} steps; held-out teacher MAE={mae:F4}; {path}");
