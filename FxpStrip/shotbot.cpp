#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include "shotbot.h"

#include <windows.h>

#include <cstdint>
#include <cstring>
#include <string>

namespace {

constexpr uintptr_t kCrosshairWrite1Rva = 0xD60FB;
constexpr uintptr_t kCrosshairWrite0Rva = 0xD6291;
constexpr uintptr_t kWeaponGunsSignatureRva = 0xD4227;
constexpr uintptr_t kWeaponSwordSignatureRva = 0xD835E;
constexpr DWORD kShotCooldownMs = 5;
constexpr DWORD kMouseHoldMs = 16;

volatile BYTE g_on_enemy = 0;
volatile BYTE g_weapon_is_gun = 1;
bool g_ready = false;
bool g_initialization_failed = false;
MemoryShotbotMode g_mode = MemoryShotbotMode::off;
bool g_mouse_down = false;
DWORD g_last_shot = 0;
DWORD g_mouse_down_at = 0;
std::string g_status = "Not initialized";

struct HookSite {
    uintptr_t address = 0;
    size_t length = 0;
    size_t patch_length = 0;
    BYTE original[16]{};
    BYTE* stub = nullptr;
    bool installed = false;
};

HookSite g_crosshair_write1;
HookSite g_crosshair_write0;
HookSite g_weapon_guns;
HookSite g_weapon_sword;

const BYTE kCrosshairWrite1[] = {
    0xC7, 0x82, 0xC0, 0x03, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00
};

const BYTE kCrosshairWrite0[] = {
    0xC7, 0x82, 0xC0, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
};

const BYTE kWeaponGunsSignature[] = {
    0x0F, 0xB6, 0x51, 0x08,
    0x3B, 0xC2,
    0x75, 0x02,
    0xEB, 0x07,
    0x8B, 0x45, 0xF8,
    0xC6, 0x40, 0x08, 0x01
};

const BYTE kWeaponSwordPrefix[] = {
    0x0F, 0xB6, 0x48, 0x08,
    0x3B, 0xD1,
    0x75, 0x02,
    0xEB, 0x0A,
    0x8B, 0x95
};

const BYTE kWeaponSwordSuffix[] = {
    0xC6, 0x42, 0x08, 0x00
};

bool safe_bytes_equal(uintptr_t address, const BYTE* expected, size_t length) {
    __try {
        return std::memcmp(reinterpret_cast<const void*>(address), expected, length) == 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool validate_signatures(uintptr_t game_base) {
    if (!safe_bytes_equal(game_base + kCrosshairWrite1Rva,
                          kCrosshairWrite1, sizeof(kCrosshairWrite1))) {
        g_status = "Crosshair write-1 signature mismatch";
        return false;
    }

    if (!safe_bytes_equal(game_base + kCrosshairWrite0Rva,
                          kCrosshairWrite0, sizeof(kCrosshairWrite0))) {
        g_status = "Crosshair write-0 signature mismatch";
        return false;
    }

    uintptr_t guns = game_base + kWeaponGunsSignatureRva;
    if (!safe_bytes_equal(guns, kWeaponGunsSignature, sizeof(kWeaponGunsSignature))) {
        g_status = "Weapon guns signature mismatch";
        return false;
    }

    uintptr_t sword = game_base + kWeaponSwordSignatureRva;
    if (!safe_bytes_equal(sword, kWeaponSwordPrefix, sizeof(kWeaponSwordPrefix)) ||
        !safe_bytes_equal(sword + 16, kWeaponSwordSuffix, sizeof(kWeaponSwordSuffix))) {
        g_status = "Weapon sword signature mismatch";
        return false;
    }

    return true;
}

bool prepare_site(HookSite& site, uintptr_t address, size_t length,
                  size_t patch_length, volatile BYTE* state, BYTE value) {
    if (length > sizeof(site.original)) {
        return false;
    }

    site.address = address;
    site.length = length;
    site.patch_length = patch_length;
    __try {
        std::memcpy(site.original, reinterpret_cast<const void*>(address), length);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }

    site.stub = static_cast<BYTE*>(VirtualAlloc(
        nullptr, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
    if (!site.stub) {
        return false;
    }

    BYTE* output = site.stub;

    // Match the working shotbot's stub exactly: mov byte ptr [state], value.
    *output++ = 0xC6;
    *output++ = 0x05;
    DWORD state_address = static_cast<DWORD>(reinterpret_cast<uintptr_t>(state));
    std::memcpy(output, &state_address, sizeof(state_address));
    output += sizeof(state_address);
    *output++ = value;

    std::memcpy(output, site.original, length);
    output += length;

    *output++ = 0xE9;
    DWORD relative_return = static_cast<DWORD>(
        (address + length) - (reinterpret_cast<uintptr_t>(output) + 4));
    std::memcpy(output, &relative_return, sizeof(relative_return));

    FlushInstructionCache(GetCurrentProcess(), site.stub, 64);
    return true;
}

DWORD writable_code_protection(uintptr_t address) {
    MEMORY_BASIC_INFORMATION memory{};
    if (VirtualQuery(reinterpret_cast<void*>(address), &memory, sizeof(memory)) !=
        sizeof(memory)) {
        return 0;
    }
    return PAGE_EXECUTE_READWRITE;
}

bool write_site_bytes(uintptr_t address, const BYTE* bytes, size_t length) {
    DWORD writable = writable_code_protection(address);
    if (!writable) {
        return false;
    }

    DWORD original_protection = 0;
    if (!VirtualProtect(reinterpret_cast<void*>(address), length,
                        writable, &original_protection)) {
        return false;
    }

    bool written = false;
    __try {
        std::memcpy(reinterpret_cast<void*>(address), bytes, length);
        written = std::memcmp(reinterpret_cast<void*>(address), bytes, length) == 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        written = false;
    }

    DWORD ignored = 0;
    bool restored = VirtualProtect(reinterpret_cast<void*>(address), length,
                                   original_protection, &ignored) != FALSE;
    if (written) {
        FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void*>(address), length);
    }
    return written && restored;
}

bool install_site(HookSite& site) {
    BYTE patch[16]{};
    std::memset(patch, 0x90, site.patch_length);
    patch[0] = 0xE9;

    DWORD relative_stub = static_cast<DWORD>(
        reinterpret_cast<uintptr_t>(site.stub) - (site.address + 5));
    std::memcpy(&patch[1], &relative_stub, sizeof(relative_stub));

    bool write_succeeded = write_site_bytes(site.address, patch, site.patch_length);
    site.installed = safe_bytes_equal(site.address, patch, site.patch_length);
    return write_succeeded && site.installed;
}

bool restore_site(HookSite& site) {
    if (site.installed) {
        bool restored = write_site_bytes(site.address, site.original, site.patch_length);
        site.installed = !safe_bytes_equal(site.address, site.original,
                                           site.patch_length);
        return restored && !site.installed;
    }
    return true;
}

void free_site(HookSite& site) {
    if (site.stub && !site.installed) {
        VirtualFree(site.stub, 0, MEM_RELEASE);
        site.stub = nullptr;
    }
}

void rollback_installation() {
    restore_site(g_weapon_sword);
    restore_site(g_weapon_guns);
    restore_site(g_crosshair_write0);
    restore_site(g_crosshair_write1);
    free_site(g_weapon_sword);
    free_site(g_weapon_guns);
    free_site(g_crosshair_write0);
    free_site(g_crosshair_write1);
}

bool prepare_all_sites(uintptr_t game_base) {
    g_crosshair_write1.address = game_base + kCrosshairWrite1Rva;
    g_crosshair_write0.address = game_base + kCrosshairWrite0Rva;

    uintptr_t guns_signature = game_base + kWeaponGunsSignatureRva;
    uintptr_t sword_signature = game_base + kWeaponSwordSignatureRva;

    return prepare_site(g_crosshair_write1, g_crosshair_write1.address, 10, 5,
                        &g_on_enemy, 1) &&
           prepare_site(g_crosshair_write0, g_crosshair_write0.address, 10, 5,
                        &g_on_enemy, 0) &&
           prepare_site(g_weapon_guns, guns_signature + 10, 7, 7,
                        &g_weapon_is_gun, 1) &&
           prepare_site(g_weapon_sword, sword_signature + 10, 10, 10,
                        &g_weapon_is_gun, 0);
}

bool install_all_sites() {
    if (!install_site(g_crosshair_write1) ||
        !install_site(g_crosshair_write0) ||
        !install_site(g_weapon_guns) ||
        !install_site(g_weapon_sword)) {
        rollback_installation();
        return false;
    }
    return true;
}

void release_mouse() {
    if (g_mouse_down) {
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
        g_mouse_down = false;
    }
}

} // namespace

bool InitializeMemoryShotbot(uintptr_t game_base) {
    if (g_ready) {
        return true;
    }
    if (!game_base) {
        g_status = "Game module unavailable";
        g_initialization_failed = true;
        return false;
    }

    if (!validate_signatures(game_base)) {
        g_initialization_failed = true;
        return false;
    }

    if (!prepare_all_sites(game_base)) {
        rollback_installation();
        g_status = "Unable to build hook stubs";
        g_initialization_failed = true;
        return false;
    }

    if (!install_all_sites()) {
        g_status = "Unable to install hook stubs";
        g_initialization_failed = true;
        return false;
    }

    g_on_enemy = 0;
    g_weapon_is_gun = 1;
    g_ready = true;
    g_initialization_failed = false;
    g_status = "Ready";
    return true;
}

bool IsMemoryShotbotReady() {
    return g_ready;
}

bool HasMemoryShotbotInitializationFailed() {
    return g_initialization_failed;
}

bool IsMemoryShotbotEnabled() {
    return g_mode == MemoryShotbotMode::firing;
}

bool IsMemoryShotbotOnEnemy() {
    return g_on_enemy != 0;
}

bool IsMemoryShotbotUsingGun() {
    return g_weapon_is_gun != 0;
}

const char* GetMemoryShotbotStatus() {
    return g_status.c_str();
}

MemoryShotbotMode GetMemoryShotbotMode() {
    return g_mode;
}

void SetMemoryShotbotMode(MemoryShotbotMode mode) {
    if (!g_ready && mode != MemoryShotbotMode::off) {
        return;
    }

    if (g_mode == MemoryShotbotMode::firing &&
        mode != MemoryShotbotMode::firing) {
        release_mouse();
    }

    g_mode = mode;
    switch (g_mode) {
    case MemoryShotbotMode::firing:
        g_status = "Enabled";
        break;
    case MemoryShotbotMode::detection_only:
        g_status = "Detection test";
        break;
    default:
        g_status = g_ready ? "Disabled" : "Not initialized";
        break;
    }
}

void DisableMemoryShotbot() {
    g_mode = MemoryShotbotMode::off;
    release_mouse();
    if (g_ready) {
        g_status = "Disabled";
    }
}

void TickMemoryShotbot() {
    DWORD now = GetTickCount();

    if (g_mouse_down) {
        if (now - g_mouse_down_at >= kMouseHoldMs) {
            release_mouse();
        }
        return;
    }

    if (!g_ready || g_mode != MemoryShotbotMode::firing ||
        !IsMemoryShotbotUsingGun() ||
        !IsMemoryShotbotOnEnemy()) {
        return;
    }

    if (now - g_last_shot < kShotCooldownMs) {
        return;
    }

    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
    g_mouse_down = true;
    g_mouse_down_at = now;
    g_last_shot = now;
}

void ShutdownMemoryShotbot() {
    DisableMemoryShotbot();
    rollback_installation();
    g_ready = false;
    g_initialization_failed = false;
    g_status = "Not initialized";
}
