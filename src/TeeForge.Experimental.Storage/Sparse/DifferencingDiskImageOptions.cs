namespace TeeForge.Experimental.Storage.Sparse;

/// <summary>Provides immutable creation, open, and lifetime options for a differencing stream.</summary>
public class DifferencingDiskImageOptions
{
    /// <summary>Gets the default options.</summary>
    public static DifferencingDiskImageOptions Default { get; } = new();

    /// <summary>Initializes options.</summary>
    public DifferencingDiskImageOptions(
        bool leaveBaseOpen = false,
        bool leaveDifferenceOpen = false,
        bool readOnly = false,
        bool notifyBaseOnCreate = false)
    {
        LeaveBaseOpen = leaveBaseOpen;
        LeaveDifferenceOpen = leaveDifferenceOpen;
        ReadOnly = readOnly;
        NotifyBaseOnCreate = notifyBaseOnCreate;
    }

    /// <summary>Gets whether disposal leaves the base stream open.</summary>
    public bool LeaveBaseOpen { get; }

    /// <summary>Gets whether disposal leaves the physical difference stream open.</summary>
    public bool LeaveDifferenceOpen { get; }

    /// <summary>Gets whether open forces read-only operation.</summary>
    public bool ReadOnly { get; }

    /// <summary>Gets whether create registers the child with its immediate base.</summary>
    public bool NotifyBaseOnCreate { get; }

}
