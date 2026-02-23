using System;
using System.Configuration;

namespace CheerfulGiverNXT.Infrastructure.AppMode;

public enum AppMode
{
    Live = 0,
    Demo = 1
}

/// <summary>
/// Global application mode (Live/Demo) used to hard-guard SKY API pledge posting.
/// In-memory only by default (resets to Live on app restart).
/// </summary>
public sealed class AppModeState
{
    private readonly object _gate = new();

    public static AppModeState Instance { get; } = new();

    private AppMode _mode = AppMode.Live;

    private AppModeState() { }

    public AppMode Mode
    {
        get { lock (_gate) return _mode; }
        private set
        {
            bool changed;
            lock (_gate)
            {
                changed = _mode != value;
                _mode = value;
            }

            if (changed)
                ModeChanged?.Invoke(this, value);
        }
    }

    public bool IsDemo => Mode == AppMode.Demo;

    public event EventHandler<AppMode>? ModeChanged;

    public void Toggle() => Set(IsDemo ? AppMode.Live : AppMode.Demo);

    public void Set(AppMode mode) => Mode = mode;
}

/// <summary>
/// Centralized posting guard so both UI and service layers can fail-closed.
/// </summary>
public static class SkyPostingPolicy
{
    public static bool IsPostingAllowed(out string? reason)
    {
        if (AppModeState.Instance.IsDemo)
        {
            reason = "Demo mode is enabled.";
            return false;
        }

        // Optional kill switch for dedicated training installs.
        var raw = ConfigurationManager.AppSettings["AllowSkyPosting"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            reason = null;
            return true;
        }

        if (bool.TryParse(raw.Trim(), out var allow))
        {
            if (!allow)
            {
                reason = "SKY API posting is disabled by App.config (AllowSkyPosting=false).";
                return false;
            }

            reason = null;
            return true;
        }

        // If misconfigured, fail open to preserve existing behavior.
        reason = null;
        return true;
    }
}
