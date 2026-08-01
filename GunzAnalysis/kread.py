"""Kernel read via CrxShield driver. Usage: python kread.py <pid> <hexaddr> <size>"""
import ctypes
import ctypes.wintypes as wt
import struct
import sys

IOCTL_READ = 0x224CE0

k = ctypes.windll.kernel32
k.CreateFileW.restype = wt.HANDLE
k.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p, wt.DWORD, wt.DWORD, wt.HANDLE]
k.DeviceIoControl.restype = wt.BOOL
k.DeviceIoControl.argtypes = [wt.HANDLE, wt.DWORD, ctypes.c_void_p, wt.DWORD, ctypes.c_void_p, wt.DWORD, ctypes.POINTER(wt.DWORD), ctypes.c_void_p]


def kread(h, pid, addr, size):
    req = struct.pack("<IQI", pid, addr, size)
    out = ctypes.create_string_buffer(size)
    ret = wt.DWORD(0)
    ok = k.DeviceIoControl(h, IOCTL_READ, req, len(req), out, size, ctypes.byref(ret), None)
    if not ok:
        return None
    return out.raw[: ret.value]


def main():
    pid = int(sys.argv[1])
    addr = int(sys.argv[2], 16)
    size = int(sys.argv[3]) if len(sys.argv) > 3 else 64
    h = k.CreateFileW("\\\\.\\CrxShield", 0xC0000000, 0, None, 3, 0x80, None)
    data = kread(h, pid, addr, size)
    if data is None:
        print(f"READ FAILED err={ctypes.GetLastError()}")
        sys.exit(1)
    for off in range(0, len(data), 16):
        chunk = data[off:off + 16]
        print(f"0x{addr + off:08X}  {chunk.hex(' ')}")


if __name__ == "__main__":
    main()
