// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Adapted for TeeForge from dotnet/runtime commit 4271d88e0aebf3d04f188f1334c2220d80555ef6.

using System.Runtime.ExceptionServices;

namespace TeeForge.Internal;

internal struct PipeCompletion
{
    private static readonly object CompletedSuccessfully = new();
    private object? _state;
    private List<PipeCompletionCallback>? _callbacks;

    public bool IsCompleted => _state is not null;

    public PipeCompletionCallbacks? TryComplete(Exception? exception = null)
    {
        if (_state is null)
        {
            _state = exception is null ? CompletedSuccessfully : ExceptionDispatchInfo.Capture(exception);
        }

        return GetCallbacks();
    }

    public PipeCompletionCallbacks? AddCallback(Action<Exception?, object?> callback, object? state)
    {
        (_callbacks ??= []).Add(new PipeCompletionCallback(callback, state));
        return IsCompleted ? GetCallbacks() : null;
    }

    public void Reset()
    {
        _state = null;
        _callbacks = null;
    }

    private PipeCompletionCallbacks? GetCallbacks()
    {
        if (_callbacks is null)
        {
            return null;
        }

        List<PipeCompletionCallback> callbacks = _callbacks;
        _callbacks = null;
        return new PipeCompletionCallbacks(callbacks, _state as ExceptionDispatchInfo);
    }
}

internal readonly record struct PipeCompletionCallback(Action<Exception?, object?> Callback, object? State);

internal sealed class PipeCompletionCallbacks
{
    private readonly List<PipeCompletionCallback> _callbacks;
    private readonly Exception? _exception;

    public PipeCompletionCallbacks(List<PipeCompletionCallback> callbacks, ExceptionDispatchInfo? exception)
    {
        _callbacks = callbacks;
        _exception = exception?.SourceException;
    }

    public void Execute()
    {
        List<Exception>? exceptions = null;
        foreach (PipeCompletionCallback callback in _callbacks)
        {
            try
            {
                callback.Callback(_exception, callback.State);
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }
        }

        if (exceptions is not null)
        {
            throw new AggregateException(exceptions);
        }
    }
}
