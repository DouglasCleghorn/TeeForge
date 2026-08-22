// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Adapted for TeeForge from dotnet/runtime commit 4271d88e0aebf3d04f188f1334c2220d80555ef6.

namespace TeeForge.Internal;

internal readonly struct CompletionData
{
    public CompletionData(
        Action<object?> completion,
        object? completionState,
        ExecutionContext? executionContext,
        SynchronizationContext? synchronizationContext)
    {
        Completion = completion;
        CompletionState = completionState;
        ExecutionContext = executionContext;
        SynchronizationContext = synchronizationContext;
    }

    public Action<object?> Completion { get; }

    public object? CompletionState { get; }

    public ExecutionContext? ExecutionContext { get; }

    public SynchronizationContext? SynchronizationContext { get; }
}
