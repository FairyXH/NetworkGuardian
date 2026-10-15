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
    public NetworkHistoryStore(string path) => _path = path;
    public IReadOnlyList<LinkQualityEstimate> Snapshot => Volatile.Read(ref _snapshot);
    public string? LoadIssue { get; private set; }
    public PreferenceState Preferences => Volatile.Read(ref _preferences);

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
        lock (_gate) { _state.Preferences = new(); PublishPreferences(); }
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
        if (!File.Exists(_path)) return;
        foreach (var path in new[] { _path, _path + ".bak" })
        {
            try
            {
                var state = NetworkGuardianJson.Deserialize<NetworkHistory>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
                if (state?.Links is null || state.Links.Any(l => l.Samples is null)) throw new InvalidDataException("invalid history");
                lock (_gate)
                {
                    _state = state;
                    _state.Preferences ??= new();
                    if (_state.Preferences.Weights is null || _state.Preferences.Feedback is null || _state.Preferences.QuestionContexts is null)
                        _state.Preferences = new();
                    PublishPreferences();
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
    }

    public void Record(Guid adapter, string ssid, NetworkBand band, string? deviceId, LinkQualitySample sample)
    {
        lock (_gate)
        {
            var link = _state.Links.FirstOrDefault(l => l.Adapter == adapter && l.Ssid == ssid && l.Band == band);
            if (link is null)
            {
                link = new() { Adapter = adapter, Ssid = ssid, Band = band, DeviceInstanceId = deviceId };
                _state.Links.Add(link);
            }
            if (link.Samples.Count > 0 && sample.AtUtc <= link.Samples[^1].AtUtc) return;
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
            lock (_gate) json = NetworkGuardianJson.Serialize(_state);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.WriteAllTextAsync(_path + ".tmp", json, token).ConfigureAwait(false);
            if (File.Exists(_path)) File.Replace(_path + ".tmp", _path, _path + ".bak");
            else File.Move(_path + ".tmp", _path);
        }
        finally { _writeGate.Release(); }
    }
}
