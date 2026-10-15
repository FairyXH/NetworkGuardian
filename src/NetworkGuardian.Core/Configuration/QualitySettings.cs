namespace NetworkGuardian.Core.Configuration;

public sealed class QualitySettings
{
    public bool Enabled { get; set; } = true;
    public bool RebalanceEnabled { get; set; } = true;
    public int SampleIntervalSeconds { get; set; } = 30;
    public int RebalanceCooldownSeconds { get; set; } = 600;
    public int MinimumSamples { get; set; } = 10;
    public int MinimumGain { get; set; } = 20;
}
