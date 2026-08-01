/*
 * CrxShieldClient.cpp — Native client implementation for the CrxShield driver.
 *
 * Mirrors CrxMem/CrxDriverController.cs exactly:
 *   - CTL_CODE(DeviceType=0x22, Function=0x1337+n, Method=0, Access=0)
 *   - All request structs pack=1
 *   - GET_PROCESS_BASE sends/expects the 12-byte struct {ULONG; ULONG64}
 */

/* CRXSHIELD_CLIENT_EXPORTS is also defined in the project file for this TU;
 * defining it here as well guarantees dllexport regardless of build setup. */
#ifndef CRXSHIELD_CLIENT_EXPORTS
#define CRXSHIELD_CLIENT_EXPORTS
#endif
#include "CrxShieldClient.h"

/* ---- IOCTL codes ------------------------------------------------------- */

#define CRX_FILE_DEVICE_UNKNOWN 0x00000022
#define CRX_METHOD_BUFFERED     0
#define CRX_FILE_ANY_ACCESS     0
#define CRX_IOCTL_BASE          0x1337

#define CRX_CTL_CODE(DeviceType, Function, Method, Access) \
    (ULONG)(((DeviceType) << 16) | ((Access) << 14) | ((Function) << 2) | (Method))

#define IOCTL_CRXSHIELD_GET_VERSION     CRX_CTL_CODE(CRX_FILE_DEVICE_UNKNOWN, CRX_IOCTL_BASE + 0x0, CRX_METHOD_BUFFERED, CRX_FILE_ANY_ACCESS)
#define IOCTL_CRXSHIELD_READ_MEMORY     CRX_CTL_CODE(CRX_FILE_DEVICE_UNKNOWN, CRX_IOCTL_BASE + 0x1, CRX_METHOD_BUFFERED, CRX_FILE_ANY_ACCESS)
#define IOCTL_CRXSHIELD_WRITE_MEMORY    CRX_CTL_CODE(CRX_FILE_DEVICE_UNKNOWN, CRX_IOCTL_BASE + 0x2, CRX_METHOD_BUFFERED, CRX_FILE_ANY_ACCESS)
#define IOCTL_CRXSHIELD_GET_PROCESS_BASE CRX_CTL_CODE(CRX_FILE_DEVICE_UNKNOWN, CRX_IOCTL_BASE + 0x3, CRX_METHOD_BUFFERED, CRX_FILE_ANY_ACCESS)

#define CRX_MAX_TRANSFER_SIZE (16UL * 1024UL * 1024UL) /* 16 MB, matches driver */

/* ---- Request/response structs (pack=1, mirror the driver) -------------- */

#pragma pack(push, 1)

typedef struct _CRXSHIELD_VERSION {
    ULONG Major;
    ULONG Minor;
    ULONG Build;
} CRXSHIELD_VERSION;

typedef struct _CRXSHIELD_READ_REQUEST {
    ULONG   ProcessId;
    ULONG64 Address;
    ULONG   Size;
} CRXSHIELD_READ_REQUEST;

typedef struct _CRXSHIELD_WRITE_REQUEST {
    ULONG   ProcessId;
    ULONG64 Address;
    ULONG   Size;
    /* payload bytes follow immediately */
} CRXSHIELD_WRITE_REQUEST;

typedef struct _CRXSHIELD_PROCESS_BASE_REQUEST {
    ULONG   ProcessId;
    ULONG64 BaseAddress;
} CRXSHIELD_PROCESS_BASE_REQUEST;

#pragma pack(pop)

/* ---- Handle context ----------------------------------------------------- */

typedef struct CRX_CONTEXT_TAG {
    HANDLE Device;     /* \\.\CrxShield */
    DWORD  LastError;  /* per-handle last Win32 error */
} CRX_CONTEXT, *PCRX_CONTEXT;

/* ---- Helpers ------------------------------------------------------------ */

static void CrxSetError(CRX_HANDLE h, DWORD err)
{
    if (h != NULL)
        h->LastError = err;
}

static BOOL CrxValidate(CRX_HANDLE h)
{
    if (h == NULL || h->Device == NULL || h->Device == INVALID_HANDLE_VALUE)
        return FALSE;
    return TRUE;
}

/* ---- API ---------------------------------------------------------------- */

CRX_HANDLE CrxConnect(void)
{
    PCRX_CONTEXT ctx;
    HANDLE dev;

    dev = CreateFileW(L"\\\\.\\CrxShield",
                      GENERIC_READ | GENERIC_WRITE,
                      FILE_SHARE_READ | FILE_SHARE_WRITE,
                      NULL,
                      OPEN_EXISTING,
                      FILE_ATTRIBUTE_NORMAL,
                      NULL);
    if (dev == INVALID_HANDLE_VALUE)
        return NULL; /* caller uses Win32 GetLastError() */

    ctx = (PCRX_CONTEXT)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(CRX_CONTEXT));
    if (ctx == NULL)
    {
        DWORD err = GetLastError();
        CloseHandle(dev);
        SetLastError(err);
        return NULL;
    }

    ctx->Device = dev;
    ctx->LastError = ERROR_SUCCESS;
    return (CRX_HANDLE)ctx;
}

void CrxDisconnect(CRX_HANDLE h)
{
    if (h == NULL)
        return;
    if (h->Device != NULL && h->Device != INVALID_HANDLE_VALUE)
        CloseHandle(h->Device);
    HeapFree(GetProcessHeap(), 0, h);
}

DWORD CrxGetLastError(CRX_HANDLE h)
{
    if (h == NULL)
        return ERROR_INVALID_HANDLE;
    return h->LastError;
}

BOOL CrxGetVersion(CRX_HANDLE h, ULONG* major, ULONG* minor, ULONG* build)
{
    CRXSHIELD_VERSION ver = { 0 };
    DWORD bytesReturned = 0;

    if (major == NULL || minor == NULL || build == NULL)
    {
        CrxSetError(h, ERROR_INVALID_PARAMETER);
        return FALSE;
    }
    *major = 0; *minor = 0; *build = 0;

    if (!CrxValidate(h))
    {
        CrxSetError(h, ERROR_INVALID_HANDLE);
        return FALSE;
    }

    if (!DeviceIoControl(h->Device, IOCTL_CRXSHIELD_GET_VERSION,
                         NULL, 0,
                         &ver, (DWORD)sizeof(ver),
                         &bytesReturned, NULL))
    {
        CrxSetError(h, GetLastError());
        return FALSE;
    }

    *major = ver.Major;
    *minor = ver.Minor;
    *build = ver.Build;
    CrxSetError(h, ERROR_SUCCESS);
    return TRUE;
}

BOOL CrxReadMemory(CRX_HANDLE h, ULONG pid, ULONG64 address, PVOID buffer, ULONG size)
{
    CRXSHIELD_READ_REQUEST req;
    DWORD bytesReturned = 0;

    if (buffer == NULL || size == 0 || size > CRX_MAX_TRANSFER_SIZE)
    {
        CrxSetError(h, ERROR_INVALID_PARAMETER);
        return FALSE;
    }
    if (!CrxValidate(h))
    {
        CrxSetError(h, ERROR_INVALID_HANDLE);
        return FALSE;
    }

    req.ProcessId = pid;
    req.Address   = address;
    req.Size      = size;

    if (!DeviceIoControl(h->Device, IOCTL_CRXSHIELD_READ_MEMORY,
                         &req, (DWORD)sizeof(req),
                         buffer, size,
                         &bytesReturned, NULL))
    {
        CrxSetError(h, GetLastError());
        return FALSE;
    }

    CrxSetError(h, ERROR_SUCCESS);
    return TRUE;
}

BOOL CrxWriteMemory(CRX_HANDLE h, ULONG pid, ULONG64 address, const PVOID buffer, ULONG size)
{
    CRXSHIELD_WRITE_REQUEST* req = NULL;
    DWORD bytesReturned = 0;
    SIZE_T totalSize;
    BOOL ok;

    if (buffer == NULL || size == 0 || size > CRX_MAX_TRANSFER_SIZE)
    {
        CrxSetError(h, ERROR_INVALID_PARAMETER);
        return FALSE;
    }
    if (!CrxValidate(h))
    {
        CrxSetError(h, ERROR_INVALID_HANDLE);
        return FALSE;
    }

    totalSize = sizeof(CRXSHIELD_WRITE_REQUEST) + (SIZE_T)size;
    req = (CRXSHIELD_WRITE_REQUEST*)HeapAlloc(GetProcessHeap(), 0, totalSize);
    if (req == NULL)
    {
        CrxSetError(h, GetLastError());
        return FALSE;
    }

    req->ProcessId = pid;
    req->Address   = address;
    req->Size      = size;
    memcpy((PUCHAR)req + sizeof(CRXSHIELD_WRITE_REQUEST), buffer, size);

    ok = DeviceIoControl(h->Device, IOCTL_CRXSHIELD_WRITE_MEMORY,
                         req, (DWORD)totalSize,
                         NULL, 0,
                         &bytesReturned, NULL);

    HeapFree(GetProcessHeap(), 0, req);

    if (!ok)
    {
        CrxSetError(h, GetLastError());
        return FALSE;
    }

    CrxSetError(h, ERROR_SUCCESS);
    return TRUE;
}

BOOL CrxGetProcessBase(CRX_HANDLE h, ULONG pid, ULONG64* baseAddress)
{
    CRXSHIELD_PROCESS_BASE_REQUEST req;
    DWORD bytesReturned = 0;

    if (baseAddress == NULL)
    {
        CrxSetError(h, ERROR_INVALID_PARAMETER);
        return FALSE;
    }
    *baseAddress = 0;

    if (!CrxValidate(h))
    {
        CrxSetError(h, ERROR_INVALID_HANDLE);
        return FALSE;
    }

    /* Mirror the C# reference: 12-byte struct used for both input and output. */
    req.ProcessId  = pid;
    req.BaseAddress = 0;

    if (!DeviceIoControl(h->Device, IOCTL_CRXSHIELD_GET_PROCESS_BASE,
                         &req, (DWORD)sizeof(req),
                         &req, (DWORD)sizeof(req),
                         &bytesReturned, NULL))
    {
        CrxSetError(h, GetLastError());
        return FALSE;
    }

    *baseAddress = req.BaseAddress;
    CrxSetError(h, ERROR_SUCCESS);
    return TRUE;
}
