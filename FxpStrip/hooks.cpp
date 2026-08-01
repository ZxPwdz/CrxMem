#include "hooks.h"
#include "module.h"
MODULE gunz;
#include <intrin.h>
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <queue>
#include <string>

#include "minhook\minhook.h"

bool PatchMemory(LPVOID addr, const BYTE* bytes, size_t size)
{
	if (addr == NULL || bytes == NULL || size == 0)
		return false;

	DWORD oldprot = 0;
	if (!VirtualProtect(addr, size, PAGE_EXECUTE_READWRITE, &oldprot))
		return false;

	bool written = false;
	__try
	{
		memcpy(addr, bytes, size);
		written = memcmp(addr, bytes, size) == 0;
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		written = false;
	}

	DWORD ignored = 0;
	VirtualProtect(addr, size, oldprot, &ignored);
	if (written)
		FlushInstructionCache(GetCurrentProcess(), addr, size);
	return written;
}

bool menuvisible = false;

// ---------------------------------------------------------------------------
// .text snapshot for system API redirection
// ---------------------------------------------------------------------------
static BYTE*      g_textBackup    = NULL;
static DWORD      g_textBase      = 0;
static DWORD      g_textSize      = 0;
static volatile LONG g_compatReady = 0;

static void BackupTextSection()
{
	if (!gunz.IsInitialized()) return;

	g_textBase = gunz.GetCodeBase();
	g_textSize = gunz.GetCodeSize();

	if (g_textSize == 0 || g_textSize > 0x1000000) return; // sanity: max 16 MB

	g_textBackup = (BYTE*)VirtualAlloc(NULL, g_textSize,
	                                    MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
	if (g_textBackup)
	{
		memcpy(g_textBackup, (LPVOID)g_textBase, g_textSize);
	}
}

// ---------------------------------------------------------------------------
// Hook: VirtualQuery — normalise page protection flags for host callers
// ---------------------------------------------------------------------------
typedef SIZE_T (WINAPI *VirtualQuery_t)(LPCVOID, PMEMORY_BASIC_INFORMATION, SIZE_T);
VirtualQuery_t orig_VirtualQuery = NULL;

SIZE_T WINAPI hook_VirtualQuery(LPCVOID lpAddress, PMEMORY_BASIC_INFORMATION lpBuffer, SIZE_T dwLength)
{
	SIZE_T result = orig_VirtualQuery(lpAddress, lpBuffer, dwLength);

	if (result > 0 && lpBuffer != NULL && gunz.IsInitialized())
	{
		DWORD retAddr = (DWORD)_ReturnAddress();
		DWORD start   = gunz.GetCodeBase();
		DWORD end     = start + gunz.GetCodeSize();

		if (retAddr >= start && retAddr < end)
		{
			DWORD& protect = lpBuffer->Protect;

			// Detection at Gunz.exe+788322:
			//   test byte ptr [protect], 0xEE  → sete al  (detected if none of these bits)
			//   test [protect], 0x101           → cmovne   (detected if NOACCESS or GUARD)
			//
			// To evade: ensure (protect & 0xEE) != 0  AND  (protect & 0x101) == 0

			if (protect & (PAGE_NOACCESS | PAGE_GUARD))
			{
				// Remove NOACCESS/GUARD — replace with plain writable
				protect = (protect & ~(PAGE_NOACCESS | PAGE_GUARD)) | PAGE_READWRITE;
			}

			// If low byte has none of the 0xEE protection bits, fix it
			if ((protect & 0xEE) == 0)
			{
				protect = (protect & ~0xFF) | PAGE_READWRITE;
			}
		}
	}

	return result;
}

// ---------------------------------------------------------------------------
// Hook: GetTickCount — ensure consistent non-zero return value
// ---------------------------------------------------------------------------
typedef DWORD (WINAPI *GetTickCount_t)();
GetTickCount_t orig_GetTickCount = NULL;

DWORD WINAPI hook_GetTickCount()
{
	DWORD result = orig_GetTickCount();
	if (result == 0)
		return 1;
	return result;
}

// ---------------------------------------------------------------------------
// Hook: ReadProcessMemory — serve snapshot for self-reads from code section
// ---------------------------------------------------------------------------
typedef BOOL (WINAPI *ReadProcessMemory_t)(HANDLE, LPCVOID, LPVOID, SIZE_T, SIZE_T*);
ReadProcessMemory_t orig_ReadProcessMemory = NULL;

static bool IsCallerInGunzModule()
{
	DWORD retAddr = (DWORD)_ReturnAddress();
	DWORD base = gunz.GetModuleBase();
	return retAddr >= base && retAddr < base + gunz.GetModuleSize();
}

BOOL WINAPI hook_ReadProcessMemory(HANDLE  hProcess,
                                   LPCVOID lpBaseAddress,
                                   LPVOID  lpBuffer,
                                   SIZE_T  nSize,
                                   SIZE_T* lpNumberOfBytesRead)
{
	if (gunz.IsInitialized() && g_textBackup != NULL && lpBuffer != NULL)
	{
		DWORD targetAddr = (DWORD)lpBaseAddress;

		if (IsCallerInGunzModule() &&
		    hProcess == GetCurrentProcess() &&
		    targetAddr >= g_textBase && targetAddr < g_textBase + g_textSize &&
		    nSize <= 64)
		{
			DWORD offset = targetAddr - g_textBase;
			SIZE_T copySize = nSize;
			if (offset + copySize > g_textSize)
				copySize = g_textSize - offset;

			memcpy(lpBuffer, g_textBackup + offset, copySize);
			if (lpNumberOfBytesRead)
				*lpNumberOfBytesRead = copySize;
			return TRUE;
		}
	}

	return orig_ReadProcessMemory(hProcess, lpBaseAddress, lpBuffer, nSize,
	                              lpNumberOfBytesRead);
}

// ---------------------------------------------------------------------------
// Hook: NtReadVirtualMemory — same backup logic for direct ntdll callers
// ---------------------------------------------------------------------------
typedef LONG (NTAPI *NtReadVirtualMemory_t)(HANDLE, PVOID, PVOID, ULONG, PULONG);
NtReadVirtualMemory_t orig_NtReadVirtualMemory = NULL;

LONG NTAPI hook_NtReadVirtualMemory(HANDLE  ProcessHandle,
                                    PVOID   BaseAddress,
                                    PVOID   Buffer,
                                    ULONG   NumberOfBytesToRead,
                                    PULONG  NumberOfBytesRead)
{
	if (gunz.IsInitialized() && g_textBackup != NULL && Buffer != NULL)
	{
		DWORD targetAddr = (DWORD)BaseAddress;

		if (IsCallerInGunzModule() &&
		    ProcessHandle == (HANDLE)-1 &&  // -1 = GetCurrentProcess() pseudohandle
		    targetAddr >= g_textBase && targetAddr < g_textBase + g_textSize &&
		    NumberOfBytesToRead <= 64)
		{
			DWORD offset = targetAddr - g_textBase;
			ULONG copySize = NumberOfBytesToRead;
			if (offset + copySize > g_textSize)
				copySize = g_textSize - offset;

			memcpy(Buffer, g_textBackup + offset, copySize);
			if (NumberOfBytesRead)
				*NumberOfBytesRead = copySize;
			return 0; // STATUS_SUCCESS
		}
	}

	return orig_NtReadVirtualMemory(ProcessHandle, BaseAddress, Buffer,
	                                NumberOfBytesToRead, NumberOfBytesRead);
}

// ---------------------------------------------------------------------------
// Hook: TerminateProcess — gate process exit from host code
// ---------------------------------------------------------------------------
typedef BOOL (WINAPI *TerminateProcess_t)(HANDLE, UINT);
TerminateProcess_t orig_TerminateProcess = NULL;

BOOL WINAPI hook_TerminateProcess(HANDLE hProcess, UINT uExitCode)
{
	if (gunz.IsInitialized())
	{
		DWORD retAddr = (DWORD)_ReturnAddress();
		DWORD start   = gunz.GetCodeBase();
		DWORD end     = start + gunz.GetCodeSize();

		if (retAddr >= start && retAddr < end)
		{

			SetLastError(ERROR_ACCESS_DENIED);
			return FALSE;
		}
	}

	return orig_TerminateProcess(hProcess, uExitCode);
}

// ---------------------------------------------------------------------------
// Hook: SetUnhandledExceptionFilter — gate exception handler removal from host
// ---------------------------------------------------------------------------
typedef LPTOP_LEVEL_EXCEPTION_FILTER (WINAPI *SetUnhandledExceptionFilter_t)(
    LPTOP_LEVEL_EXCEPTION_FILTER);
SetUnhandledExceptionFilter_t orig_SetUnhandledExceptionFilter = NULL;

LPTOP_LEVEL_EXCEPTION_FILTER WINAPI
hook_SetUnhandledExceptionFilter(LPTOP_LEVEL_EXCEPTION_FILTER lpTopLevelExceptionFilter)
{
	if (gunz.IsInitialized())
	{
		DWORD retAddr = (DWORD)_ReturnAddress();
		DWORD start   = gunz.GetCodeBase();
		DWORD end     = start + gunz.GetCodeSize();

		if (retAddr >= start && retAddr < end)
		{
			// Pretend we removed the handler successfully
			return NULL;
		}
	}

	return orig_SetUnhandledExceptionFilter(lpTopLevelExceptionFilter);
}

// ---------------------------------------------------------------------------
// Existing hooks (kept from original) ---------------------------------------
// ---------------------------------------------------------------------------

static volatile LONG g_memoryWritePatchReady = 0;

typedef DWORD(WINAPI* GetCurrentThreadId_t)();
GetCurrentThreadId_t orig_GetCurrentThreadId = NULL;

DWORD WINAPI hook_GetCurrentThreadId()
{
	DWORD start = gunz.GetCodeBase();
	DWORD end = start + gunz.GetCodeSize();
	if ((DWORD)_ReturnAddress() >= start && (DWORD)_ReturnAddress() < end)
	{
		if (*(SHORT*)_ReturnAddress() == 0x0539)
		{
			return **(DWORD**)((DWORD)_ReturnAddress() + 2);
		}
	}

	return orig_GetCurrentThreadId();
}

typedef BOOL(WINAPI* PostMessageA_t)(HWND hWnd, UINT Msg, WPARAM wParam, LPARAM lParam);
PostMessageA_t orig_PostMessageA = NULL;

BOOL WINAPI hook_PostMessageA(HWND hWnd, UINT Msg, WPARAM wParam, LPARAM lParam)
{
	if (Msg == WM_CLOSE)
	{
		return TRUE;
	}

	return orig_PostMessageA(hWnd, Msg, wParam, lParam);
}

typedef BOOL(WINAPI* SetCursorPos_t)(int X, int Y);
SetCursorPos_t orig_SetCursorPos = NULL;

BOOL WINAPI hook_SetCursorPos(int X, int Y)
{
	if (GetForegroundWindow() != g_hWnd || menuvisible)
		return FALSE;

	return orig_SetCursorPos(X, Y);
}

typedef BOOL(WINAPI* PeekMessageA_t)(LPMSG lpMsg, HWND hWnd, UINT wMsgFilterMin, UINT wMsgFilterMax, UINT wRemoveMsg);
PeekMessageA_t orig_PeekMessageA = NULL;

typedef void (*ZChatOutput_t)(const char* szMsg, int msgtype, int loc, DWORD _color);
ZChatOutput_t ZChatOutput;
std::queue<std::string> messagequeue;
HANDLE messagemutex;

BOOL WINAPI hook_PeekMessageA(LPMSG lpMsg, HWND hWnd, UINT wMsgFilterMin, UINT wMsgFilterMax, UINT wRemoveMsg)
{
	WaitForSingleObject(messagemutex, INFINITE);
	while (!messagequeue.empty())
	{
		std::string msg = messagequeue.front();
		messagequeue.pop();
		ZChatOutput(msg.c_str(), 2, 0, 0xFFFFFFFF);
	}
	ReleaseMutex(messagemutex);

	return orig_PeekMessageA(lpMsg, hWnd, wMsgFilterMin, wMsgFilterMax, wRemoveMsg);
}

typedef HHOOK(WINAPI* SetWindowsHookExA_t)(int idHook, HOOKPROC lpfn, HINSTANCE hmod, DWORD dwThreadId);
SetWindowsHookExA_t orig_SetWindowsHookExA = NULL;

HHOOK WINAPI hook_SetWindowsHookExA(int idHook, HOOKPROC lpfn, HINSTANCE hmod, DWORD dwThreadId)
{
	return NULL;
}

void InitScan()
{
	static bool scanned = false;
	if (!scanned)
	{
		scanned = true;
		gunz.InitializeModule("gunz.exe");
	}
}

// ---------------------------------------------------------------------------
// PrepareMemoryWrites — init module scan and install compat hooks
// ---------------------------------------------------------------------------
// ---------------------------------------------------------------------------
// Ninja Jump — NOP the jz rel32 at RVA 0x2B7C3D (6 bytes), toggleable
// ---------------------------------------------------------------------------
static BYTE  g_ninjaOrig[6]   = {0};
static bool  g_ninjaEnabled   = false;
static bool  g_ninjaOrigSaved = false;

void SetNinjaJump(bool enable)
{
	if (!gunz.IsInitialized()) return;

	DWORD addr = gunz.GetModuleBase() + 0x2B7C3D;

	if (!g_ninjaOrigSaved)
	{
		// Prefer the .text backup (clean original), fall back to live memory
		if (g_textBackup)
			memcpy(g_ninjaOrig, g_textBackup + (addr - g_textBase), 6);
		else
			memcpy(g_ninjaOrig, (LPVOID)addr, 6);
		g_ninjaOrigSaved = true;
	}

	BYTE nop6[] = { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 };
	const BYTE* bytes = enable ? nop6 : g_ninjaOrig;

	DWORD oldprot = 0;
	if (!VirtualProtect((LPVOID)addr, 6, PAGE_EXECUTE_READWRITE, &oldprot))
		return;

	__try { memcpy((LPVOID)addr, bytes, 6); }
	__except (EXCEPTION_EXECUTE_HANDLER) {}

	VirtualProtect((LPVOID)addr, 6, oldprot, &oldprot);
	FlushInstructionCache(GetCurrentProcess(), (LPVOID)addr, 6);
	g_ninjaEnabled = enable;
}

bool IsNinjaJumpEnabled()
{
	return g_ninjaEnabled;
}

// ---------------------------------------------------------------------------
// PrepareMemoryWrites — repair NtProtectVirtualMemory syscall stub and install compat hooks
// ---------------------------------------------------------------------------
void PrepareMemoryWrites()
{
	InitScan();
	InstallCompatHooks();

	// Repair ntdll!NtProtectVirtualMemory syscall stub if it was detoured
	LPVOID target = GetProcAddress(GetModuleHandleA("ntdll.dll"), "NtProtectVirtualMemory");
	BYTE repair[] = { 0xB8, 0x50, 0x00, 0x00, 0x00 }; // mov eax, 0x50 (syscall number)
	bool patchApplied = PatchMemory(target, repair, sizeof(repair));

	bool repaired = false;
	__try
	{
		repaired = patchApplied && target != NULL && memcmp(target, repair, sizeof(repair)) == 0;
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		repaired = false;
	}

	InterlockedExchange(&g_memoryWritePatchReady, repaired ? 1 : 0);
}

bool IsMemoryWritePatchReady()
{
	return InterlockedCompareExchange(&g_memoryWritePatchReady, 0, 0) != 0;
}

// ---------------------------------------------------------------------------
// InstallCompatHooks — redirect system APIs for host compatibility
// ---------------------------------------------------------------------------
void InstallCompatHooks()
{
	static bool installed = false;
	if (installed || !gunz.IsInitialized())
		return;
	installed = true;

	// Snapshot code section before any modifications
	BackupTextSection();

	MH_CreateHookApi(L"kernel32.dll", "VirtualQuery",
	                 &hook_VirtualQuery, (LPVOID*)&orig_VirtualQuery);

	MH_CreateHookApi(L"kernel32.dll", "GetTickCount",
	                 &hook_GetTickCount, (LPVOID*)&orig_GetTickCount);

	MH_CreateHookApi(L"kernel32.dll", "ReadProcessMemory",
	                 &hook_ReadProcessMemory, (LPVOID*)&orig_ReadProcessMemory);

	MH_CreateHookApi(L"ntdll.dll", "NtReadVirtualMemory",
	                 &hook_NtReadVirtualMemory, (LPVOID*)&orig_NtReadVirtualMemory);

	MH_EnableHook(MH_ALL_HOOKS);

	InterlockedExchange(&g_compatReady, 1);
}

bool IsCompatReady()
{
	return InterlockedCompareExchange(&g_compatReady, 0, 0) != 0;
}

// ---------------------------------------------------------------------------

DWORD __stdcall DoNothing(LPVOID lpThreadParameter)
{
	while (1)
	{
		Sleep(INFINITE);
	}

	return 0;
}

typedef HANDLE(WINAPI* CreateThread_t)(LPSECURITY_ATTRIBUTES lpThreadAttributes, SIZE_T dwStackSize, LPTHREAD_START_ROUTINE lpStartAddress, LPVOID lpParameter, DWORD dwCreationFlags, LPDWORD lpThreadId);
CreateThread_t orig_CreateThread = NULL;

// Legit GunZ thread-pool start RVAs (relative to Gunz.exe module base).
// Identified from live log: starts 0x137880..0x137920 with module base 0x40000.
static bool IsLegitPoolThread(DWORD startAddr)
{
	DWORD rva = startAddr - gunz.GetModuleBase();
	switch (rva)
	{
		case 0xF7880:
		case 0xF78C0:
		case 0xF78E0:
		case 0xF7900:
		case 0xF7920:
			return true;
	}
	return false;
}

// Shared block policy: true = this in-.text thread start must be blocked.
// Caller must only invoke when start is inside gunz .text.
static bool ShouldBlockThreadStart(DWORD startAddr, DWORD caller)
{
	DWORD callerbytes = 0;
	__try { callerbytes = *(DWORD*)caller; }
	__except (EXCEPTION_EXECUTE_HANDLER) { return false; } // can't read caller -> don't block

	// Friend's whitelisted caller signatures.
	if (callerbytes == 0x8B044689 || callerbytes == 0xFF85F88B)
		return false;

	// Legit thread-pool spawner (callerbytes 0x89A8558B) — only for known pool starts.
	if (callerbytes == 0x89A8558B && IsLegitPoolThread(startAddr))
		return false;

	return true;
}

static void LogThreadEvent(const char* tag, DWORD startAddr, DWORD caller)
{
	if (FILE* f = fopen("D:\\FxpGunZ\\fxp_cthread.log", "a"))
	{
		DWORD callerbytes = 0;
		__try { callerbytes = *(DWORD*)caller; }
		__except (EXCEPTION_EXECUTE_HANDLER) {}
		fprintf(f, "%s start=0x%08X caller=0x%08X callerbytes=%08X\n",
		        tag, startAddr, caller, callerbytes);
		fclose(f);
	}
}

static HANDLE GetFakeThread()
{
	static HANDLE fakethread = NULL;
	if (fakethread == NULL)
	{
		fakethread = orig_CreateThread(NULL, 0, &DoNothing, NULL, 0, NULL);
	}
	return fakethread;
}

HANDLE WINAPI hook_CreateThread(LPSECURITY_ATTRIBUTES lpThreadAttributes, SIZE_T dwStackSize, LPTHREAD_START_ROUTINE lpStartAddress, LPVOID lpParameter, DWORD dwCreationFlags, LPDWORD lpThreadId)
{
	DWORD start = gunz.GetCodeBase();
	DWORD end = start + gunz.GetCodeSize();

	if ((DWORD)lpStartAddress >= start && (DWORD)lpStartAddress < end)
	{
		DWORD caller = (DWORD)_ReturnAddress();

		if (ShouldBlockThreadStart((DWORD)lpStartAddress, caller))
		{
			LogThreadEvent("BLOCKED", (DWORD)lpStartAddress, caller);
			return GetFakeThread();
		}

		LogThreadEvent("allowed", (DWORD)lpStartAddress, caller);
	}

	return orig_CreateThread(lpThreadAttributes, dwStackSize, lpStartAddress, lpParameter, dwCreationFlags, lpThreadId);
}

// ---------------------------------------------------------------------------
// Hook: NtCreateThreadEx — same policy for direct ntdll callers.
// A watchdog that bypasses kernel32!CreateThread lands here instead.
// ---------------------------------------------------------------------------
typedef LONG (NTAPI *NtCreateThreadEx_t)(
	PHANDLE ThreadHandle, ACCESS_MASK DesiredAccess, PVOID ObjectAttributes,
	HANDLE ProcessHandle, LPTHREAD_START_ROUTINE StartRoutine, LPVOID Argument,
	ULONG CreateFlags, SIZE_T ZeroBits, SIZE_T StackSize, SIZE_T MaximumStackSize,
	PVOID AttributeList);
NtCreateThreadEx_t orig_NtCreateThreadEx = NULL;

LONG NTAPI hook_NtCreateThreadEx(
	PHANDLE ThreadHandle, ACCESS_MASK DesiredAccess, PVOID ObjectAttributes,
	HANDLE ProcessHandle, LPTHREAD_START_ROUTINE StartRoutine, LPVOID Argument,
	ULONG CreateFlags, SIZE_T ZeroBits, SIZE_T StackSize, SIZE_T MaximumStackSize,
	PVOID AttributeList)
{
	// Only gate thread creation targeted at THIS process.
	if (gunz.IsInitialized() &&
	    (ProcessHandle == (HANDLE)-1 || ProcessHandle == GetCurrentProcess()) &&
	    StartRoutine != NULL)
	{
		DWORD start = gunz.GetCodeBase();
		DWORD end = start + gunz.GetCodeSize();

		if ((DWORD)StartRoutine >= start && (DWORD)StartRoutine < end)
		{
			DWORD caller = (DWORD)_ReturnAddress();

			if (ShouldBlockThreadStart((DWORD)StartRoutine, caller))
			{
				LogThreadEvent("BLOCKED-NT", (DWORD)StartRoutine, caller);
				if (ThreadHandle)
					*ThreadHandle = GetFakeThread();
				return 0; // STATUS_SUCCESS — pretend the thread was created
			}

			LogThreadEvent("allowed-NT", (DWORD)StartRoutine, caller);
		}
	}

	return orig_NtCreateThreadEx(ThreadHandle, DesiredAccess, ObjectAttributes,
	                             ProcessHandle, StartRoutine, Argument, CreateFlags,
	                             ZeroBits, StackSize, MaximumStackSize, AttributeList);
}

typedef BOOL(WINAPI* BitBlt_t)(HDC hdc, int x, int y, int cx, int cy, HDC hdcSrc, int x1, int y1, DWORD rop);
BitBlt_t orig_BitBlt = NULL;
BOOL WINAPI hook_BitBlt(HDC hdc, int x, int y, int cx, int cy, HDC hdcSrc, int x1, int y1, DWORD rop)
{
	DWORD start = gunz.GetCodeBase();
	DWORD end = start + gunz.GetCodeSize();
	if ((DWORD)_ReturnAddress() >= start && (DWORD)_ReturnAddress() < end)
	{
		return TRUE;
	}

	return orig_BitBlt(hdc, x, y, cx, cy, hdcSrc, x1, y1, rop);
}

typedef BOOL(WINAPI* ClipCursor_t)(const RECT* lpRect);
ClipCursor_t orig_ClipCursor = NULL;
BOOL WINAPI hook_ClipCursor(const RECT* lpRect)
{
	lpRect = NULL;
	return orig_ClipCursor(lpRect);
}

typedef HMODULE(WINAPI* LoadLibraryA_t)(LPCSTR lpLibFileName);
LoadLibraryA_t orig_LoadLibraryA = NULL;

const char* stristr(const char* haystack, const char* needle) {
	if (!*needle)
		return haystack;

	for (; *haystack; ++haystack) {
		if (!_strnicmp(haystack, needle, strlen(needle)))
			return haystack;
	}
	return NULL;
}

HMODULE WINAPI hook_LoadLibraryA(LPCSTR lpLibFileName)
{
	if (stristr(lpLibFileName, "EhSvc.dll"))
	{
		size_t basePathLen = strlen(lpLibFileName) - strlen("EhSvc.dll");

		char newPath[MAX_PATH];
		strncpy(newPath, lpLibFileName, basePathLen);
		newPath[basePathLen] = '\0';

		strcat(newPath, "fake\\EhSvc.dll");

		lpLibFileName = newPath;
	}

	return orig_LoadLibraryA(lpLibFileName);
}

typedef HANDLE(WINAPI* CreateMutexA_t)(LPSECURITY_ATTRIBUTES lpMutexAttributes, BOOL bInitialOwner, LPCSTR lpName);
CreateMutexA_t orig_CreateMutexA = NULL;

HANDLE WINAPI hook_CreateMutexA(LPSECURITY_ATTRIBUTES lpMutexAttributes, BOOL bInitialOwner, LPCSTR lpName)
{
	static BOOL firstcall = false;
	if (!firstcall)
	{
		firstcall = true;
		PrepareMemoryWrites();
	}

	return orig_CreateMutexA(lpMutexAttributes, bInitialOwner, lpName);
}

typedef LONG(WINAPI* SetWindowLongA_t)(HWND hWnd, int nIndex, LONG dwNewLong);
SetWindowLongA_t orig_SetWindowLongA = NULL;

LONG WINAPI hook_SetWindowLongA(HWND hWnd, int nIndex, LONG dwNewLong)
{
	if (hWnd != g_hWnd)
	{
		return 0;
	}

	return orig_SetWindowLongA(hWnd, nIndex, dwNewLong);
}

BOOL InstallHooks()
{
	BOOL ret = TRUE;

	if (MH_Initialize() != MH_OK)
		ret = FALSE;
	else
	{
		// Crash-isolation build: keep only the hook required for fake EhSvc.dll.
		if (MH_CreateHookApi(L"kernel32.dll", "LoadLibraryA", &hook_LoadLibraryA, (LPVOID*)&orig_LoadLibraryA) != MH_OK)
			ret = FALSE;

		// Watchdog prevention: block stealth thread creation from game code
		// (friend's fix — swaps non-whitelisted in-.text thread starts for a dummy).
		if (MH_CreateHookApi(L"kernel32.dll", "CreateThread", &hook_CreateThread, (LPVOID*)&orig_CreateThread) != MH_OK)
			ret = FALSE;

		// Same policy one level down — catches watchdogs that skip kernel32.
		if (MH_CreateHookApi(L"ntdll.dll", "NtCreateThreadEx", &hook_NtCreateThreadEx, (LPVOID*)&orig_NtCreateThreadEx) != MH_OK)
			ret = FALSE;

		if (MH_EnableHook(MH_ALL_HOOKS) != MH_OK)
			ret = FALSE;
	}

	return ret;
}
