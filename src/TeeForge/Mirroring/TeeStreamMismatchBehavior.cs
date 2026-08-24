namespace TeeForge.Mirroring;

/// <summary>Specifies how <see cref="TeeStream"/> handles successful results that differ between destinations.</summary>
public enum TeeStreamMismatchBehavior
{
    /// <summary>Throws for the current operation and continues invoking every destination on later operations.</summary>
    ThrowAndContinue = 0,

    /// <summary>Throws for the current operation and permanently faults the wrapper.</summary>
    ThrowAndFault = 1,

    /// <summary>Accepts the primary destination's result.</summary>
    UsePrimary = 2,
}
