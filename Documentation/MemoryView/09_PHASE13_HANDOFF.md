# MemoryView Phase 13 Handoff

## Outcome

Phase 13 removes two major safety gates: WPF can assemble a bounded single instruction into reviewable replacement bytes, and module-relative patches/metadata can survive ASLR and restarts without trusting stale absolute addresses.

## Assembly boundary

- `IAssemblerService` is UI-free and returns structured success, bytes, or an explicit error.
- The current parser accepts one instruction and supports common no-operand, branch/call, 32/64-bit register, and immediate forms.
- Assembly only updates the replacement-byte field. `IPatchService` remains the sole memory mutation boundary.
- Longer encodings are rejected before a transaction. Shorter encodings require the existing explicit NOP-padding option.
- Memory operands, SIMD/vector forms, labels, directives, and scripts remain disabled until added as separately tested grammar families.

## Durable identity boundary

- Loaded modules now carry a file path when Windows exposes one.
- `FileModuleIdentityService` fingerprints the on-disk binary with SHA-256, image size, file length, and last-write metadata.
- Persisted patch and metadata locations use the fingerprint plus RVA; runtime addresses are reconstructed only after an exact identity and bounds check.
- Patch reload also reads the current bytes to classify an entry as applied, restored, or stale. It never writes automatically.
- Entries lacking a reliable module fingerprint are deliberately session-only.

## Storage

- Patches: `%LocalAppData%\CrxMem\MemoryView.patches.v1.json`
- Metadata: `%LocalAppData%\CrxMem\MemoryView.metadata.v1.json`
- Both documents are versioned and written through a temporary file followed by replacement.
- Resolvable legacy comments/bookmarks migrate into the versioned metadata store; unresolvable absolute records remain in the legacy compatibility files.

## Next milestone

Expose comment/bookmark editing in WPF using `VersionedMemoryMetadataService`, then add direct hex selection/paste editing by translating edits into verified patch requests. Keep memory-operand assembly as an independent grammar/test expansion rather than silently accepting incomplete syntax.
