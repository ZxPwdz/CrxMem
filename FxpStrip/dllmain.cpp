#include "hooks.h"
#include "ehsvc.h"
#include "gunz.h"
#include <Windows.h>

DWORD __stdcall MainThread(LPVOID lpParameter)
{
	PatchHackshield();
	PrepareMemoryWrites();

	while (!(g_hWnd = FindWindowA("RealSpace2", NULL)))
	{
		Sleep(500);
	}

	while (1)
	{
		Sleep(10);
	}

	FreeLibraryAndExitThread((HMODULE)lpParameter, 0);
	return 0;
}

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpvReserved)
{
	if (fdwReason == DLL_PROCESS_ATTACH)
	{
		DisableThreadLibraryCalls(hinstDLL);

		SetConsoleModule(hinstDLL);


		InstallHooks();

		// Spawn the main thread for game window detection
		CloseHandle(CreateThread(NULL, 0, &MainThread, hinstDLL, 0, NULL));

		// Spawn the console control thread
		CloseHandle(CreateThread(NULL, 0, &ConsoleThread, NULL, 0, NULL));
		return TRUE;
	}
	else if (fdwReason == DLL_PROCESS_DETACH)
	{
		RestoreAll();
		return TRUE;
	}

	return FALSE;
}
