#pragma once

#include <Windows.h>

bool PatchMemory(LPVOID addr, const BYTE* bytes, size_t size);
BOOL InstallHooks();
void PrepareMemoryWrites();
bool IsMemoryWritePatchReady();

// Compatibility layer for system API redirection
void InstallCompatHooks();
bool IsCompatReady();

// Ninja Jump toggle — NOPs the jz rel32 at RVA 0x2B7C3D
void SetNinjaJump(bool enable);
bool IsNinjaJumpEnabled();

// Global HWND shared between modules
extern HWND g_hWnd;
