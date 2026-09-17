using System.Collections.ObjectModel;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.App.ViewModels;

/// <summary>Editable wrapper around <see cref="CommandDefinition"/> for the settings list.</summary>
public sealed class CommandDefinitionViewModel : ObservableObject
{
    private readonly CommandDefinition _model;

    public CommandDefinitionViewModel(CommandDefinition model)
    {
        _model = model;
    }

    public CommandDefinition Model => _model;

    public string Id => _model.Id;

    public string Name
    {
        get => _model.Name;
        set
        {
            if (_model.Name != value)
            {
                _model.Name = value;
                Raise();
            }
        }
    }

    public bool Enabled
    {
        get => _model.Enabled;
        set
        {
            if (_model.Enabled != value)
            {
                _model.Enabled = value;
                Raise();
            }
        }
    }

    public int KindIndex
    {
        get => (int)_model.Kind;
        set
        {
            if ((int)_model.Kind != value)
            {
                _model.Kind = (CommandKind)value;
                Raise();
                Raise(nameof(KindHint));
            }
        }
    }

    public string KindHint => _model.Kind == CommandKind.Shell
        ? "命令行模式：直接填写完整命令行，例如 curl http://10.0.0.1/login"
        : "程序模式：填写可执行文件路径与参数";

    public string ExecutablePath
    {
        get => _model.ExecutablePath;
        set
        {
            if (_model.ExecutablePath != value)
            {
                _model.ExecutablePath = value;
                Raise();
            }
        }
    }

    public string Arguments
    {
        get => _model.Arguments;
        set
        {
            if (_model.Arguments != value)
            {
                _model.Arguments = value;
                Raise();
            }
        }
    }

    public string WorkingDirectory
    {
        get => _model.WorkingDirectory ?? string.Empty;
        set
        {
            if (_model.WorkingDirectory != value)
            {
                _model.WorkingDirectory = value;
                Raise();
            }
        }
    }

    public bool RunAsAdministrator
    {
        get => _model.RunAsAdministrator;
        set
        {
            if (_model.RunAsAdministrator != value)
            {
                _model.RunAsAdministrator = value;
                Raise();
            }
        }
    }

    public double ExecutionTimeoutSeconds
    {
        get => _model.ExecutionTimeoutSeconds;
        set
        {
            var rounded = (int)value;
            if (_model.ExecutionTimeoutSeconds != rounded)
            {
                _model.ExecutionTimeoutSeconds = rounded;
                Raise();
            }
        }
    }

    public double MinIntervalSeconds
    {
        get => _model.MinIntervalSeconds;
        set
        {
            var rounded = (int)value;
            if (_model.MinIntervalSeconds != rounded)
            {
                _model.MinIntervalSeconds = rounded;
                Raise();
            }
        }
    }

    public double MaxRunsPerHour
    {
        get => _model.MaxRunsPerHour;
        set
        {
            var rounded = (int)value;
            if (_model.MaxRunsPerHour != rounded)
            {
                _model.MaxRunsPerHour = rounded;
                Raise();
            }
        }
    }

    public double MaxConsecutiveRuns
    {
        get => _model.MaxConsecutiveRuns;
        set
        {
            var rounded = (int)value;
            if (_model.MaxConsecutiveRuns != rounded)
            {
                _model.MaxConsecutiveRuns = rounded;
                Raise();
            }
        }
    }

    public bool SkipIfAlreadyRunning
    {
        get => _model.SkipIfAlreadyRunning;
        set
        {
            if (_model.SkipIfAlreadyRunning != value)
            {
                _model.SkipIfAlreadyRunning = value;
                Raise();
            }
        }
    }

    public bool KillOnTimeout
    {
        get => _model.KillOnTimeout;
        set
        {
            if (_model.KillOnTimeout != value)
            {
                _model.KillOnTimeout = value;
                Raise();
            }
        }
    }

    public bool WaitForExit
    {
        get => _model.WaitForExit;
        set
        {
            if (_model.WaitForExit != value)
            {
                _model.WaitForExit = value;
                Raise();
            }
        }
    }

    public double WaitAfterRunSeconds
    {
        get => _model.WaitAfterRunSeconds;
        set
        {
            var rounded = (int)value;
            if (_model.WaitAfterRunSeconds != rounded)
            {
                _model.WaitAfterRunSeconds = rounded;
                Raise();
            }
        }
    }

    public string Summary =>
        $"{Name}｜{(KindIndex == 1 ? "命令行" : "程序")}｜{(Enabled ? "启用" : "停用")}｜" +
        $"{Path.GetFileName(ExecutablePath)}";
}

/// <summary>Editable wrapper around a probe endpoint.</summary>
public sealed class ProbeEndpointViewModel : ObservableObject
{
    private readonly ProbeEndpointSettings _model;

    public ProbeEndpointViewModel(ProbeEndpointSettings model)
    {
        _model = model;
    }

    public string Name
    {
        get => _model.Name;
        set
        {
            if (_model.Name != value)
            {
                _model.Name = value;
                Raise();
            }
        }
    }

    public bool Enabled
    {
        get => _model.Enabled;
        set
        {
            if (_model.Enabled != value)
            {
                _model.Enabled = value;
                Raise();
            }
        }
    }

    public int KindIndex
    {
        get => (int)_model.Kind;
        set
        {
            if ((int)_model.Kind != value)
            {
                _model.Kind = (ProbeKind)value;
                Raise();
            }
        }
    }

    public string Target
    {
        get => _model.Target;
        set
        {
            if (_model.Target != value)
            {
                _model.Target = value;
                Raise();
            }
        }
    }

    public double TimeoutMs
    {
        get => _model.TimeoutMs ?? 2500;
        set
        {
            var rounded = (int)value;
            if (_model.TimeoutMs != rounded)
            {
                _model.TimeoutMs = rounded;
                Raise();
            }
        }
    }

    public string BodyMarker
    {
        get => _model.BodyMarker ?? string.Empty;
        set
        {
            if (_model.BodyMarker != value)
            {
                _model.BodyMarker = string.IsNullOrWhiteSpace(value) ? null : value;
                Raise();
            }
        }
    }
}
