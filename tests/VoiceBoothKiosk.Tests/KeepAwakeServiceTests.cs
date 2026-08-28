using System.Collections.Concurrent;
using System.ComponentModel;
using VoiceBoothKiosk.Application;

namespace VoiceBoothKiosk.Tests;

public sealed class KeepAwakeServiceTests
{
    private static readonly ExecutionStateRequest AwakeRequest =
        ExecutionStateRequest.Continuous |
        ExecutionStateRequest.SystemRequired |
        ExecutionStateRequest.DisplayRequired;

    [Fact]
    public void Enabling_and_disabling_requests_the_expected_indefinite_execution_states()
    {
        var api = new RecordingExecutionStateApi();
        using var service = new KeepAwakeService(api);

        service.SetEnabled(true);
        service.SetEnabled(true);
        service.SetEnabled(false);

        Assert.Equal(new KeepAwakeState(false, false, null), service.CurrentState);
        Assert.Equal([AwakeRequest, ExecutionStateRequest.Continuous], api.Requests);
    }

    [Fact]
    public void Failed_enable_records_the_desired_state_and_can_be_retried()
    {
        var api = new RecordingExecutionStateApi(0, Success);
        using var service = new KeepAwakeService(api);

        var exception = Assert.Throws<Win32Exception>(() => service.SetEnabled(true));

        Assert.Contains("keep-awake", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(service.CurrentState.IsRequested);
        Assert.False(service.CurrentState.IsActive);
        Assert.NotNull(service.CurrentState.LastError);

        service.Reassert();

        Assert.Equal(new KeepAwakeState(true, true, null), service.CurrentState);
    }

    [Fact]
    public void Failed_disable_preserves_the_effective_state_until_retry_succeeds()
    {
        var api = new RecordingExecutionStateApi(Success, 0, Success);
        using var service = new KeepAwakeService(api);
        service.SetEnabled(true);

        Assert.Throws<Win32Exception>(() => service.SetEnabled(false));
        Assert.False(service.CurrentState.IsRequested);
        Assert.True(service.CurrentState.IsActive);
        Assert.NotNull(service.CurrentState.LastError);

        service.Reassert();

        Assert.Equal(new KeepAwakeState(false, false, null), service.CurrentState);
    }

    [Fact]
    public void Reassert_reissues_the_awake_request_after_a_resume_notification()
    {
        var api = new RecordingExecutionStateApi();
        using var service = new KeepAwakeService(api);
        service.SetEnabled(true);

        service.Reassert();

        Assert.Equal([AwakeRequest, AwakeRequest], api.Requests);
    }

    [Fact]
    public void Periodic_heartbeat_reasserts_an_enabled_request()
    {
        var api = new RecordingExecutionStateApi();
        using var service = new KeepAwakeService(api, TimeSpan.FromMilliseconds(20));
        service.SetEnabled(true);

        Assert.True(SpinWait.SpinUntil(() => api.Requests.Count >= 2, TimeSpan.FromSeconds(2)));
        Assert.All(api.Requests, request => Assert.Equal(AwakeRequest, request));
    }

    [Fact]
    public async Task Calls_from_multiple_threads_are_serialized_onto_one_owner_thread()
    {
        var api = new RecordingExecutionStateApi();
        using var service = new KeepAwakeService(api);
        service.SetEnabled(true);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(service.Reassert)));
        service.SetEnabled(false);

        Assert.Single(api.ThreadIds.Distinct());
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, api.ThreadIds);
    }

    [Fact]
    public void Disposal_releases_on_the_owner_thread_and_tolerates_release_failure()
    {
        var api = new RecordingExecutionStateApi(Success, 0);
        var service = new KeepAwakeService(api);
        service.SetEnabled(true);

        service.Dispose();
        service.Dispose();

        Assert.Equal(ExecutionStateRequest.Continuous, api.Requests[^1]);
        Assert.Single(api.ThreadIds.Distinct());
        Assert.Equal(new KeepAwakeState(false, false, null), service.CurrentState);
    }

    [Fact]
    public void Calls_after_disposal_fail_deterministically()
    {
        var service = new KeepAwakeService(new RecordingExecutionStateApi());
        service.Dispose();

        Assert.Throws<ObjectDisposedException>(() => service.SetEnabled(true));
        Assert.Throws<ObjectDisposedException>(service.Reassert);
    }

    [Fact]
    public void State_notifications_report_failure_and_recovery()
    {
        var api = new RecordingExecutionStateApi(0, Success);
        using var service = new KeepAwakeService(api);
        var observed = new ConcurrentQueue<KeepAwakeState>();
        service.StateChanged += (_, args) => observed.Enqueue(args.State);

        Assert.Throws<Win32Exception>(() => service.SetEnabled(true));
        service.Reassert();

        Assert.Contains(observed, state => state.IsRequested && !state.IsActive && state.LastError is not null);
        Assert.Contains(observed, state => state.IsRequested && state.IsActive && state.LastError is null);
    }

    [Fact]
    public void Windows_execution_state_api_accepts_enable_reassert_and_release_requests()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var service = new KeepAwakeService();

        service.SetEnabled(true);
        service.Reassert();
        Assert.True(service.IsEnabled);
        service.SetEnabled(false);
        Assert.False(service.IsEnabled);
    }

    private const uint Success = (uint)ExecutionStateRequest.Continuous;

    private sealed class RecordingExecutionStateApi(params uint[] results) : IExecutionStateApi
    {
        private readonly ConcurrentQueue<uint> _results = new(results);
        private readonly ConcurrentQueue<ExecutionStateRequest> _requests = new();
        private readonly ConcurrentQueue<int> _threadIds = new();

        public IReadOnlyList<ExecutionStateRequest> Requests => _requests.ToArray();
        public IReadOnlyList<int> ThreadIds => _threadIds.ToArray();

        public uint SetThreadExecutionState(ExecutionStateRequest request)
        {
            _requests.Enqueue(request);
            _threadIds.Enqueue(Environment.CurrentManagedThreadId);
            return _results.TryDequeue(out var result) ? result : Success;
        }
    }
}
