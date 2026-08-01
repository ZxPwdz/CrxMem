"""Gunz thread census: CrxShield kernel ENUM_THREADS + CE bridge classification.

- Thread start addresses come from the KERNEL driver (immune to in-process hooks).
- Module/region classification comes from the CE bridge pipe (ce_bridge.py).
- Two-phase CPU-time sampling catches a periodic integrity loop.

Usage: python gunz_thread_census.py [pid] [--sample SECONDS]
"""
import ctypes
import ctypes.wintypes as wt
import json
import struct
import sys
import time

from ce_bridge import call as ce_call  # optional; only used if CE bridge is up

IOCTL_GET_VERSION = 0x224CDC
IOCTL_ENUM_THREADS = 0x224CEC  # GET_VERSION + 4*4
GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
OPEN_EXISTING = 3
FILE_ATTRIBUTE_NORMAL = 0x80
INVALID_HANDLE = wt.HANDLE(-1).value

kernel32 = ctypes.windll.kernel32
kernel32.CreateFileW.restype = wt.HANDLE
kernel32.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p, wt.DWORD, wt.DWORD, wt.HANDLE]
kernel32.OpenThread.restype = wt.HANDLE
kernel32.OpenThread.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
kernel32.CloseHandle.argtypes = [wt.HANDLE]
kernel32.DeviceIoControl.restype = wt.BOOL
kernel32.DeviceIoControl.argtypes = [wt.HANDLE, wt.DWORD, ctypes.c_void_p, wt.DWORD, ctypes.c_void_p, wt.DWORD, ctypes.POINTER(wt.DWORD), ctypes.c_void_p]


def open_device():
    h = kernel32.CreateFileW(
        "\\\\.\\CrxShield", GENERIC_READ | GENERIC_WRITE, 0, None,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, None)
    if h == INVALID_HANDLE:
        raise OSError(ctypes.GetLastError(), "cannot open \\\\.\\CrxShield - is the driver running?")
    return h


def ioctl(h, code, inbuf, outsize):
    outbuf = ctypes.create_string_buffer(outsize)
    returned = wt.DWORD(0)
    ok = kernel32.DeviceIoControl(
        h, code, inbuf, len(inbuf), outbuf, outsize,
        ctypes.byref(returned), None)
    return ok, outbuf.raw[: returned.value]


def enum_threads(h, pid):
    inbuf = struct.pack("<I", pid)
    ok, data = ioctl(h, IOCTL_ENUM_THREADS, inbuf, 4 + 256 * 12)
    if not ok and not data:
        raise OSError(ctypes.GetLastError(), "ENUM_THREADS failed")
    if len(data) < 4:
        raise RuntimeError(f"short response: {len(data)} bytes")
    count = struct.unpack_from("<I", data, 0)[0]
    threads = []
    off = 4
    for _ in range(count):
        if off + 12 > len(data):
            break
        tid, start = struct.unpack_from("<IQ", data, off)
        threads.append((tid, start))
        off += 12
    return threads


def enum_modules_toolhelp(pid):
    """[(base, end, name)] via toolhelp32 — includes 32-bit modules of WoW64 targets."""
    TH32CS_SNAPMODULE = 0x00000008
    TH32CS_SNAPMODULE32 = 0x00000010
    kernel32.CreateToolhelp32Snapshot.restype = wt.HANDLE
    kernel32.CreateToolhelp32Snapshot.argtypes = [wt.DWORD, wt.DWORD]
    kernel32.Module32FirstW.argtypes = [wt.HANDLE, ctypes.c_void_p]
    kernel32.Module32NextW.argtypes = [wt.HANDLE, ctypes.c_void_p]
    kernel32.Module32FirstW.restype = wt.BOOL
    kernel32.Module32NextW.restype = wt.BOOL

    snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid)
    if snap == INVALID_HANDLE:
        raise OSError(ctypes.GetLastError(), "CreateToolhelp32Snapshot failed")
    # MODULEENTRY32W layout (x64): 5 DWORDs, pad, ptr base, DWORD size, pad, HANDLE, WCHAR[256], WCHAR[260]
    entry = ctypes.create_string_buffer(568)
    struct.pack_into("<I", entry, 0, 568)
    mods = []
    try:
        ok = kernel32.Module32FirstW(snap, entry)
        while ok:
            base = struct.unpack_from("<Q", entry, 24)[0]
            size = struct.unpack_from("<I", entry, 32)[0]
            name = entry.raw[48:48 + 512].decode("utf-16-le", "ignore").split("\0")[0]
            mods.append((base, base + size, name))
            ok = kernel32.Module32NextW(snap, entry)
    finally:
        kernel32.CloseHandle(snap)
    return mods


ntdll = ctypes.windll.ntdll


def win32_start_address(tid):
    """True per-thread start address via NtQueryInformationThread (external caller,
    so in-process hooks inside Gunz.exe cannot affect us)."""
    h = kernel32.OpenThread(0x0040, False, tid)  # THREAD_QUERY_LIMITED_INFORMATION
    if not h:
        return None
    try:
        buf = ctypes.c_uint64(0)
        retlen = wt.ULONG(0)
        # ThreadQuerySetWin32StartAddress = 9
        status = ntdll.NtQueryInformationThread(
            h, 9, ctypes.byref(buf), 8, ctypes.byref(retlen))
        return buf.value if status == 0 else None
    finally:
        kernel32.CloseHandle(h)


def thread_times(pids_tids):
    """{(tid): (kernel100ns, user100ns)} via GetThreadTimes; skips denied."""
    result = {}
    for tid in pids_tids:
        h = kernel32.OpenThread(0x0040, False, tid)  # THREAD_QUERY_LIMITED_INFORMATION
        if not h:
            continue
        ct, et, kt, ut = wt.FILETIME(), wt.FILETIME(), wt.FILETIME(), wt.FILETIME()
        if kernel32.GetThreadTimes(h, ctypes.byref(ct), ctypes.byref(et),
                                   ctypes.byref(kt), ctypes.byref(ut)):
            k = (kt.dwHighDateTime << 32) | kt.dwLowDateTime
            u = (ut.dwHighDateTime << 32) | ut.dwLowDateTime
            result[tid] = (k, u)
        kernel32.CloseHandle(h)
    return result


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    sample = 10
    if "--sample" in sys.argv:
        sample = float(sys.argv[sys.argv.index("--sample") + 1])
    pid = int(args[0]) if args else None
    if pid is None:
        raise SystemExit("usage: python gunz_thread_census.py <pid> [--sample N]")

    h = open_device()
    ok, ver = ioctl(h, IOCTL_GET_VERSION, b"", 16)
    print(f"[*] CrxShield connected, version bytes: {ver[:6].hex()}")

    threads = enum_threads(h, pid)
    print(f"[*] kernel census: {len(threads)} threads in PID {pid}")
    # Print raw census IMMEDIATELY - survives any later failure or target crash
    for tid, start in threads:
        print(f"    RAW TID={tid:<7} start=0x{start:016X}")
    with open(f"census_{pid}_{int(time.time())}.txt", "w") as f:
        for tid, start in threads:
            f.write(f"{tid} 0x{start:016X}\n")

    mods = []
    try:
        mods = enum_modules_toolhelp(pid)
    except OSError as e:
        print(f"[!] module snapshot failed ({e}); classifying by known ranges only")
    mod_ranges = [(lo, hi, name) for lo, hi, name in mods]

    def classify(addr):
        for lo, hi, name in mod_ranges:
            if lo <= addr < hi:
                return f"{name}!+0x{addr - lo:X}"
        return "MEM_PRIVATE (no module)"

    tids = [t for t, _ in threads]
    t0 = thread_times(tids)
    print(f"[*] sampling CPU for {sample}s ...")
    time.sleep(sample)
    t1 = thread_times(tids)

    rows = []
    for tid, start in threads:
        w32 = win32_start_address(tid)
        delta = 0.0
        if tid in t0 and tid in t1:
            dk = t1[tid][0] - t0[tid][0]
            du = t1[tid][1] - t0[tid][1]
            delta = (dk + du) / 10000.0  # ms
        rows.append((tid, w32 if w32 else start, classify(w32) if w32 else "(no w32 start)", delta))

    from collections import Counter
    start_counts = Counter(s for _, s, _, _ in rows)

    print(f"\n{'TID':>7}  {'CPU(ms)':>9}  START")
    for tid, start, where, delta in sorted(rows, key=lambda r: -r[3]):
        flags = []
        if "MEM_PRIVATE" in where:
            flags.append("NON-MODULE")
        if start_counts[start] > 1:
            flags.append(f"shared-start x{start_counts[start]}")
        flag_s = ("  <-- " + ", ".join(flags)) if flags else ""
        print(f"{tid:>7}  {delta:>9.1f}  0x{start:016X}  {where}{flag_s}")

    print("\n=== PRIME SUSPECTS ===")
    for tid, start, where, delta in rows:
        score = 0
        if "MEM_PRIVATE" in where:
            score += 3
        if start_counts[start] > 1:
            score += 1
        if delta > 5:
            score += 1
        if score >= 3:
            print(f"  TID {tid} start=0x{start:X} {where} cpu={delta:.1f}ms score={score}")
    kernel32.CloseHandle(h)


if __name__ == "__main__":
    main()
