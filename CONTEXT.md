# TeeForge

TeeForge provides .NET I/O primitives that distribute one producer's data to multiple consumers.

## Language

**TeeStream**:
A stream that mirrors operations across an arbitrary set of destination streams and presents them as one stream. An operation is supported only when every destination supports it.

**Destination stream**:
A stream configured to receive the data written through a TeeStream.

**Primary stream**:
The first destination stream. Its data and return values become TeeStream's observable result when the configured consistency policy tolerates differences between destinations.

**Primary-sized read**:
A TeeStream read in which the primary stream determines the returned byte count and every other destination is advanced by that same number of bytes before their content is compared.

**Consistency policy**:
The rule that determines whether TeeStream rejects differences between destination results or accepts the primary stream's result.

**Strict consistency**:
The default TeeStream consistency policy, under which differing return values or read data cause the current operation to fail without preventing later operations.

**Faulted stream**:
A TeeStream that refuses further operations after discovering inconsistent destination results. Faulting is an opt-in consistency policy rather than the default.

**Use primary**:
A TeeStream consistency policy that accepts differences between destinations and exposes the primary stream's data or return value.

**TeePipe**:
A pipe with one writer and a fixed set of readers that broadcasts the same byte sequence to every reader. Each reader observes the complete sequence independently rather than competing with other readers for data.

**Active reader**:
A TeePipe reader that has not completed and therefore still participates in broadcast delivery and flow control.

**Reader completion**:
The normal or exceptional end of one TeePipe reader's participation in the broadcast. Reader completions are independently observable by their fixed reader indexes.
