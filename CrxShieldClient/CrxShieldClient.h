#pragma once

/*
 * CrxShieldClient.h — Native client for the CrxShield kernel driver (\\.\CrxShield)
 *
 * USAGE
 * -----
 *   1. CrxConnect()              — open a handle to the driver device.
 *                                  Returns NULL on failure (driver not loaded,
 *                                  insufficient privilege, etc.). Call
 *                                  GetLastError() after a failed connect for
 *                                  details — note: with a NULL handle there is
 *                                  no per-handle error, so use GetLastError()
 *                                  (Win32) directly in that case.
 *   2. Issue requests:
 *        CrxGetVersion(h, &major, &minor, &build);
 *        CrxReadMemory(h, pid, addr, buffer, size);   // size <= 16 MB
 *        CrxWriteMemory(h, pid, addr, buffer, size);  // size <= 16 MB
 *        CrxGetProcessBase(h, pid, &base);
 *   3. CrxDisconnect(h)          — close the handle and free it. Always call,
 *                                  even on the failure path; CrxDisconnect(NULL)
 *                                  is a safe no-op.
 *
 * Every function returns BOOL (TRUE/FALSE). On FALSE, call
 * CrxGetLastError(h) to retrieve the Win32 error code captured at the point
 * of failure (stored per-handle; not thread-shared). Success resets the
 * stored error to ERROR_SUCCESS.
 *
 * Link: add CrxShieldClient.lib to your linker inputs and ship
 * CrxShieldClient.dll next to the executable. The DLL uses the static CRT and
 * has no runtime dependencies beyond kernel32.dll.
 */

#ifndef CRXSHIELD_CLIENT_H
#define CRXSHIELD_CLIENT_H

#include <Windows.h>

#ifdef __cplusplus
extern "C" {
#endif

/* dllexport when building the DLL, dllimport for consumers */
#ifdef CRXSHIELD_CLIENT_EXPORTS
#define CRX_API __declspec(dllexport)
#else
#define CRX_API __declspec(dllimport)
#endif

/* Opaque connection handle. Points to an internal struct holding the device
 * HANDLE and the per-handle last-error code. Treat as opaque. */
typedef struct CRX_CONTEXT_TAG* CRX_HANDLE;

/* Open \\.\CrxShield. Returns NULL on failure (see Win32 GetLastError). */
CRX_API CRX_HANDLE CrxConnect(void);

/* Close the device handle and free the context. NULL-safe no-op. */
CRX_API void CrxDisconnect(CRX_HANDLE h);

/* Query driver version (IOCTL_CRXSHIELD_GET_VERSION). */
CRX_API BOOL CrxGetVersion(CRX_HANDLE h, ULONG* major, ULONG* minor, ULONG* build);

/* Read up to 16 MB of target process memory into buffer. */
CRX_API BOOL CrxReadMemory(CRX_HANDLE h, ULONG pid, ULONG64 address, PVOID buffer, ULONG size);

/* Write up to 16 MB from buffer into target process memory. */
CRX_API BOOL CrxWriteMemory(CRX_HANDLE h, ULONG pid, ULONG64 address, const PVOID buffer, ULONG size);

/* Query the main module base address of the target process. */
CRX_API BOOL CrxGetProcessBase(CRX_HANDLE h, ULONG pid, ULONG64* baseAddress);

/* Per-handle last-error code (Win32 error). ERROR_SUCCESS after the last
 * successful call on this handle. Returns ERROR_INVALID_HANDLE for NULL. */
CRX_API DWORD CrxGetLastError(CRX_HANDLE h);

#ifdef __cplusplus
}
#endif

#endif /* CRXSHIELD_CLIENT_H */
