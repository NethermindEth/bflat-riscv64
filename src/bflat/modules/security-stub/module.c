void NetSecurityNative_ReleaseGssBuffer(void* buffer, int length)
{
}

int
NetSecurityNative_DisplayMinorStatus(int* minorStatus, int statusValue, void* outBuffer)
{
    return -1;
}

int
NetSecurityNative_DisplayMajorStatus(int* minorStatus, int statusValue, void* outBuffer)
{
    return -1;
}

int
NetSecurityNative_ImportUserName(int* minorStatus, char* inputName, int inputNameLen, void** outputName)
{
    return -1;
}

int
NetSecurityNative_ImportPrincipalName(int* minorStatus,
                                      char* inputName,
                                      int inputNameLen,
                                      void** outputName)
{
    return -1;
}

int
NetSecurityNative_ReleaseName(int* minorStatus, void** inputName)
{
    return -1;
}

int
NetSecurityNative_AcquireAcceptorCred(int* minorStatus, void** outputCredHandle)
{
    return -1;
}

int
NetSecurityNative_InitiateCredSpNego(int* minorStatus, void* desiredName, void** outputCredHandle)
{
    return -1;
}

int
NetSecurityNative_ReleaseCred(int* minorStatus, void** credHandle)
{
    return -1;
}

int
NetSecurityNative_InitSecContext(int* minorStatus,
                                 void* claimantCredHandle,
                                 void** contextHandle,
                                 int packageType,
                                 void* targetName,
                                 int reqFlags,
                                 int* inputBytes,
                                 int inputLength,
                                 void* outBuffer,
                                 int* retFlags,
                                 int* isNtlmUsed)
{
    return -1;
}

int
NetSecurityNative_InitSecContextEx(int* minorStatus,
                                   void* claimantCredHandle,
                                   void** contextHandle,
                                   int packageType,
                                   void* cbt,
                                   int cbtSize,
                                   void* targetName,
                                   int reqFlags,
                                   int* inputBytes,
                                   int inputLength,
                                   void* outBuffer,
                                   int* retFlags,
                                   int* isNtlmUsed)
{
    return -1;
}

int
NetSecurityNative_AcceptSecContext(int* minorStatus,
                                   void* acceptorCredHandle,
                                   void** contextHandle,
                                   int* inputBytes,
                                   int inputLength,
                                   void* outBuffer,
                                   int* retFlags,
                                   int* isNtlmUsed)
{
    return -1;
}

int
NetSecurityNative_DeleteSecContext(int* minorStatus, void** contextHandle)
{
    return -1;
}

int
NetSecurityNative_Wrap(int* minorStatus,
                       void* contextHandle,
                       void* isEncrypt,
                       int* inputBytes,
                       int count,
                       void* outBuffer)
{
    return -1;
}

int
NetSecurityNative_Unwrap(int* minorStatus,
                         void* contextHandle,
                         void* isEncrypt,
                         int* inputBytes,
                         int count,
                         void* outBuffer)
{
    return -1;
}

int
NetSecurityNative_GetMic(int* minorStatus,
                         void* contextHandle,
                         int* inputBytes,
                         int inputLength,
                         void* outBuffer)
{
    return -1;
}

int
NetSecurityNative_VerifyMic(int* minorStatus,
                            void* contextHandle,
                            int* inputBytes,
                            int inputLength,
                            int* tokenBytes,
                            int tokenLength)
{
    return -1;
}

int
NetSecurityNative_InitiateCredWithPassword(int* minorStatus,
                                           int packageType,
                                           void* desiredName,
                                           char* password,
                                           int passwdLen,
                                           void** outputCredHandle)
{
    return -1;
}

int
NetSecurityNative_IsNtlmInstalled(void)
{
    return -1;
}

int
NetSecurityNative_GetUser(int* minorStatus,
                          void* contextHandle,
                          void* outBuffer)
{
    return -1;
}

int
NetSecurityNative_EnsureGssInitialized(void)
{
    return -1;
}
