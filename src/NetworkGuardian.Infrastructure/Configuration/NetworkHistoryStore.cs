using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using NetworkGuardian.Core.Serialization;

namespace NetworkGuardian.Infrastructure.Configuration;

/// <summary>Bounded local history; snapshots are immutable and safe for the UI and policy threads.</summary>
public sealed class NetworkHistoryStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private NetworkHistory _state = new();
    private IReadOnlyList<LinkQualityEstimate> _snapshot = Array.Empty<LinkQualityEstimate>();
    private PreferenceState _preferences = new();
    private SurveyState _survey = new();
    private NetworkBrainModel _brain = NetworkBrain.Seed();
    private long _savedSurveyRevision = -1;
    public NetworkHistoryStore(string path) { _path = path; _state.Brain = _brain; }
    public IReadOnlyList<LinkQualityEstimate> Snapshot => Volatile.Read(ref _snapshot);
    public string? LoadIssue { get; private set; }
    public PreferenceState Preferences => Volatile.Read(ref _preferences);
    public SurveyState Survey => Volatile.Read(ref _survey);
    public NetworkBrainModel Brain => Volatile.Read(ref _brain);
    public string BrainPath => Path.Combine(Path.GetDirectoryName(_path)!, "network-brain.json");
    public string SurveyPath => Path.Combine(Path.GetDirectoryName(_path)!, "network-survey.json");

    public async Task ReplaceBrainAsync(string expectedHash, NetworkBrainModel replacement)
    {
        if (!NetworkBrain.Valid(replacement)) throw new ArgumentException("Invalid brain model");
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (BrainDistillation.Hash(_state.Brain!) != expectedHash)
                    throw new InvalidOperationException("当前模型已变化，请基于最新模型创建训练任务；未覆盖新学习数据。");
                var previous = _state.Brain;
                _state.Brain = BrainDistillation.Clone(replacement);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    File.WriteAllText(_path + ".tmp", NetworkGuardianJson.Serialize(_state));
                    if (File.Exists(_path)) File.Replace(_path + ".tmp", _path, _path + ".bak");
                    else File.Move(_path + ".tmp", _path);
                }
                catch { _state.Brain = previous; throw; }
                PublishLearning();
            }
        }
        finally { _writeGate.Release(); }
        // network-history.json is authoritative; the cache is refreshed by the next regular save.
    }

    private void PublishLearning()
    {
        Volatile.Write(ref _survey, new SurveyState { Answers = _state.Survey.Answers.ToList(),
            SoftLabels = new(_state.Survey.SoftLabels), TotalAnswers = _state.Survey.TotalAnswers, Revision = _state.Survey.Revision });
        Volatile.Write(ref _brain, NetworkGuardianJson.Deserialize<NetworkBrainModel>(NetworkGuardianJson.Serialize(_state.Brain!))!);
        PublishPreferences();
    }

    public void RecordAnswer(SurveyAnswer answer)
    {
        lock (_gate)
        {
            if (_state.Survey.Answers.Any(a => a.Question.Id == answer.Question.Id)) return;
            SurveyLearning.Record(_state, answer);
            _state.Survey.Revision++;
            _state.SurveyRevision = _state.Survey.Revision;
            PublishLearning();
        }
    }

    public void RemoveAnswer(string id)
    {
        lock (_gate)
        {
            var retained = _state.Survey.Answers.Where(a => a.Question.Id != id).ToArray();
            var revision = _state.Survey.Revision + 1;
            RebuildSurvey(new() { Revision = revision, Answers = retained.ToList() });
            PublishLearning();
        }
    }

    public string ExportAnswers() { lock (_gate) return NetworkGuardianJson.Serialize(_state.Survey); }

    private void RebuildSurvey(SurveyState dataset)
    {
        var manual = _state.Preferences.Feedback.Where(f => f.Source != "survey").ToArray();
        var answers = dataset.Answers.ToArray();
        _state.Preferences = new(); _state.Survey = new() { Revision = dataset.Revision }; _state.Brain = NetworkBrain.Seed();
        foreach (var feedback in manual) PreferencePolicy.Train(_state.Preferences, feedback);
        foreach (var answer in answers) SurveyLearning.Record(_state, answer);
        _state.SurveyRevision = dataset.Revision;
    }

    public IReadOnlyList<SuppressedWifiProfile> SuppressedProfiles
    {
        get { lock (_gate) return _state.SuppressedProfiles.ToArray(); }
    }

    public void SetSuppressedProfile(SuppressedWifiProfile profile, bool suppressed)
    {
        lock (_gate)
        {
            _state.SuppressedProfiles.RemoveAll(p => p == profile);
            if (suppressed) _state.SuppressedProfiles.Add(profile);
        }
    }

    public void RecordPreference(PreferenceFeedback feedback)
    {
        lock (_gate)
        {
            PreferencePolicy.Train(_state.Preferences, feedback);
            PublishPreferences();
        }
    }

    public void MarkQuestion(string context, DateTimeOffset now)
    {
        lock (_gate)
        {
            _state.Preferences.LastQuestionUtc = now;
            _state.Preferences.QuestionContexts[context] = now;
            _state.Preferences.QuestionContexts = _state.Preferences.QuestionContexts
                .OrderByDescending(p => p.Value).Take(128).ToDictionary(p => p.Key, p => p.Value);
            PublishPreferences();
        }
    }

    public void ResetPreferences()
    {
        lock (_gate)
        {
            var revision = _state.Survey.Revision + 1;
            _state.Preferences = new(); _state.Survey = new() { Revision = revision }; _state.SurveyRevision = revision;
            _state.Brain = NetworkBrain.Seed(); PublishLearning();
        }
    }

    public void RemovePreference(DateTimeOffset at)
    {
        lock (_gate)
        {
            var retained = _state.Preferences.Feedback.Where(f => f.AtUtc != at).ToArray();
            var rebuilt = new PreferenceState
            {
                LastQuestionUtc = _state.Preferences.LastQuestionUtc,
                QuestionContexts = _state.Preferences.QuestionContexts,
            };
            foreach (var feedback in retained) PreferencePolicy.Train(rebuilt, feedback);
            _state.Preferences = rebuilt;
            PublishPreferences();
        }
    }

    public string ExportPreferences()
    {
        lock (_gate) return NetworkGuardianJson.Serialize(_state.Preferences);
    }

    private void PublishPreferences() => Volatile.Write(ref _preferences,
        NetworkGuardianJson.Deserialize<PreferenceState>(NetworkGuardianJson.Serialize(_state.Preferences))!);

    public async Task LoadAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (!File.Exists(BrainPath))
            await File.WriteAllTextAsync(BrainPath, NetworkGuardianJson.Serialize(_brain), token).ConfigureAwait(false);
        var dataset = await LoadSurveyAsync(token).ConfigureAwait(false);
        if (!File.Exists(_path))
        {
            lock (_gate) { RebuildSurvey(dataset); PublishLearning(); }
            return;
        }
        foreach (var path in new[] { _path, _path + ".bak" })
        {
            try
            {
                var state = NetworkGuardianJson.Deserialize<NetworkHistory>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
                if (state?.Links is null || state.Links.Any(l => l.Samples is null)) throw new InvalidDataException("invalid history");
                lock (_gate)
                {
                    _state = state;
                    _state.Survey = dataset;
                    if (!NetworkBrain.Valid(_state.Brain)) _state.Brain = NetworkBrain.Seed();
                    _state.SuppressedProfiles ??= new();
                    _state.Preferences ??= new();
                    if (_state.Preferences.Weights is null || _state.Preferences.Feedback is null || _state.Preferences.QuestionContexts is null)
                        _state.Preferences = new();
                    if (_state.SurveyRevision != dataset.Revision) RebuildSurvey(dataset);
                    PublishLearning();
                    Trim();
                    Publish(DateTimeOffset.UtcNow);
                }
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LoadIssue = $"质量历史读取失败：{ex.Message}；尝试备份或重新学习";
            }
        }
        lock (_gate)
        {
            _state = new();
            _state.Brain = NetworkBrain.Seed();
            RebuildSurvey(dataset);
            Publish(DateTimeOffset.UtcNow);
            PublishLearning();
        }
    }

    public void Record(Guid adapter, string ssid, NetworkBand band, string? deviceId, LinkQualitySample sample)
        => Record(new(adapter, ssid, band, InterfaceKind.Wifi, "", "", ssid, deviceId), sample);

    public void Record(NetworkQualityIdentity identity, LinkQualitySample sample)
    {
        lock (_gate)
        {
            var link = _state.Links.FirstOrDefault(l => l.Adapter == identity.Adapter && l.Ssid == identity.Ssid &&
                l.Band == identity.Band && l.Kind == identity.Kind && l.NetworkId == identity.NetworkId);
            if (link is null)
            {
                link = new() { Adapter = identity.Adapter, Ssid = identity.Ssid, Band = identity.Band,
                    DeviceInstanceId = identity.DeviceInstanceId, Kind = identity.Kind, InterfaceId = identity.InterfaceId,
                    NetworkId = identity.NetworkId, NetworkName = identity.NetworkName };
                _state.Links.Add(link);
            }
            if (link.Samples.Count > 0 && sample.AtUtc <= link.Samples[^1].AtUtc) return;
            link.InterfaceId = identity.InterfaceId;
            link.NetworkName = identity.NetworkName;
            link.Samples.Add(sample);
            link.TotalSamples++;
            var current = NetworkQualityPolicy.Estimate(link, sample.AtUtc);
            link.LongTermScore = link.TotalSamples == 1 ? current.Score : link.LongTermScore * .98 + current.Score * .02;
            Trim();
            Publish(sample.AtUtc);
        }
    }

    private void Trim()
    {
        foreach (var link in _state.Links)
            if (link.Samples.Count > 120) link.Samples.RemoveRange(0, link.Samples.Count - 120);
        _state.Links = _state.Links.OrderByDescending(l => l.Samples.LastOrDefault()?.AtUtc).Take(256).ToList();
    }

    private void Publish(DateTimeOffset now) => Volatile.Write(ref _snapshot,
        _state.Links.Select(l => NetworkQualityPolicy.Estimate(l, l.Samples.LastOrDefault()?.AtUtc ?? now)).ToArray());

    public async Task SaveAsync(CancellationToken token)
    {
        await _writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string json;
            string brain;
            string? survey;
            long surveyRevision;
            lock (_gate)
            {
                json = NetworkGuardianJson.Serialize(_state);
                brain = NetworkGuardianJson.Serialize(_state.Brain!);
                surveyRevision = _state.Survey.Revision;
                survey = surveyRevision != _savedSurveyRevision ? NetworkGuardianJson.Serialize(_state.Survey) : null;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (survey is not null)
            {
                await WriteAtomicAsync(SurveyPath, survey, token).ConfigureAwait(false);
                _savedSurveyRevision = surveyRevision;
            }
            await File.WriteAllTextAsync(_path + ".tmp", json, token).ConfigureAwait(false);
            if (File.Exists(_path)) File.Replace(_path + ".tmp", _path, _path + ".bak");
            else File.Move(_path + ".tmp", _path);
            await File.WriteAllTextAsync(BrainPath + ".tmp", brain, token).ConfigureAwait(false);
            if (File.Exists(BrainPath)) File.Replace(BrainPath + ".tmp", BrainPath, BrainPath + ".bak");
            else File.Move(BrainPath + ".tmp", BrainPath);
        }
        finally { _writeGate.Release(); }
    }

    private static async Task WriteAtomicAsync(string path, string json, CancellationToken token)
    {
        await File.WriteAllTextAsync(path + ".tmp", json, token).ConfigureAwait(false);
        if (File.Exists(path)) File.Replace(path + ".tmp", path, path + ".bak");
        else File.Move(path + ".tmp", path);
    }

    private async Task<SurveyState> LoadSurveyAsync(CancellationToken token)
    {
        foreach (var path in new[] { SurveyPath, SurveyPath + ".bak" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var dataset = NetworkGuardianJson.Deserialize<SurveyState>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
                if (dataset?.Answers is null || dataset.SoftLabels is null) throw new InvalidDataException("Invalid survey dataset");
                dataset.Answers = dataset.Answers.TakeLast(1000).ToList();
                if (path == SurveyPath) _savedSurveyRevision = dataset.Revision;
                return dataset;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { LoadIssue = $"答题集读取失败，尝试备份：{ex.Message}"; }
        }
        return new();
    }
}
