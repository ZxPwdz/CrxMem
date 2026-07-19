# Phase 20 Handoff — Final Acceptance

## Result

- CrxMem version advanced to 1.2.0.
- WPF remains the primary Memory View route; the legacy viewer remains available for at least this release.
- All 119 Release tests pass with every native opt-in gate enabled.
- Final persistence now includes tool columns, disassembly recent addresses, hex recent addresses, and Microsoft symbol policy in addition to existing placement, panes, tabs, font, and density settings.
- The final handoff and known-limitations documents now reflect Phases 15–20 and the actual AA compatibility boundary.

## Native acceptance

- Debug attach/pause/resume/detach and target cleanup: pass.
- DbgHelp native unwind and symbol lifecycle: pass.
- Register mutate/read-back/restore: pass.
- Verified patch apply/restore: pass.
- Software and hardware breakpoint lifecycle: pass.
- Transactional AA enable/disable against the benign executable probe: pass.
- Local navigation performance budget: pass.

## Manual acceptance

- The Release executable is produced in `CrxMem/bin/Release/net8.0-windows` and launched for owner verification.
- Notepad and Gunz remain the owner's manual visual/behavior checks because automated tests must not assume those external applications or game binaries are installed/running.
- Subjective per-monitor DPI inspection remains a manual check; automated resource/render coverage passes.

## Recovery

- Revert each phase independently through commits `2b0b98f`, `07c00c4`, `b996af4`, `f7b5796`, `faa7cff`, and the Phase 20 documentation commit.
- Reset only MemoryView layout by renaming `%LocalAppData%\CrxMem\MemoryView.layout.json` while the viewer is closed.
- Never reuse an AA preview after the process restarts; process-instance checks reject it automatically.
