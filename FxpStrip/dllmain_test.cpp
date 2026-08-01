// ============================================================
//  Minimal Test DLL - Check if hooks work
// ============================================================

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "hooks.h"

extern HMODULE g_hModule;

BOOL APIENTRY DllMain(HMODULE hMod, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_hModule = hMod;
        DisableThreadLibraryCalls(hMod);

        // Test 1: Beep immediately
        Beep(1000, 200);

        Sleep(500);

        // Test 2: Install hooks
        BOOL hookResult = InstallHooks();

        Sleep(200);

        // Test 3: Second beep to indicate hook install result
        if (hookResult) {
            Beep(2000, 200); // High beep = success
        } else {
            Beep(500, 200); // Low beep = failed
        }
    }
    return TRUE;
}
