using System.Diagnostics;
using System.Numerics;

namespace FlugelKranz.Core;

public interface IFlightRuntime : IDisposable
{
    RigidPose OriginalOffset { get; }
    RigidPose CurrentOffset { get; }
    InputFrame ReadPhysical();
    void Apply(RigidPose offset);
    void Restore();
}

public interface IFlightInputControl
{
    void SetPilotInputEnabled(bool enabled);
}

public interface IReferenceSpaceOffsetProvider
{
    RigidPose ReferenceSpaceOffset { get; }
}

public interface IFlightInputDiagnostics
{
    string InputDiagnostics { get; }
}

public interface IFlightBindings
{
    void OpenBindings();
}

public sealed record FlightStatus(
    bool Enabled,
    bool Connected,
    string Message,
    bool Dragging = false,
    bool Turning = false,
    FlightMode Mode = FlightMode.InfiniteWalking,
    float? LeftTrackpadForce = null,
    float? RightTrackpadForce = null,
    RigidPose? ReferenceSpaceOffset = null,
    Vector3? RecentReferenceSpaceMovement = null,
    string? InputDiagnostics = null);

/// <summary>Owns the runtime on one worker. Flight off permits independently enabled Drag without inertia; reset restores the offset.</summary>
public sealed class FlightController(
    Func<IFlightRuntime> createRuntime,
    IProgress<FlightStatus> progress,
    Func<FlugelKranzSettings>? getSettings = null) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private Task? worker;
    private bool enabled, resetRequested, disposed;
    private bool bindingsRequested;
    private long releaseVersion;

    public void SetEnabled(bool value)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            enabled = value;
            releaseVersion++;
            if (value && (worker is null || worker.IsCompleted)) worker = Task.Run(RunAsync);
        }
    }

    public void RefreshDragConnection()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var settings = (getSettings?.Invoke() ?? FlugelKranzSettings.Default).Normalized();
            if (settings.Mode == FlightMode.FreeFlight && settings.FreeFlight.DragEnabled &&
                (worker is null || worker.IsCompleted)) worker = Task.Run(RunAsync);
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            releaseVersion++;
            resetRequested = true;
        }
    }

    public void OpenBindings()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            bindingsRequested = true;
            if (worker is null || worker.IsCompleted) worker = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        IFlightRuntime? runtime = null;
        string? error = null;
        FlightMode reportedMode = FlightMode.InfiniteWalking;
        try
        {
            reportedMode = (getSettings?.Invoke() ?? FlugelKranzSettings.Default).Normalized().Mode;
            lock (gate)
                progress.Report(new(enabled, false, "ランタイムに接続しています…", Mode: reportedMode));
            runtime = createRuntime();
            var session = new FlightSession(runtime.CurrentOffset);
            long previousTimestamp = Stopwatch.GetTimestamp();
            long observedVersion = -1;
            RigidPose? previousReferenceSpaceOffset = null;
            int tick = 0;
            while (!shutdown.IsCancellationRequested)
            {
                lock (gate)
                {
                    var inputSettings = (getSettings?.Invoke() ?? FlugelKranzSettings.Default).Normalized();
                    (runtime as IFlightInputControl)?.SetPilotInputEnabled(enabled &&
                        session.InputMode(inputSettings.Mode) == FlightMode.FreeFlight &&
                        inputSettings.FreeFlight.HeadPilotEnabled);
                }
                var frame = runtime.ReadPhysical();
                long timestamp = Stopwatch.GetTimestamp();
                float elapsedSeconds = (float)Stopwatch.GetElapsedTime(previousTimestamp, timestamp).TotalSeconds;
                previousTimestamp = timestamp;
                var settings = (getSettings?.Invoke() ?? FlugelKranzSettings.Default).Normalized();
                FlightGesture gesture = session.PrepareInput(frame, elapsedSeconds, settings.Mode);
                FlightMode selectedMode = session.SelectedMode;
                reportedMode = selectedMode;
                if (gesture == FlightGesture.ToggleMode)
                    progress.Report(new(enabled, true, FlightStatusFormatter.ModeChanged(selectedMode),
                        Mode: selectedMode));
                lock (gate)
                {
                    if (bindingsRequested)
                    {
                        bindingsRequested = false;
                        if (runtime is not IFlightBindings bindings)
                            throw new NotSupportedException("このランタイムはバインド設定に対応していません。");
                        bindings.OpenBindings();
                    }
                    if (observedVersion != releaseVersion)
                    {
                        session.ReleaseInputs(runtime.CurrentOffset);
                        observedVersion = releaseVersion;
                    }
                    if (resetRequested)
                    {
                        runtime.Restore();
                        session.ResetTo(runtime.CurrentOffset);
                        resetRequested = false;
                    }
                    var result = session.Step(frame, elapsedSeconds, settings, enabled,
                        runtime.CurrentOffset, runtime.OriginalOffset, gesture == FlightGesture.ResetSpace);
                    if (result.SpaceResetStarted)
                        progress.Report(new(enabled, true, FlightStatusFormatter.SpaceReset(selectedMode),
                            Mode: selectedMode));
                    // A failed Apply exits this worker; never reuse its advanced calculation state.
                    if (result.Offset is { } offset) runtime.Apply(offset);
                    if (tick++ % 10 == 0)
                    {
                        var referenceSpaceOffset = (runtime as IReferenceSpaceOffsetProvider)
                            ?.ReferenceSpaceOffset;
                        var recentReferenceSpaceMovement = referenceSpaceOffset is { } currentReferenceSpaceOffset
                            && previousReferenceSpaceOffset is { } previous
                            ? currentReferenceSpaceOffset.Position - previous.Position
                            : (Vector3?)null;
                        previousReferenceSpaceOffset = referenceSpaceOffset;
                        progress.Report(new(
                            enabled,
                            true,
                            FlightStatusFormatter.Format(result.State),
                            result.State.Dragging,
                            result.State.Turning,
                            selectedMode,
                            TrackpadForce(frame.Left),
                            TrackpadForce(frame.Right),
                            referenceSpaceOffset,
                            recentReferenceSpaceMovement,
                            (runtime as IFlightInputDiagnostics)?.InputDiagnostics));
                    }
                }
                await Task.Delay(10, shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception exception) { error = exception.Message; }
        finally
        {
            if (runtime is not null)
            {
                try { runtime.Restore(); }
                catch (Exception exception) { error = $"{error}\n復元に失敗しました: {exception.Message}".Trim(); }
                finally
                {
                    try { runtime.Dispose(); }
                    catch (Exception exception) { error = $"{error}\n切断処理に失敗しました: {exception.Message}".Trim(); }
                }
            }
            lock (gate)
            {
                enabled = false;
                resetRequested = false;
                bindingsRequested = false;
                worker = null;
                if (error is not null) Console.Error.WriteLine(error);
                progress.Report(new(
                    false,
                    false,
                    error ?? "終了しました。接続時の位置・姿勢へ復元しました。",
                    Mode: reportedMode));
            }
        }
    }

    private static float? TrackpadForce(HandSample hand) =>
        hand.TrackpadForceActive && float.IsFinite(hand.TrackpadForce)
            ? Math.Clamp(hand.TrackpadForce, 0, 1)
            : null;

    public async ValueTask DisposeAsync()
    {
        Task? pending;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            enabled = false;
            shutdown.Cancel();
            pending = worker;
        }
        if (pending is not null) await pending.ConfigureAwait(false);
        shutdown.Dispose();
    }
}
