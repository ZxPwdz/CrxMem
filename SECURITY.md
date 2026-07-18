# Security Policy

CrxMem interacts with process memory and includes experimental native components. Treat development builds as research software and test kernel-mode functionality only in an isolated environment.

## Reporting a vulnerability

Do not open a public issue for a vulnerability, exposed credential, or unsafe proof of concept. Use GitHub's private security-advisory reporting for this repository instead.

Include the affected commit, reproduction conditions, expected impact, and the smallest safe reproduction you can provide. Do not include real credentials, private binaries, or third-party data.

## Supported versions

Only the current `main` branch receives security fixes while the project is pre-release. No published release is currently considered production-ready.

## Credential handling

- Never commit tokens, passwords, private keys, certificates, or credential-bearing remote URLs.
- Authenticate Git operations through Git Credential Manager, GitHub CLI browser login, or SSH.
- Revoke any credential exposed in logs, screenshots, terminal history, issues, or chat.
