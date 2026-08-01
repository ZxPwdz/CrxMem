#pragma once

#include <Windows.h>

void SetConsoleModule(HMODULE hMod);
DWORD WINAPI ConsoleThread(LPVOID);
void RestoreAll();
