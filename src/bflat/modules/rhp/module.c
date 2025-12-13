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
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>

extern void *RhpNewObject(void *methodTable, int allocFlags);
extern void *RhpGcAlloc(void *pEEType, unsigned int uFlags,
	unsigned long numElements, void * pTransitionFrame);
extern void **S_P_CoreLib_System_Runtime_TypeCast__CheckCastAny_NoCacheLookup(
	unsigned int *param_1, unsigned int **param_2);

/*
 * The actual cell is in t5, therefore we must extract it from there
 */
static inline void *
get_dispatch_cell_from_t5(void)
{
	void *cell;
	__asm__ volatile("mv %0, t5" : "=r"(cell));
	return cell;
}

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

/*
 * Thread Static Storage for zkVM.
 *
 * On zkVM there's no real threading, so we provide a simple static storage
 * area for thread statics. The NativeAOT runtime uses thread statics for
 * various internal purposes including locks and class constructor tracking.
 *
 * GetUninlinedThreadStaticBaseForType is called to get the base address
 * for a type's thread static fields. We provide a simple static buffer.
 */
#define THREAD_STATIC_STORAGE_SIZE (256 * 1024)
static uint8_t thread_static_storage[THREAD_STATIC_STORAGE_SIZE]
	__attribute__((aligned(64))) = { 0 };
static uint8_t *thread_static_ptr = thread_static_storage;

/*
 * Wrapper for RhGetThreadStaticStorage - returns our static storage
 */
void *
__wrap_RhGetThreadStaticStorage(void)
{
	return thread_static_storage;
}

/*
 * ---------------------------------------------------------------------------
 * Non-cached interface dispatch resolver for RhpCidResolve
 * ---------------------------------------------------------------------------
 *
 * This is called by the assembly trampoline `__wrap_RhpCidResolve` (provided
 * by the rhp_native module). It attempts to resolve the interface target using
 * dispatch-cell metadata and the type's interface dispatch map, without
 * touching the cached dispatch-cell fast path.
 */

typedef struct MethodTable MethodTable;

extern void RhpGetDispatchCellInfo(void *pCell, void *pDispatchCellInfoOut);
extern void *RhResolveDispatchOnType(MethodTable *pInstanceType, MethodTable *pInterfaceType, uint16_t slot);

/* Object header layout: first pointer-sized field is MethodTable* */
static inline MethodTable *
get_method_table(void *obj)
{
	return *(MethodTable **)obj;
}

typedef enum DispatchCellType : uint32_t
{
	DispatchCellType_InterfaceAndSlot = 0,
	DispatchCellType_VTableOffset     = 1,
	DispatchCellType_MetadataToken    = 2,
} DispatchCellType;

typedef struct DispatchCellInfoLite
{
	uint32_t CellType;
	uint32_t _pad0;
	void    *InterfaceType;
	uint16_t InterfaceSlot;
	uint8_t  HasCache;
	uint8_t  _pad1;
	uint32_t MetadataToken;
	uint32_t VTableOffset;
	uint32_t _pad2;
} DispatchCellInfoLite;

static void
cid_fail_fast(const char *reason,
              void *callerTransitionBlockParam,
              void *pCell,
              void *obj,
              MethodTable *mt,
              const DispatchCellInfoLite *info,
              void *target)
{
	fprintf(stderr,
	        "[CID] FAIL: %s\n"
	        "  tb=%p pCell=%p obj=%p mt=%p target=%p\n",
	        reason,
	        callerTransitionBlockParam,
	        pCell,
	        obj,
	        (void *)mt,
	        target);

	if (info != NULL)
	{
		fprintf(stderr,
		        "  cellInfo: CellType=%u InterfaceType=%p InterfaceSlot=%u VTableOffset=%u\n",
		        (unsigned)info->CellType,
		        (void *)info->InterfaceType,
		        (unsigned)info->InterfaceSlot,
		        (unsigned)info->VTableOffset);
	}

	/* Prevent silent jump-to-null at the call site */
	abort();
}

void *
__rhp_cid_resolve_nocache(void *callerTransitionBlockParam, void *pCell)
{
	uint8_t *tb = (uint8_t *)callerTransitionBlockParam;
	void *obj = *(void **)(tb + (2 * sizeof(void *)));
	MethodTable *mt = get_method_table(obj);

	pCell = get_dispatch_cell_from_t5();

	if (obj == NULL || mt == NULL)
	{
		cid_fail_fast("could not locate 'this' in transition block",
		              callerTransitionBlockParam, pCell, obj, mt, NULL, NULL);
	}

	DispatchCellInfoLite info;
	for (size_t i = 0; i < sizeof(info); i++)
		((volatile uint8_t *)&info)[i] = 0;

	RhpGetDispatchCellInfo(pCell, &info);

	if (info.CellType == DispatchCellType_InterfaceAndSlot)
	{
		void *target = RhResolveDispatchOnType(mt, (MethodTable *)info.InterfaceType, info.InterfaceSlot);
		if (target == NULL)
			cid_fail_fast("RhResolveDispatchOnType returned NULL",
			              callerTransitionBlockParam, pCell, obj, mt, &info, target);
		return target;
	}

	if (info.CellType == DispatchCellType_VTableOffset)
	{
		/* VTableOffset dispatch: *(mt + offset) */
		void *target = *(void **)(((uint8_t *)mt) + info.VTableOffset);
		if (target == NULL)
			cid_fail_fast("VTableOffset target is NULL",
			              callerTransitionBlockParam, pCell, obj, mt, &info, target);
		return target;
	}

	/* Unsupported dispatch-cell type on this minimal path */
	cid_fail_fast("unsupported dispatch cell type",
	              callerTransitionBlockParam, pCell, obj, mt, &info, NULL);
	return (void *)0;
}
