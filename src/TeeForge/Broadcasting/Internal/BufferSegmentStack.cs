// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Adapted for TeeForge from dotnet/runtime commit 4271d88e0aebf3d04f188f1334c2220d80555ef6.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace TeeForge.Broadcasting.Internal;

internal struct BufferSegmentStack
{
    private SegmentAsValueType[] _array;
    private int _size;

    public BufferSegmentStack(int size)
    {
        _array = new SegmentAsValueType[size];
    }

    public int Count => _size;

    public bool TryPop([NotNullWhen(true)] out BufferSegment? result)
    {
        int size = _size - 1;
        SegmentAsValueType[] array = _array;
        if ((uint)size >= (uint)array.Length)
        {
            result = null;
            return false;
        }

        _size = size;
        result = array[size];
        array[size] = default;
        return true;
    }

    public void Push(BufferSegment item)
    {
        int size = _size;
        if ((uint)size < (uint)_array.Length)
        {
            _array[size] = item;
            _size = size + 1;
            return;
        }

        PushWithResize(item);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PushWithResize(BufferSegment item)
    {
        Array.Resize(ref _array, Math.Max(4, 2 * _array.Length));
        _array[_size++] = item;
    }

    private readonly struct SegmentAsValueType
    {
        private readonly BufferSegment _value;

        private SegmentAsValueType(BufferSegment value) => _value = value;

        public static implicit operator SegmentAsValueType(BufferSegment value) => new(value);

        public static implicit operator BufferSegment(SegmentAsValueType value) => value._value;
    }
}
