using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CheerfulGiverNXT.Services;

/// <summary>
/// Exposes runtime status of the outbox queue processor for display in the Audit window.
/// </summary>
public sealed class OutboxQueueStatus : INotifyPropertyChanged
{
    public static OutboxQueueStatus Instance { get; } = new();

    private bool _isRunning;
    private DateTime? _lastRunUtc;
    private string? _lastMessage;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
        }
    }

    public DateTime? LastRunUtc
    {
        get => _lastRunUtc;
        set
        {
            if (_lastRunUtc == value) return;
            _lastRunUtc = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
        }
    }

    public string? LastMessage
    {
        get => _lastMessage;
        set
        {
            if (string.Equals(_lastMessage, value, StringComparison.Ordinal)) return;
            _lastMessage = value;
            OnPropertyChanged();
        }
    }

    public string Display
    {
        get
        {
            if (!IsRunning) return "Outbox: Stopped";
            if (LastRunUtc is null) return "Outbox: Running";
            return $"Outbox: Running (Last: {LastRunUtc:yyyy-MM-dd HH:mm:ss} UTC)";
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
