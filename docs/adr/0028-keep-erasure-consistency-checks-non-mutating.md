# Keep erasure consistency checks non-mutating

`ErasureCodeStream` consistency checks inspect member metadata, integrity
blocks, and Reed-Solomon codewords without rewriting stored shards. They may
change volatile member condition and emit bounded findings, but all storage
mutation belongs to the separate explicit heal operation.

This preserves physically read-only checking and keeps a diagnostic call from
changing evidence while it is being examined. It requires a second maintenance
pass to heal identifiable damage, accepting that separation in exchange for a
clear read-only contract and deliberate mutation boundary.
