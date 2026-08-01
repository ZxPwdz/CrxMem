#ifndef MODULE_H
#define MODULE_H

#include <Windows.h>

class MODULE
{
	bool Initialized;

	IMAGE_DOS_HEADER *pDosHeader;
	IMAGE_NT_HEADERS *pNtHeaders;

	DWORD ModuleBase;
	DWORD ModuleSize;
	DWORD CodeBase;
	DWORD CodeSize;

public:
	MODULE();
	bool IsInitialized();
	bool InitializeModule(const char* ModuleName);
	PIMAGE_OPTIONAL_HEADER GetOptionalHeader();
	DWORD GetModuleBase();
	DWORD GetModuleSize();
	DWORD GetCodeBase();
	DWORD GetCodeSize();
};

extern MODULE gunz;

#endif
