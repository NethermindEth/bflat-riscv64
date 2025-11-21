/**
 * @file
 * @brief Redhawk Platform (re)-implementation - for neglecting some functions
 *        that don't work well under zkVMs.
 *
 * Copyright (C) 2025 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
#include <inttypes.h>

extern void *RhpNewObject(void *methodTable, int allocFlags);
extern void *RhpGcAlloc(void *pEEType, unsigned int uFlags,
	unsigned long numElements, void * pTransitionFrame);
extern void **S_P_CoreLib_System_Runtime_TypeCast__CheckCastAny_NoCacheLookup(
	unsigned int *param_1, unsigned int **param_2);

void *
__wrap_RhpNewFast(void *methodTable)
{
	return RhpNewObject(methodTable, 0);
}

void *
__wrap_RhpNewPtrArrayFast(void *methodTable, unsigned long numElements)
{
	return RhpGcAlloc(methodTable, 0, numElements, 0);
}

void *
__wrap_RhpNewArrayFast(void *methodTable, unsigned long numElements)
{
	return RhpGcAlloc(methodTable, 0, numElements, 0);
}

void *
__wrap_RhNewString(void *methodTable, unsigned long numElements)
{
	return RhpGcAlloc(methodTable, 0, numElements, 0);
}

void **
__wrap_S_P_CoreLib_System_Runtime_TypeCast__CheckCastAny(
	unsigned int *param_1, unsigned int **param_2)
{
	return S_P_CoreLib_System_Runtime_TypeCast__CheckCastAny_NoCacheLookup(
		param_1, param_2);
}

void
__wrap_S_P_CoreLib_System_Diagnostics_Tracing_EventPipeEventProvider__Register()
{
}

void
__wrap_S_P_CoreLib_System_Diagnostics_Tracing_EventSource__InitializeDefaultEventSources()
{
}

/*
void
__wrap_S_P_CoreLib_System_Globalization_GlobalizationMode_Settings___cctor()
{
}
*/

int32_t
__wrap_GlobalizationNative_GetDefaultLocaleName(char *value, int valueLength)
{
	value[0] = 'e';
	value[1] = 'n';
	value[2] = '_';
	value[3] = 'U';
	value[4] = 'S';
	value[5] = '\0';
	return 1;
}

int
__wrap_S_P_CoreLib_System_Threading_ProcessorIdCache__ProcessorNumberSpeedCheck(void)
{
	return 1;
}