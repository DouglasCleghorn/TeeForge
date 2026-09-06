// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Adapted for TeeForge from dotnet/runtime commit 4271d88e0aebf3d04f188f1334c2220d80555ef6.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace TeeForge.Broadcasting.Internal;

internal struct PipeAwaitable
{
    private AwaitableState _awaitableState;
    private Action<object?>? _completion;
    private object? _completionState;
    private SchedulingContext? _schedulingContext;
    private CancellationTokenRegistration _cancellationTokenRegistration;

    private CancellationToken CancellationToken => _cancellationTokenRegistration.Token;

    public PipeAwaitable(bool completed, bool useSynchronizationContext)
    {
        _awaitableState = (completed ? AwaitableState.Completed : AwaitableState.None)
            | (useSynchronizationContext ? AwaitableState.UseSynchronizationContext : AwaitableState.None);
    }

    public bool IsCompleted => (_awaitableState & (AwaitableState.Completed | AwaitableState.Canceled)) != 0;

    public bool IsRunning => (_awaitableState & AwaitableState.Running) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginOperation(Action<object?> callback, object? state, CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled && !IsCompleted)
        {
            AwaitableState previousState = _awaitableState;
            _cancellationTokenRegistration = cancellationToken.UnsafeRegister(callback, state);
            if (_cancellationTokenRegistration == default)
            {
                Debug.Assert(previousState == _awaitableState);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        _awaitableState |= AwaitableState.Running;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Complete(out CompletionData completionData)
    {
        ExtractCompletion(out completionData);
        _awaitableState |= AwaitableState.Completed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetUncompleted()
    {
        Debug.Assert(_completion is null);
        Debug.Assert(_completionState is null);
        Debug.Assert(_schedulingContext is null);
        _awaitableState &= ~AwaitableState.Completed;
    }

    public void OnCompleted(
        Action<object?> continuation,
        object? state,
        ValueTaskSourceOnCompletedFlags flags,
        out CompletionData completionData,
        out bool doubleCompletion)
    {
        completionData = default;
        doubleCompletion = _completion is not null;
        if (IsCompleted || doubleCompletion)
        {
            completionData = new CompletionData(
                continuation,
                state,
                _schedulingContext?.ExecutionContext,
                _schedulingContext?.SynchronizationContext);
            return;
        }

        _completion = continuation;
        _completionState = state;

        if ((_awaitableState & AwaitableState.UseSynchronizationContext) != 0
            && (flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) != 0)
        {
            SynchronizationContext? synchronizationContext = SynchronizationContext.Current;
            if (synchronizationContext is not null && synchronizationContext.GetType() != typeof(SynchronizationContext))
            {
                _schedulingContext ??= new SchedulingContext();
                _schedulingContext.SynchronizationContext = synchronizationContext;
            }
        }

        if ((flags & ValueTaskSourceOnCompletedFlags.FlowExecutionContext) != 0)
        {
            _schedulingContext ??= new SchedulingContext();
            _schedulingContext.ExecutionContext = ExecutionContext.Capture();
        }
    }

    public void Cancel(out CompletionData completionData)
    {
        ExtractCompletion(out completionData);
        _awaitableState |= AwaitableState.Canceled;
    }

    public void CancellationTokenFired(out CompletionData completionData)
    {
        if (CancellationToken.IsCancellationRequested)
        {
            Cancel(out completionData);
        }
        else
        {
            completionData = default;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ObserveCancellation()
    {
        bool isCanceled = (_awaitableState & AwaitableState.Canceled) != 0;
        _awaitableState &= ~(AwaitableState.Canceled | AwaitableState.Running);
        return isCanceled;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CancellationTokenRegistration ReleaseCancellationTokenRegistration(out CancellationToken cancellationToken)
    {
        cancellationToken = CancellationToken;
        CancellationTokenRegistration registration = _cancellationTokenRegistration;
        _cancellationTokenRegistration = default;
        return registration;
    }

    private void ExtractCompletion(out CompletionData completionData)
    {
        Action<object?>? currentCompletion = _completion;
        object? currentState = _completionState;
        SchedulingContext? schedulingContext = _schedulingContext;

        _completion = null;
        _completionState = null;
        _schedulingContext = null;

        completionData = currentCompletion is null
            ? default
            : new CompletionData(
                currentCompletion,
                currentState,
                schedulingContext?.ExecutionContext,
                schedulingContext?.SynchronizationContext);
    }

    [Flags]
    private enum AwaitableState : byte
    {
        None = 0,
        Completed = 1,
        Running = 2,
        Canceled = 4,
        UseSynchronizationContext = 8,
    }

    private sealed class SchedulingContext
    {
        public SynchronizationContext? SynchronizationContext { get; set; }

        public ExecutionContext? ExecutionContext { get; set; }
    }
}
