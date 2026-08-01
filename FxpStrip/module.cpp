#include "module.h"
#include <stdio.h>

MODULE::MODULE()
{
	Initialized = FALSE;
	pDosHeader = NULL;
	pNtHeaders = NULL;

	ModuleBase = 0;
	ModuleSize = 0;
	CodeBase = 0;
	CodeSize = 0;
}

bool MODULE::IsInitialized()
{
	return Initialized;
}

bool MODULE::InitializeModule(const char *ModuleName)
{
	Initialized = FALSE;

	if (!ModuleName)
		return FALSE;

	pDosHeader = (PIMAGE_DOS_HEADER)GetModuleHandle(ModuleName);

	if (!pDosHeader || pDosHeader->e_magic != IMAGE_DOS_SIGNATURE || !pDosHeader->e_lfanew)
		return FALSE;

	pNtHeaders = (PIMAGE_NT_HEADERS)((LPBYTE)pDosHeader + pDosHeader->e_lfanew);

	if (!pNtHeaders || pNtHeaders->Signature != IMAGE_NT_SIGNATURE || pNtHeaders->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR_MAGIC)
		return FALSE;

	ModuleBase = (DWORD)pDosHeader;
	ModuleSize = pNtHeaders->OptionalHeader.SizeOfImage;
	CodeBase = ModuleBase;
	CodeSize = ModuleSize;

	// Parse section headers for accurate .text boundaries (OptionalHeader
	// values are spoofed by packers like Themida).
	PIMAGE_SECTION_HEADER pSection = IMAGE_FIRST_SECTION(pNtHeaders);
	for (WORD i = 0; i < pNtHeaders->FileHeader.NumberOfSections; i++, pSection++)
	{
		if (pSection->Characteristics & IMAGE_SCN_CNT_CODE)
		{
			CodeBase = ModuleBase + pSection->VirtualAddress;
			CodeSize = pSection->Misc.VirtualSize;
			break;
		}
	}

	Initialized = TRUE;
	return TRUE;
}

PIMAGE_OPTIONAL_HEADER MODULE::GetOptionalHeader()
{
	return &pNtHeaders->OptionalHeader;
}

DWORD MODULE::GetModuleBase()
{
	return ModuleBase;
}

DWORD MODULE::GetModuleSize()
{
	return ModuleSize;
}

DWORD MODULE::GetCodeBase()
{
	return CodeBase;
}

DWORD MODULE::GetCodeSize()
{
	return CodeSize;
}