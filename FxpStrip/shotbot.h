#pragma once

#include <cstdint>

enum class MemoryShotbotMode : uint8_t {
    off,
    detection_only,
    firing
};

bool InitializeMemoryShotbot(uintptr_t game_base);
bool IsMemoryShotbotReady();
bool HasMemoryShotbotInitializationFailed();
bool IsMemoryShotbotEnabled();
bool IsMemoryShotbotOnEnemy();
bool IsMemoryShotbotUsingGun();
MemoryShotbotMode GetMemoryShotbotMode();
const char* GetMemoryShotbotStatus();
void SetMemoryShotbotMode(MemoryShotbotMode mode);
void DisableMemoryShotbot();
void TickMemoryShotbot();
void ShutdownMemoryShotbot();
