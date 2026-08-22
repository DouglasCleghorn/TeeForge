# Keep TeeForge public types unsealed

TeeForge's public concrete classes will remain inheritable rather than sealed. This permits consumer specialization and follows the extensibility posture requested for the library, accepting that inherited virtual `Stream` members can be overridden in ways that bypass TeeStream's mirroring guarantees and that sealing these types later would be a breaking change. Inheritance does not imply that TeeForge will add new protected extension points beyond the members required by the framework abstractions.
