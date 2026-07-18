# Contributing to CrxMem

## Development workflow

1. Create a branch from an up-to-date `main` branch.
2. Keep each change focused and avoid committing build output, local settings, binaries, archives, or credentials.
3. Build the managed solution with `dotnet build CrxMem.sln --configuration Release`.
4. Add or update tests for behavior changes as the test suite is introduced.
5. Open a pull request describing the behavior, verification performed, and any experimental limitations.

Do not force-push `main`. Use Git Credential Manager, GitHub CLI browser authentication, or SSH; never embed a token in a Git remote URL.

## Project boundaries

CrxMem is an educational memory-scanning and debugging project. Contributions must not contain stolen code, credentials, game-specific online-cheat payloads, or instructions intended to bypass third-party security controls.

Kernel driver and injected-DLL changes require additional review and should be tested only in an isolated virtual machine. The managed build workflow does not validate those optional native components.

## Code quality

- Preserve nullable-reference annotations and fix new warnings in touched code.
- Keep process-memory logic independent from WPF and WinForms where practical.
- Surface actionable failures instead of silently swallowing exceptions.
- Disable unfinished commands in the UI rather than presenting them as working features.
