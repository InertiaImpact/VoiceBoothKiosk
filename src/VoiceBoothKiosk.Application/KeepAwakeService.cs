using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VoiceBoothKiosk.Application;

/// <summary>
/// Windows execution-state flags used to keep a running kiosk from idling its display or system.
/// </summary>
[Flags]
public enum ExecutionStateRequest : uint
{
    SystemRequired = 0x00000001,
    DisplayRequired = 0x00000002,
    Continuous = 0x80000000
}

/// <summary>Abstraction around the Windows execution-state API for deterministic testing.</summary>
public interface IExecutionStateApi
{
    uint SetThreadExecutionState(ExecutionStateRequest request);
}

/// <summary>Calls the supported Windows API used by tools such as PowerToys Awake.</summary>
public sealed class WindowsExecutionStateApi : IExecutionStateApi
{
    public uint SetThreadExecutionState(ExecutionStateRequest request) =>
        NativeSetThreadExecutionState(request);

    [DllImport("kernel32.dll", EntryPoint = "SetThreadExecutionState", ExactSpelling = true, SetLastError = true)]
    private static extern uint NativeSetThreadExecutionState(ExecutionStateRequest request);
}

/// <summary>The requested and effective state of the keep-awake worker.</summary>
public sealed record KeepAwakeState(bool IsRequested, bool IsActive, string? LastError);

public sealed class KeepAwakeStateChangedEventArgs : EventArgs
{
    public required KeepAwakeState State { get; init; }
}

/// <summary>
/// Maintains an indefinite Windows display-and-system-awake request on one dedicated thread.
/// SetThreadExecutionState is thread-scoped, so every enable, refresh, and release is serialized
/// through that worker regardless of which application thread calls this service.
/// </summary>
public sealed class KeepAwakeService : IDisposable
{
    private static readonly TimeSpan DefaultReassertInterval = TimeSpan.FromMinutes(1);
    private static readonly ExecutionStateRequest AwakeRequest =
        ExecutionStateRequest.Continuous |
        ExecutionStateRequest.SystemRequired |
        ExecutionStateRequest.DisplayRequired;

    private readonly IExecutionStateApi _executionState;
    private readonly TimeSpan _reassertInterval;
    private readonly BlockingCollection<WorkerCommand> _commands = [];
    private readonly Thread _worker;
    private readonly object _lifecycleGate = new();
    private readonly object _stateGate = new();
    private KeepAwakeState _state = new(false, false, null);
    private bool _disposeStarted;

    public KeepAwakeService(
        IExecutionStateApi? executionState = null,
        TimeSpan? reassertInterval = null)
    {
        _executionState = executionState ?? new WindowsExecutionStateApi();
        _reassertInterval = reassertInterval ?? DefaultReassertInterval;
        if (_reassertInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(reassertInterval), "The reassert interval must be positive.");
        }

        _worker = new Thread(RunWorker)
        {
            IsBackground = true,
            Name = "Voice Booth keep-awake"
        };
        _worker.Start();
    }

    public event EventHandler<KeepAwakeStateChangedEventArgs>? StateChanged;

    public KeepAwakeState CurrentState
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public bool IsEnabled => CurrentState.IsActive;

    /// <summary>
    /// Enables or disables the indefinite request. This returns only after Windows has accepted
    /// the change, and it may safely be called from any thread.
    /// </summary>
    public void SetEnabled(bool enabled) => Execute(WorkerCommandKind.SetEnabled, enabled);

    /// <summary>
    /// Reasserts the desired state after resume or a power-policy change. A periodic heartbeat
    /// also reasserts it so a missed Windows notification cannot silently disable the policy.
    /// </summary>
    public void Reassert() => Execute(WorkerCommandKind.Reassert, null);

    public void Dispose()
    {
        Task completion;
        lock (_lifecycleGate)
        {
            if (_disposeStarted)
            {
                return;
            }

            _disposeStarted = true;
            var command = new WorkerCommand(WorkerCommandKind.Shutdown, null);
            _commands.Add(command);
            completion = command.Completion.Task;
        }

        // The worker owns the execution-state request. Let it clear the request and terminate;
        // thread termination is the Windows backstop if an explicit release is rejected.
        completion.GetAwaiter().GetResult();
        _worker.Join();
        _commands.Dispose();
    }

    private void Execute(WorkerCommandKind kind, bool? enabled)
    {
        Task completion;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            var command = new WorkerCommand(kind, enabled);
            _commands.Add(command);
            completion = command.Completion.Task;
        }

        completion.GetAwaiter().GetResult();
    }

    private void RunWorker()
    {
        while (true)
        {
            if (!_commands.TryTake(out var command, _reassertInterval))
            {
                TryApplyDesiredState();
                continue;
            }

            if (command.Kind == WorkerCommandKind.Shutdown)
            {
                ShutdownWorker();
                command.Completion.TrySetResult();
                return;
            }

            try
            {
                switch (command.Kind)
                {
                    case WorkerCommandKind.SetEnabled:
                        ApplySetting(command.Enabled!.Value);
                        break;
                    case WorkerCommandKind.Reassert:
                        ApplyDesiredState();
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported keep-awake command: {command.Kind}.");
                }
                command.Completion.TrySetResult();
            }
            catch (Exception exception)
            {
                command.Completion.TrySetException(exception);
            }
        }
    }

    private void ApplySetting(bool enabled)
    {
        var state = CurrentState;
        if (state.IsRequested == enabled && state.IsActive == enabled && state.LastError is null)
        {
            return;
        }

        UpdateState(state with { IsRequested = enabled });
        ApplyDesiredState();
    }

    private void ApplyDesiredState()
    {
        var state = CurrentState;
        try
        {
            Request(state.IsRequested ? AwakeRequest : ExecutionStateRequest.Continuous);
            UpdateState(new KeepAwakeState(state.IsRequested, state.IsRequested, null));
        }
        catch (Exception exception)
        {
            // Preserve the effective state when a transition fails. The desired state remains
            // recorded so the next heartbeat, resume notification, or explicit call retries it.
            UpdateState(state with { LastError = exception.Message });
            throw;
        }
    }

    private void TryApplyDesiredState()
    {
        var state = CurrentState;
        if (!state.IsRequested && !state.IsActive)
        {
            return;
        }

        try
        {
            ApplyDesiredState();
        }
        catch
        {
            // StateChanged reports the failure. The heartbeat keeps retrying without terminating
            // the long-lived owner thread.
        }
    }

    private void ShutdownWorker()
    {
        var state = CurrentState;
        if (state.IsRequested || state.IsActive)
        {
            try
            {
                Request(ExecutionStateRequest.Continuous);
            }
            catch
            {
                // Windows clears a thread execution-state request when this worker exits.
            }
        }

        UpdateState(new KeepAwakeState(false, false, null));
    }

    private void Request(ExecutionStateRequest request)
    {
        if (_executionState.SetThreadExecutionState(request) == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Windows could not apply the Voice Booth keep-awake request.");
        }
    }

    private void UpdateState(KeepAwakeState state)
    {
        lock (_stateGate)
        {
            if (_state == state)
            {
                return;
            }
            _state = state;
        }

        var handler = StateChanged;
        if (handler is null)
        {
            return;
        }

        var args = new KeepAwakeStateChangedEventArgs { State = state };
        foreach (EventHandler<KeepAwakeStateChangedEventArgs> subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch
            {
                // An observer cannot be allowed to terminate the thread holding the request.
            }
        }
    }

    private enum WorkerCommandKind
    {
        SetEnabled,
        Reassert,
        Shutdown
    }

    private sealed class WorkerCommand(WorkerCommandKind kind, bool? enabled)
    {
        public WorkerCommandKind Kind { get; } = kind;
        public bool? Enabled { get; } = enabled;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
