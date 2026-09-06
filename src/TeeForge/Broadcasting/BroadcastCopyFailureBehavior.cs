namespace TeeForge.Broadcasting;

/// <summary>Controls how a broadcast copy handles a destination write failure.</summary>
public enum BroadcastCopyFailureBehavior
{
    /// <summary>Cancels the source pump and other destination copies, then reports collected failures.</summary>
    Stop = 0,

    /// <summary>Removes failed destinations, finishes healthy copies, then reports collected failures.</summary>
    Continue = 1,
}
