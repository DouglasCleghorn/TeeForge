using TeeForge.ErasureCoding;

namespace TeeForge.Tests;

public class LibraryBoundaryTests
{
    [Fact]
    public void Broadcast_pipe_replaces_the_previous_tee_pipe_public_names()
    {
        System.Reflection.Assembly assembly = typeof(BroadcastPipe).Assembly;
        Assert.Null(assembly.GetType("TeeForge.Pipelines.TeePipe"));
        Assert.Null(assembly.GetType("TeeForge.Pipelines.TeePipeOptions"));
        Assert.Null(assembly.GetType("TeeForge.Pipelines.TeePipeReaderFailureBehavior"));
    }

    [Fact]
    public void Core_assembly_has_no_persistent_storage_types_or_dependencies()
    {
        System.Reflection.Assembly assembly = typeof(ErasureStream).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), name =>
            name.Name!.Contains("Experimental", StringComparison.Ordinal));
        Assert.DoesNotContain(assembly.GetExportedTypes(), type =>
            type.Namespace!.Contains("Experimental", StringComparison.Ordinal) ||
            type.Namespace == "TeeForge.Sparse" ||
            type.Name.Contains("Journal", StringComparison.Ordinal) ||
            type.Name.Contains("ErasureImage", StringComparison.Ordinal) ||
            type.Name.Contains("ErasureCodeStream", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(ErasureStream).GetMethods(), method =>
            method.Name is "IncreaseParityAsync" or "ReduceParityAsync" or "ReplaceParityImageAsync");
    }
}
