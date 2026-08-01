#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include "gunz.h"
#include "hooks.h"
#include "shotbot.h"

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <conio.h>
#include <iomanip>
#include <iostream>
#include <limits>
#include <sstream>
#include <string>

namespace {

constexpr uintptr_t kGroundSlashOffset = 0x7909EC;
constexpr uintptr_t kOverclockOffset = 0x79FC90;
constexpr float kGroundSlashOff = 80.0f;
constexpr float kGroundSlashOn = 0.0f;
constexpr float kOverclockDefault = 4.800000191f;
constexpr float kOverclockMaximum = 7.0f;
constexpr float kStepPercent = 5.0f;
constexpr float kFloatTolerance = 0.0001f;
constexpr SHORT kShotbotStatusRow = 8;
constexpr SHORT kTargetStatusRow = 10;
constexpr SHORT kWeaponStatusRow = 11;

std::atomic_bool g_console_running{false};
HMODULE g_module = nullptr;
uintptr_t g_game_base = 0;
bool g_ground_slash_enabled = false;
bool g_overclock_enabled = false;
bool g_ninja_jump_enabled = false;
float g_selected_percent = 50.0f;

enum class WriteStage {
    none,
    query,
    validate,
    protect,
    write,
    verify,
    restore
};

struct WriteResult {
    bool success;
    WriteStage stage;
    DWORD error_code;
};

HANDLE output_handle() {
    return GetStdHandle(STD_OUTPUT_HANDLE);
}

void set_color(WORD attributes) {
    SetConsoleTextAttribute(output_handle(), attributes);
}

void clear_console() {
    HANDLE handle = output_handle();
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (!GetConsoleScreenBufferInfo(handle, &info)) {
        return;
    }

    DWORD cells = static_cast<DWORD>(info.dwSize.X) * static_cast<DWORD>(info.dwSize.Y);
    DWORD written = 0;
    COORD home{0, 0};
    FillConsoleOutputCharacterA(handle, ' ', cells, home, &written);
    FillConsoleOutputAttribute(handle, info.wAttributes, cells, home, &written);
    SetConsoleCursorPosition(handle, home);
}

void write_status_row(const char* hotkey, const char* name, const char* value,
                      WORD value_color) {
    set_color(FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE |
              FOREGROUND_INTENSITY);
    std::cout << "  " << std::left << std::setw(11) << hotkey << "| "
              << std::setw(22) << name << "| ";
    set_color(value_color);
    std::cout << std::left << std::setw(14) << value;
    set_color(FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE |
              FOREGROUND_INTENSITY);
    std::cout << '\n';
}

void write_dynamic_line(SHORT row, const char* hotkey, const char* name,
                        const char* value, WORD value_color) {
    HANDLE handle = output_handle();
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (!GetConsoleScreenBufferInfo(handle, &info)) {
        return;
    }

    COORD saved_cursor = info.dwCursorPosition;
    SetConsoleCursorPosition(handle, {0, row});
    set_color(FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE |
              FOREGROUND_INTENSITY);
    std::cout << "  " << std::left << std::setw(11) << hotkey << "| "
              << std::setw(22) << name << "| ";
    set_color(value_color);
    std::cout << std::left << std::setw(14) << value;
    set_color(FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE |
              FOREGROUND_INTENSITY);
    std::cout.flush();
    SetConsoleCursorPosition(handle, saved_cursor);
}

const char* shotbot_mode_text() {
    if (HasMemoryShotbotInitializationFailed()) {
        return "UNAVAILABLE";
    }

    switch (GetMemoryShotbotMode()) {
    case MemoryShotbotMode::firing:
        return "ON";
    case MemoryShotbotMode::detection_only:
        return "TEST";
    default:
        return "OFF";
    }
}

void render_dynamic_status() {
    MemoryShotbotMode mode = GetMemoryShotbotMode();
    WORD shotbot_color = FOREGROUND_RED | FOREGROUND_INTENSITY;
    if (mode == MemoryShotbotMode::firing) {
        shotbot_color = FOREGROUND_GREEN | FOREGROUND_INTENSITY;
    } else if (mode == MemoryShotbotMode::detection_only ||
               HasMemoryShotbotInitializationFailed()) {
        shotbot_color = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_INTENSITY;
    }
    write_dynamic_line(kShotbotStatusRow, "[F3/F4]", "Shotbot",
                       shotbot_mode_text(), shotbot_color);

    bool target_active = IsMemoryShotbotReady() && IsMemoryShotbotOnEnemy();
    write_dynamic_line(kTargetStatusRow, "--", "Target",
                       target_active ? "ACTIVE" : "CLEAR",
                       target_active
                           ? FOREGROUND_GREEN | FOREGROUND_INTENSITY
                           : FOREGROUND_RED | FOREGROUND_INTENSITY);

    const char* weapon = "UNKNOWN";
    WORD weapon_color = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_INTENSITY;
    if (IsMemoryShotbotReady()) {
        bool using_gun = IsMemoryShotbotUsingGun();
        weapon = using_gun ? "GUN" : "SWORD";
        weapon_color = using_gun
                           ? FOREGROUND_GREEN | FOREGROUND_INTENSITY
                           : FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_INTENSITY;
    }
    write_dynamic_line(kWeaponStatusRow, "--", "Weapon", weapon, weapon_color);
}

void border(char left, char fill, char right, int width = 66) {
    set_color(FOREGROUND_BLUE | FOREGROUND_GREEN | FOREGROUND_INTENSITY);
    std::cout << left << std::string(width, fill) << right << '\n';
}

void centered(const std::string& text, int width = 66) {
    int padding = std::max(0, (width - static_cast<int>(text.size())) / 2);
    set_color(FOREGROUND_BLUE | FOREGROUND_GREEN | FOREGROUND_INTENSITY);
    std::cout << '|';
    set_color(FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE | FOREGROUND_INTENSITY);
    std::cout << std::string(padding, ' ') << text
              << std::string(width - padding - static_cast<int>(text.size()), ' ');
    set_color(FOREGROUND_BLUE | FOREGROUND_GREEN | FOREGROUND_INTENSITY);
    std::cout << "|\n";
}

std::string progress_bar(float percent, int width = 32) {
    percent = std::clamp(percent, 0.0f, 100.0f);
    int filled = static_cast<int>(std::round((percent / 100.0f) * width));
    return "[" + std::string(filled, '#') + std::string(width - filled, '-') + "]";
}

std::string random_console_title() {
    static constexpr char alphabet[] =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
        "abcdefghijklmnopqrstuvwxyz"
        "0123456789";

    int stack_marker = 0;
    DWORD seed = GetTickCount();
    seed ^= GetCurrentProcessId() * 0x45D9F3Bu;
    seed ^= GetCurrentThreadId() * 0x119DE1F3u;
    seed ^= static_cast<DWORD>(reinterpret_cast<uintptr_t>(&stack_marker));

    auto next = [&seed]() {
        seed ^= seed << 13;
        seed ^= seed >> 17;
        seed ^= seed << 5;
        return seed;
    };

    size_t length = 12 + (next() % 13);
    std::string title;
    title.reserve(length);
    for (size_t i = 0; i < length; ++i) {
        title.push_back(alphabet[next() % (sizeof(alphabet) - 1)]);
    }
    return title;
}

bool resolve_game_base() {
    HMODULE module = GetModuleHandleA("Gunz.exe");
    if (!module) {
        module = GetModuleHandleA("GunZ.exe");
    }
    if (!module) {
        module = GetModuleHandleA(nullptr);
    }
    if (!module) {
        return false;
    }

    auto dos = reinterpret_cast<PIMAGE_DOS_HEADER>(module);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) {
        return false;
    }

    auto nt = reinterpret_cast<PIMAGE_NT_HEADERS>(
        reinterpret_cast<uintptr_t>(module) + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE ||
        nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR32_MAGIC ||
        nt->OptionalHeader.SizeOfImage <= kOverclockOffset + sizeof(float)) {
        return false;
    }

    g_game_base = reinterpret_cast<uintptr_t>(module);
    return true;
}

bool is_executable_protection(DWORD protection) {
    switch (protection & 0xFF) {
    case PAGE_EXECUTE:
    case PAGE_EXECUTE_READ:
    case PAGE_EXECUTE_READWRITE:
    case PAGE_EXECUTE_WRITECOPY:
        return true;
    default:
        return false;
    }
}

const char* write_stage_name(WriteStage stage) {
    switch (stage) {
    case WriteStage::query:
        return "memory query";
    case WriteStage::validate:
        return "address validation";
    case WriteStage::protect:
        return "protection change";
    case WriteStage::write:
        return "memory write";
    case WriteStage::verify:
        return "write verification";
    case WriteStage::restore:
        return "protection restore";
    default:
        return "unknown operation";
    }
}

WriteResult write_float(uintptr_t address_value, float value) {
    auto address = reinterpret_cast<float*>(address_value);
    MEMORY_BASIC_INFORMATION memory{};
    if (VirtualQuery(address, &memory, sizeof(memory)) != sizeof(memory)) {
        return {false, WriteStage::query, GetLastError()};
    }

    uintptr_t region_start = reinterpret_cast<uintptr_t>(memory.BaseAddress);
    uintptr_t region_end = region_start + memory.RegionSize;
    DWORD base_protection = memory.Protect & 0xFF;
    if (memory.State != MEM_COMMIT || address_value < region_start ||
        address_value + sizeof(float) > region_end || base_protection == PAGE_NOACCESS ||
        (memory.Protect & PAGE_GUARD) != 0) {
        return {false, WriteStage::validate, ERROR_NOACCESS};
    }

    DWORD writable_protection = PAGE_READWRITE;
    if (memory.Type == MEM_IMAGE) {
        writable_protection = PAGE_EXECUTE_WRITECOPY;
    } else if (is_executable_protection(memory.Protect)) {
        writable_protection = PAGE_EXECUTE_READWRITE;
    }

    DWORD old_protection = 0;
    if (!VirtualProtect(address, sizeof(float), writable_protection, &old_protection)) {
        return {false, WriteStage::protect, GetLastError()};
    }

    SIZE_T written = 0;
    BOOL write_ok = WriteProcessMemory(
        GetCurrentProcess(), address, &value, sizeof(value), &written);
    DWORD write_error = write_ok ? ERROR_SUCCESS : GetLastError();

    bool verified = false;
    if (write_ok && written == sizeof(value)) {
        __try {
            verified = (*address == value);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            write_error = GetExceptionCode();
        }
    }

    DWORD ignored = 0;
    if (!VirtualProtect(address, sizeof(float), old_protection, &ignored)) {
        return {false, WriteStage::restore, GetLastError()};
    }

    if (!write_ok || written != sizeof(value)) {
        if (write_error == ERROR_SUCCESS) {
            write_error = ERROR_WRITE_FAULT;
        }
        return {false, WriteStage::write, write_error};
    }
    if (!verified) {
        return {false, WriteStage::verify,
                write_error == ERROR_SUCCESS ? ERROR_WRITE_FAULT : write_error};
    }

    return {true, WriteStage::none, ERROR_SUCCESS};
}

float read_float(uintptr_t address) {
    __try {
        return *reinterpret_cast<float*>(address);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return std::numeric_limits<float>::quiet_NaN();
    }
}

float value_from_percent(float percent) {
    percent = std::clamp(percent, 0.0f, 100.0f);
    return kOverclockDefault +
           ((kOverclockMaximum - kOverclockDefault) * (percent / 100.0f));
}

float percent_from_value(float value) {
    return ((value - kOverclockDefault) /
            (kOverclockMaximum - kOverclockDefault)) * 100.0f;
}

bool approximately(float left, float right) {
    return std::fabs(left - right) <= kFloatTolerance;
}

std::string failure_status(const WriteResult& result) {
    std::ostringstream text;
    text << "Write failed during " << write_stage_name(result.stage) << " (0x"
         << std::hex << std::uppercase << result.error_code << ").";
    return text.str();
}

void initialize_state() {
    float ground = read_float(g_game_base + kGroundSlashOffset);
    g_ground_slash_enabled = !std::isnan(ground) && approximately(ground, kGroundSlashOn);

    float overclock = read_float(g_game_base + kOverclockOffset);
    if (!std::isnan(overclock) && overclock > kOverclockDefault + kFloatTolerance &&
        overclock <= kOverclockMaximum + kFloatTolerance) {
        g_overclock_enabled = true;
        g_selected_percent = std::clamp(percent_from_value(overclock), 0.0f, 100.0f);
    } else {
        g_overclock_enabled = false;
        g_selected_percent = 50.0f;
    }

    g_ninja_jump_enabled = IsNinjaJumpEnabled();
}

bool toggle_ground_slash(std::string& status) {
    bool next_state = !g_ground_slash_enabled;
    WriteResult result = write_float(
        g_game_base + kGroundSlashOffset,
        next_state ? kGroundSlashOn : kGroundSlashOff);
    if (!result.success) {
        status = failure_status(result);
        return false;
    }

    g_ground_slash_enabled = next_state;
    status = std::string("Ground Slash ") + (next_state ? "enabled." : "disabled.");
    return true;
}

bool toggle_overclock(std::string& status) {
    bool next_state = !g_overclock_enabled;
    float value = next_state ? value_from_percent(g_selected_percent) : kOverclockDefault;
    WriteResult result = write_float(g_game_base + kOverclockOffset, value);
    if (!result.success) {
        status = failure_status(result);
        return false;
    }

    g_overclock_enabled = next_state;
    status = std::string("Overclock ") + (next_state ? "enabled." : "disabled.");
    return true;
}

bool toggle_ninja_jump(std::string& status) {
    bool next_state = !g_ninja_jump_enabled;
    SetNinjaJump(next_state);
    g_ninja_jump_enabled = next_state;
    status = std::string("Ninja Jump ") + (next_state ? "enabled." : "disabled.");
    return true;
}

bool adjust_overclock(float amount, std::string& status) {
    float next_percent = std::clamp(g_selected_percent + amount, 0.0f, 100.0f);
    WriteResult result = write_float(
        g_game_base + kOverclockOffset, value_from_percent(next_percent));
    if (!result.success) {
        status = failure_status(result);
        return false;
    }

    g_selected_percent = next_percent;
    g_overclock_enabled = true;
    std::ostringstream text;
    text << "Overclock set to " << std::fixed << std::setprecision(0)
         << g_selected_percent << "%.";
    status = text.str();
    return true;
}

void render(const std::string& status, bool status_ok) {
    clear_console();

    border('+', '=', '+');
    centered("CurseX - ZxPwds Overclock");
    border('+', '=', '+');

    set_color(FOREGROUND_GREEN | FOREGROUND_BLUE | FOREGROUND_INTENSITY);
    std::cout << "\n  " << std::left << std::setw(11) << "HOTKEY" << "| "
              << std::setw(22) << "NAME" << "| STATUS\n";
    std::cout << "  " << std::string(11, '-') << "+-"
              << std::string(22, '-') << "+-" << std::string(14, '-') << '\n';

    write_status_row("[F1]", "Ground Slash",
                     g_ground_slash_enabled ? "ON" : "OFF",
                     g_ground_slash_enabled
                         ? FOREGROUND_GREEN | FOREGROUND_INTENSITY
                         : FOREGROUND_RED | FOREGROUND_INTENSITY);
    write_status_row("[F2]", "Overclock",
                     g_overclock_enabled ? "ON" : "OFF",
                     g_overclock_enabled
                         ? FOREGROUND_GREEN | FOREGROUND_INTENSITY
                         : FOREGROUND_RED | FOREGROUND_INTENSITY);
    write_status_row("[F3/F4]", "Shotbot", shotbot_mode_text(),
                     HasMemoryShotbotInitializationFailed()
                         ? FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_INTENSITY
                         : FOREGROUND_RED | FOREGROUND_INTENSITY);
    write_status_row("[F5]", "Ninja Jump",
                     g_ninja_jump_enabled ? "ON" : "OFF",
                     g_ninja_jump_enabled
                         ? FOREGROUND_GREEN | FOREGROUND_INTENSITY
                         : FOREGROUND_RED | FOREGROUND_INTENSITY);
    write_status_row("--", "Target",
                     IsMemoryShotbotReady() && IsMemoryShotbotOnEnemy()
                         ? "ACTIVE" : "CLEAR",
                     IsMemoryShotbotReady() && IsMemoryShotbotOnEnemy()
                         ? FOREGROUND_GREEN | FOREGROUND_INTENSITY
                         : FOREGROUND_RED | FOREGROUND_INTENSITY);
    write_status_row("--", "Weapon",
                     IsMemoryShotbotReady()
                         ? (IsMemoryShotbotUsingGun() ? "GUN" : "SWORD")
                         : "UNKNOWN",
                     FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_INTENSITY);

    set_color(FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE | FOREGROUND_INTENSITY);
    std::cout << "\n  Overclock Level  ";
    set_color(FOREGROUND_GREEN | FOREGROUND_INTENSITY);
    std::cout << progress_bar(g_selected_percent) << ' '
              << std::fixed << std::setprecision(0) << g_selected_percent << "%\n";

    border('+', '-', '+');
    set_color(FOREGROUND_GREEN | FOREGROUND_BLUE | FOREGROUND_INTENSITY);
    std::cout << "  HOTKEY     | ACTION\n";
    std::cout << "  -----------+-------------------------------------------\n";
    std::cout << "  [F3]       | Toggle Shotbot firing\n";
    std::cout << "  [F4]       | Toggle detection test\n";
    std::cout << "  [-]        | Decrease overclock by 5%\n";
    std::cout << "  [+]        | Increase overclock by 5%\n";
    set_color(FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_INTENSITY);
    std::cout << "  [Q]        | Close console\n";
    border('+', '-', '+');

    if (!status.empty()) {
        set_color(status_ok
                      ? FOREGROUND_GREEN | FOREGROUND_INTENSITY
                      : FOREGROUND_RED | FOREGROUND_INTENSITY);
        std::cout << "\n  " << status << '\n';
    }

    set_color(FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE | FOREGROUND_INTENSITY);
    std::cout << "\n  Hotkeys work while the game is focused.";
    render_dynamic_status();
}

bool key_pressed(int virtual_key) {
    return (GetAsyncKeyState(virtual_key) & 0x0001) != 0;
}

} // namespace

HWND g_hWnd = nullptr;

void SetConsoleModule(HMODULE module) {
    g_module = module;
}

void RestoreAll() {
    ShutdownMemoryShotbot();

    if (!g_game_base) {
        return;
    }

    if (g_ground_slash_enabled) {
        write_float(g_game_base + kGroundSlashOffset, kGroundSlashOff);
        g_ground_slash_enabled = false;
    }
    if (g_overclock_enabled) {
        write_float(g_game_base + kOverclockOffset, kOverclockDefault);
        g_overclock_enabled = false;
    }
    if (g_ninja_jump_enabled) {
        SetNinjaJump(false);
        g_ninja_jump_enabled = false;
    }
}

DWORD WINAPI ConsoleThread(LPVOID) {
    if (g_console_running.exchange(true)) {
        return 0;
    }

    if (!AllocConsole()) {
        g_console_running.store(false);
        return 1;
    }

    FILE* stream = nullptr;
    freopen_s(&stream, "CONOUT$", "w", stdout);
    freopen_s(&stream, "CONIN$", "r", stdin);

    std::string title = random_console_title();
    SetConsoleTitleA(title.c_str());

    std::string status = "Waiting for game initialization...";
    bool status_ok = true;
    render(status, status_ok);

    while (!resolve_game_base() || !IsMemoryWritePatchReady()) {
        if (_kbhit()) {
            int key = _getch();
            if (key == 'q' || key == 'Q') {
                DisableMemoryShotbot();
                FreeConsole();
                g_console_running.store(false);
                return 0;
            }
        }
        Sleep(100);
    }

    initialize_state();
    status = "Ready.";
    status_ok = true;
    render(status, status_ok);

    bool running = true;
    MemoryShotbotMode displayed_mode = GetMemoryShotbotMode();
    bool displayed_target = IsMemoryShotbotOnEnemy();
    bool displayed_weapon = IsMemoryShotbotUsingGun();
    while (running) {
        bool changed = false;

        if (key_pressed(VK_F1)) {
            status_ok = toggle_ground_slash(status);
            changed = true;
        }
        if (key_pressed(VK_F2)) {
            status_ok = toggle_overclock(status);
            changed = true;
        }
        if (key_pressed(VK_F3)) {
            if (!IsMemoryShotbotReady() && !HasMemoryShotbotInitializationFailed()) {
                status_ok = InitializeMemoryShotbot(g_game_base);
            }

            if (IsMemoryShotbotReady()) {
                MemoryShotbotMode next_mode =
                    GetMemoryShotbotMode() == MemoryShotbotMode::firing
                        ? MemoryShotbotMode::off
                        : MemoryShotbotMode::firing;
                SetMemoryShotbotMode(next_mode);
                status_ok = true;
                status = std::string("Shotbot ") +
                         (IsMemoryShotbotEnabled() ? "enabled." : "disabled.");
            } else {
                status_ok = false;
                status = std::string("Shotbot unavailable: ") +
                         GetMemoryShotbotStatus() + ".";
            }
            changed = true;
        }
        if (key_pressed(VK_F4)) {
            if (!IsMemoryShotbotReady() && !HasMemoryShotbotInitializationFailed()) {
                status_ok = InitializeMemoryShotbot(g_game_base);
            }

            if (IsMemoryShotbotReady()) {
                MemoryShotbotMode next_mode =
                    GetMemoryShotbotMode() == MemoryShotbotMode::detection_only
                        ? MemoryShotbotMode::off
                        : MemoryShotbotMode::detection_only;
                SetMemoryShotbotMode(next_mode);
                status_ok = true;
                status = next_mode == MemoryShotbotMode::detection_only
                             ? "Detection test enabled; mouse clicks are blocked."
                             : "Detection test disabled.";
            } else {
                status_ok = false;
                status = std::string("Shotbot unavailable: ") +
                         GetMemoryShotbotStatus() + ".";
            }
            changed = true;
        }
        if (key_pressed(VK_F5)) {
            status_ok = toggle_ninja_jump(status);
            changed = true;
        }

        bool minus_pressed = key_pressed(VK_SUBTRACT) || key_pressed(VK_OEM_MINUS);
        bool plus_pressed = key_pressed(VK_ADD);
        bool main_plus_pressed = key_pressed(VK_OEM_PLUS);
        if (main_plus_pressed && (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) {
            plus_pressed = true;
        }

        if (minus_pressed) {
            status_ok = adjust_overclock(-kStepPercent, status);
            changed = true;
        }
        if (plus_pressed) {
            status_ok = adjust_overclock(kStepPercent, status);
            changed = true;
        }

        if (_kbhit()) {
            int key = _getch();
            if (key == 'q' || key == 'Q') {
                DisableMemoryShotbot();
                running = false;
            }
        }

        TickMemoryShotbot();

        if (changed) {
            render(status, status_ok);
        }

        MemoryShotbotMode current_mode = GetMemoryShotbotMode();
        bool current_target = IsMemoryShotbotOnEnemy();
        bool current_weapon = IsMemoryShotbotUsingGun();
        if (!changed && (current_mode != displayed_mode ||
                         current_target != displayed_target ||
                         current_weapon != displayed_weapon)) {
            render_dynamic_status();
        }
        displayed_mode = current_mode;
        displayed_target = current_target;
        displayed_weapon = current_weapon;
        Sleep(1);
    }

    FreeConsole();
    g_console_running.store(false);
    return 0;
}
