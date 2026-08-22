// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Adapted for TeeForge from dotnet/runtime commit 4271d88e0aebf3d04f188f1334c2220d80555ef6.

using System.Runtime.CompilerServices;

namespace TeeForge.Internal;

internal struct PipeOperationState
{
    private State _state;

    public bool IsWritingActive => (_state & State.Writing) != 0;

    public bool IsReadingActive => (_state & State.Reading) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginRead()
    {
        if ((_state & State.Reading) != 0)
        {
            throw new InvalidOperationException("Reading is already in progress.");
        }

        _state |= State.Reading;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginReadTentative()
    {
        if ((_state & State.Reading) != 0)
        {
            throw new InvalidOperationException("Reading is already in progress.");
        }

        _state |= State.ReadingTentative;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EndRead()
    {
        if ((_state & (State.Reading | State.ReadingTentative)) == 0)
        {
            throw new InvalidOperationException("No read operation is available to advance.");
        }

        _state &= ~(State.Reading | State.ReadingTentative);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginWrite() => _state |= State.Writing;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EndWrite() => _state &= ~State.Writing;

    public void Reset() => _state = State.None;

    [Flags]
    private enum State : byte
    {
        None = 0,
        Reading = 1,
        ReadingTentative = 2,
        Writing = 4,
    }
}
