/**
 * @file
 * @brief Unit tests for security-stub/module.c: all 20 GSSAPI stubs fail
 *        with -1 and leave every out-parameter untouched;
 *        ReleaseGssBuffer is a safe no-op.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include "common.h"

extern void NetSecurityNative_ReleaseGssBuffer(void *buffer, int length);
extern int NetSecurityNative_DisplayMinorStatus(int *minorStatus,
                                                int statusValue,
                                                void *outBuffer);
extern int NetSecurityNative_DisplayMajorStatus(int *minorStatus,
                                                int statusValue,
                                                void *outBuffer);
extern int NetSecurityNative_ImportUserName(int *minorStatus, char *inputName,
                                            int inputNameLen,
                                            void **outputName);
extern int NetSecurityNative_ImportPrincipalName(int *minorStatus,
                                                 char *inputName,
                                                 int inputNameLen,
                                                 void **outputName);
extern int NetSecurityNative_ReleaseName(int *minorStatus, void **inputName);
extern int NetSecurityNative_AcquireAcceptorCred(int *minorStatus,
                                                 void **outputCredHandle);
extern int NetSecurityNative_InitiateCredSpNego(int *minorStatus,
                                                void *desiredName,
                                                void **outputCredHandle);
extern int NetSecurityNative_ReleaseCred(int *minorStatus, void **credHandle);
extern int NetSecurityNative_InitSecContext(int *minorStatus,
                                            void *claimantCredHandle,
                                            void **contextHandle,
                                            int packageType, void *targetName,
                                            int reqFlags, int *inputBytes,
                                            int inputLength, void *outBuffer,
                                            int *retFlags, int *isNtlmUsed);
extern int NetSecurityNative_InitSecContextEx(
    int *minorStatus, void *claimantCredHandle, void **contextHandle,
    int packageType, void *cbt, int cbtSize, void *targetName, int reqFlags,
    int *inputBytes, int inputLength, void *outBuffer, int *retFlags,
    int *isNtlmUsed);
extern int NetSecurityNative_AcceptSecContext(int *minorStatus,
                                              void *acceptorCredHandle,
                                              void **contextHandle,
                                              int *inputBytes, int inputLength,
                                              void *outBuffer, int *retFlags,
                                              int *isNtlmUsed);
extern int NetSecurityNative_DeleteSecContext(int *minorStatus,
                                              void **contextHandle);
extern int NetSecurityNative_Wrap(int *minorStatus, void *contextHandle,
                                  void *isEncrypt, int *inputBytes, int count,
                                  void *outBuffer);
extern int NetSecurityNative_Unwrap(int *minorStatus, void *contextHandle,
                                    void *isEncrypt, int *inputBytes,
                                    int count, void *outBuffer);
extern int NetSecurityNative_GetMic(int *minorStatus, void *contextHandle,
                                    int *inputBytes, int inputLength,
                                    void *outBuffer);
extern int NetSecurityNative_VerifyMic(int *minorStatus, void *contextHandle,
                                       int *inputBytes, int inputLength,
                                       int *tokenBytes, int tokenLength);
extern int NetSecurityNative_InitiateCredWithPassword(int *minorStatus,
                                                      int packageType,
                                                      void *desiredName,
                                                      char *password,
                                                      int passwdLen,
                                                      void **outputCredHandle);
extern int NetSecurityNative_IsNtlmInstalled(void);
extern int NetSecurityNative_GetUser(int *minorStatus, void *contextHandle,
                                     void *outBuffer);
extern int NetSecurityNative_EnsureGssInitialized(void);

int main(void)
{
    int minor = 12345;         /* must stay untouched */
    void *handle = (void *)0x1; /* must stay untouched */
    int flags = 777;

    NetSecurityNative_ReleaseGssBuffer(NULL, 0);
    NetSecurityNative_ReleaseGssBuffer(&minor, 4);
    t_pass++; /* reached without crashing */

    CHECK(NetSecurityNative_DisplayMinorStatus(&minor, 1, NULL) == -1);
    CHECK(NetSecurityNative_DisplayMajorStatus(&minor, 1, NULL) == -1);
    CHECK(NetSecurityNative_ImportUserName(&minor, "user", 4, &handle) == -1);
    CHECK(NetSecurityNative_ImportPrincipalName(&minor, "p", 1, &handle)
          == -1);
    CHECK(NetSecurityNative_ReleaseName(&minor, &handle) == -1);
    CHECK(NetSecurityNative_AcquireAcceptorCred(&minor, &handle) == -1);
    CHECK(NetSecurityNative_InitiateCredSpNego(&minor, NULL, &handle) == -1);
    CHECK(NetSecurityNative_ReleaseCred(&minor, &handle) == -1);
    CHECK(NetSecurityNative_InitSecContext(&minor, NULL, &handle, 0, NULL, 0,
                                           NULL, 0, NULL, &flags, &flags)
          == -1);
    CHECK(NetSecurityNative_InitSecContextEx(&minor, NULL, &handle, 0, NULL,
                                             0, NULL, 0, NULL, 0, NULL,
                                             &flags, &flags)
          == -1);
    CHECK(NetSecurityNative_AcceptSecContext(&minor, NULL, &handle, NULL, 0,
                                             NULL, &flags, &flags)
          == -1);
    CHECK(NetSecurityNative_DeleteSecContext(&minor, &handle) == -1);
    CHECK(NetSecurityNative_Wrap(&minor, NULL, NULL, NULL, 0, NULL) == -1);
    CHECK(NetSecurityNative_Unwrap(&minor, NULL, NULL, NULL, 0, NULL) == -1);
    CHECK(NetSecurityNative_GetMic(&minor, NULL, NULL, 0, NULL) == -1);
    CHECK(NetSecurityNative_VerifyMic(&minor, NULL, NULL, 0, NULL, 0) == -1);
    CHECK(NetSecurityNative_InitiateCredWithPassword(&minor, 0, NULL, "pw", 2,
                                                     &handle)
          == -1);
    CHECK(NetSecurityNative_IsNtlmInstalled() == -1);
    CHECK(NetSecurityNative_GetUser(&minor, NULL, NULL) == -1);
    CHECK(NetSecurityNative_EnsureGssInitialized() == -1);

    /* No stub may write through its out-parameters. */
    CHECK(minor == 12345);
    CHECK(handle == (void *)0x1);
    CHECK(flags == 777);

    TEST_MAIN_END("security-stub");
}
