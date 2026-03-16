// hook_native.cpp
// Small C++ DLL that uses MinHook to hook QMqttClient::messageReceived
// in wbmqtt.dll and forwards intercepted messages via a callback to the
// NativeAOT C# hook DLL (TradingPilot.Webull.Hook).
//
// MinHook handles CFG, security cookies, and instruction relocation properly,
// which our custom X64Hook in C# cannot do for messageReceived.

#include <windows.h>
#include <stdio.h>
#include "MinHook.h"

// ═══════════════════════════════════════════════════════════════════
// Callback type: called by the detour to forward message data to C#
// ═══════════════════════════════════════════════════════════════════

// Parameters: topicUtf8, topicLen, payload, payloadLen
typedef void(__cdecl* MessageCallback)(const char* topicUtf8, int topicLen, const char* payload, int payloadLen);

static MessageCallback g_callback = nullptr;

// ═══════════════════════════════════════════════════════════════════
// Function signature for the target
// ═══════════════════════════════════════════════════════════════════

// void QMqttClient::messageReceived(const QByteArray& payload, const QMqttTopicName& topic)
// MSVC x64 ABI: rcx=this, rdx=QByteArray*, r8=QMqttTopicName*
typedef void(__fastcall* fn_messageReceived)(void* thisPtr, void* qByteArrayRef, void* qMqttTopicNameRef);

static fn_messageReceived g_origMessageReceived = nullptr;
static volatile LONG g_messageCount = 0;

// ═══════════════════════════════════════════════════════════════════
// Qt5 QArrayData layout (x64)
// ═══════════════════════════════════════════════════════════════════
//   int ref;           // offset 0,  4 bytes (atomic ref count)
//   int size;          // offset 4,  4 bytes
//   uint alloc:31;     // offset 8,  4 bytes (packed with capacityReserved)
//   uint caps:1;
//   <padding>          // offset 12, 4 bytes
//   qptrdiff offset;   // offset 16, 8 bytes (offset from QArrayData* to data)
// Data is at: (byte*)d + d->offset

struct QArrayData {
    int ref;
    int size;
    unsigned int alloc;
    int _pad;
    long long offset;
};

// ═══════════════════════════════════════════════════════════════════
// Qt type readers
// ═══════════════════════════════════════════════════════════════════

// Read QByteArray data. QByteArray is { QTypedArrayData<char>* d; }
static const char* ReadQByteArrayData(void* qbaPtr, int* outSize) {
    if (!qbaPtr) { *outSize = 0; return nullptr; }
    QArrayData** pp = (QArrayData**)qbaPtr;
    QArrayData* d = *pp;
    if (!d) { *outSize = 0; return nullptr; }
    *outSize = d->size;
    if (d->size <= 0 || d->size > 10 * 1024 * 1024) { *outSize = 0; return nullptr; }
    return (const char*)((char*)d + d->offset);
}

// Read QString/QMqttTopicName data (UTF-16). QString is { QTypedArrayData<QChar>* d; }
static const wchar_t* ReadQStringData(void* qstrPtr, int* outLen) {
    if (!qstrPtr) { *outLen = 0; return nullptr; }
    QArrayData** pp = (QArrayData**)qstrPtr;
    QArrayData* d = *pp;
    if (!d) { *outLen = 0; return nullptr; }
    *outLen = d->size;
    if (d->size <= 0 || d->size > 1024 * 1024) { *outLen = 0; return nullptr; }
    return (const wchar_t*)((char*)d + d->offset);
}

// ═══════════════════════════════════════════════════════════════════
// Detour function
// ═══════════════════════════════════════════════════════════════════

// Simple log to file for debugging
static void DebugLog(const char* fmt, ...) {
    static CRITICAL_SECTION logLock;
    static bool logInit = false;
    if (!logInit) { InitializeCriticalSection(&logLock); logInit = true; }
    EnterCriticalSection(&logLock);
    FILE* f = fopen("D:\\Third-Parties\\WebullHook\\hook_native.log", "a");
    if (f) {
        va_list args;
        va_start(args, fmt);
        vfprintf(f, fmt, args);
        va_end(args);
        fprintf(f, "\n");
        fclose(f);
    }
    LeaveCriticalSection(&logLock);
}

static void __fastcall Detour_messageReceived(void* thisPtr, void* qByteArrayRef, void* qMqttTopicNameRef) {
    LONG count = InterlockedIncrement(&g_messageCount);

    __try {
        // Debug: dump first few messages' raw QArrayData
        if (count <= 3) {
            DebugLog("MSG #%d: this=%p ba=%p topic=%p", count, thisPtr, qByteArrayRef, qMqttTopicNameRef);
            if (qMqttTopicNameRef) {
                QArrayData* td = *(QArrayData**)qMqttTopicNameRef;
                if (td) {
                    DebugLog("  topic QArrayData: ref=%d size=%d alloc=%u offset=%lld addr=%p",
                        td->ref, td->size, td->alloc, td->offset, td);
                    const char* rawData = (const char*)td + td->offset;
                    // Dump first 40 bytes as hex
                    char hex[128] = {0};
                    int dumpLen = td->size * 2 < 40 ? td->size * 2 : 40;  // UTF-16 = 2 bytes per char
                    for (int i = 0; i < dumpLen && i < 40; i++)
                        sprintf(hex + i*2, "%02X", (unsigned char)rawData[i]);
                    DebugLog("  topic raw data: %s", hex);
                }
            }
            if (qByteArrayRef) {
                QArrayData* bd = *(QArrayData**)qByteArrayRef;
                if (bd) {
                    DebugLog("  payload QArrayData: ref=%d size=%d alloc=%u offset=%lld addr=%p",
                        bd->ref, bd->size, bd->alloc, bd->offset, bd);
                    const char* rawData = (const char*)bd + bd->offset;
                    // Dump first 80 bytes as text
                    char preview[81] = {0};
                    int previewLen = bd->size < 80 ? bd->size : 80;
                    memcpy(preview, rawData, previewLen);
                    // Replace non-printable chars
                    for (int i = 0; i < previewLen; i++)
                        if (preview[i] < 32 || preview[i] > 126) preview[i] = '.';
                    DebugLog("  payload preview: %s", preview);
                }
            }
        }

        // Read topic (QString/QMqttTopicName -> UTF-16)
        int topicWLen = 0;
        const wchar_t* topicW = ReadQStringData(qMqttTopicNameRef, &topicWLen);

        // Convert UTF-16 topic to UTF-8
        char topicUtf8[4096];
        int topicUtf8Len = 0;
        if (topicW && topicWLen > 0) {
            topicUtf8Len = WideCharToMultiByte(CP_UTF8, 0, topicW, topicWLen,
                topicUtf8, sizeof(topicUtf8) - 1, NULL, NULL);
            if (topicUtf8Len < 0) topicUtf8Len = 0;
        }

        // Read payload (QByteArray)
        int payloadLen = 0;
        const char* payload = ReadQByteArrayData(qByteArrayRef, &payloadLen);

        // Forward to C# callback
        if (g_callback) {
            g_callback(topicUtf8, topicUtf8Len, payload ? payload : "", payloadLen);
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        // Silently ignore read errors to avoid crashing the host process
    }

    // Call original function
    if (g_origMessageReceived) {
        g_origMessageReceived(thisPtr, qByteArrayRef, qMqttTopicNameRef);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Exported API (called by the NativeAOT C# hook DLL)
// ═══════════════════════════════════════════════════════════════════

extern "C" {

/// Set the callback function that receives intercepted messages.
/// Must be called before InstallMessageReceivedHook.
__declspec(dllexport) void SetMessageCallback(MessageCallback cb) {
    g_callback = cb;
}

/// Install a MinHook-based hook on QMqttClient::messageReceived.
/// targetAddr: the resolved address of the messageReceived export in wbmqtt.dll.
/// Returns 0 on success, non-zero error code on failure.
///   1xx = MH_CreateHook error (100 + MH_STATUS)
///   2xx = MH_EnableHook error (200 + MH_STATUS)
///   Other = MH_Initialize error
__declspec(dllexport) int InstallMessageReceivedHook(void* targetAddr) {
    MH_STATUS status = MH_Initialize();
    if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED) {
        return (int)status;
    }

    status = MH_CreateHook(targetAddr, (LPVOID)&Detour_messageReceived, (LPVOID*)&g_origMessageReceived);
    if (status != MH_OK) {
        return 100 + (int)status;
    }

    status = MH_EnableHook(targetAddr);
    if (status != MH_OK) {
        return 200 + (int)status;
    }

    return 0; // success
}

/// Remove the hook and uninitialize MinHook.
__declspec(dllexport) void RemoveMessageReceivedHook(void* targetAddr) {
    if (targetAddr) {
        MH_DisableHook(targetAddr);
        MH_RemoveHook(targetAddr);
    }
    MH_Uninitialize();
}

/// Returns the number of messages intercepted by the native hook.
__declspec(dllexport) int GetNativeMessageCount() {
    return (int)g_messageCount;
}

} // extern "C"

// ═══════════════════════════════════════════════════════════════════
// DLL entry point (minimal, no-op)
// ═══════════════════════════════════════════════════════════════════

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved) {
    (void)hModule;
    (void)reason;
    (void)reserved;
    return TRUE;
}
