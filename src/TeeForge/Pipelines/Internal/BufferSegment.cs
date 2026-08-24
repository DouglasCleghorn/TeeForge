// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Adapted for TeeForge from dotnet/runtime commit 4271d88e0aebf3d04f188f1334c2220d80555ef6.

using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace TeeForge.Pipelines.Internal;

internal sealed class BufferSegment : ReadOnlySequenceSegment<byte>
{
    private IMemoryOwner<byte>? _memoryOwner;
    private byte[]? _array;
    private BufferSegment? _next;
    private int _end;

    public int End
    {
        get => _end;
        set
        {
            Debug.Assert(value <= AvailableMemory.Length);
            _end = value;
            Memory = AvailableMemory[..value];
        }
    }

    public BufferSegment? NextSegment
    {
        get => _next;
        set
        {
            Next = value;
            _next = value;
        }
    }

    public Memory<byte> AvailableMemory { get; private set; }

    public int Length => End;

    public int WritableBytes => AvailableMemory.Length - End;

    internal object? MemoryOwner => (object?)_memoryOwner ?? _array;

    public void SetOwnedMemory(IMemoryOwner<byte> memoryOwner)
    {
        _memoryOwner = memoryOwner;
        AvailableMemory = memoryOwner.Memory;
    }

    public void SetOwnedMemory(byte[] arrayPoolBuffer)
    {
        _array = arrayPoolBuffer;
        AvailableMemory = arrayPoolBuffer;
    }

    public void Reset()
    {
        ResetMemory();
        Next = null;
        RunningIndex = 0;
        _next = null;
    }

    public void ResetMemory()
    {
        IMemoryOwner<byte>? memoryOwner = _memoryOwner;
        if (memoryOwner is not null)
        {
            _memoryOwner = null;
            memoryOwner.Dispose();
        }
        else if (_array is not null)
        {
            ArrayPool<byte>.Shared.Return(_array);
            _array = null;
        }

        Memory = default;
        _end = 0;
        AvailableMemory = default;
    }

    public void SetNext(BufferSegment segment)
    {
        Debug.Assert(Next is null);
        NextSegment = segment;

        BufferSegment current = this;
        while (current.NextSegment is not null)
        {
            current.NextSegment.RunningIndex = current.RunningIndex + current.Length;
            current = current.NextSegment;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long GetLength(BufferSegment startSegment, int startIndex, BufferSegment endSegment, int endIndex) =>
        (endSegment.RunningIndex + (uint)endIndex) - (startSegment.RunningIndex + (uint)startIndex);
}
