// hook_native.cpp
// Small C++ DLL that uses MinHook to hook QMqttClient::messageReceived
// in wbmqtt.dll and forwards intercepted messages via a callback to the
// NativeAOT C# hook DLL (TradingPilot.Webull.Hook).
//
// MinHook handles CFG, security cookies, and instruction relocation properly,
// which our custom X64Hook in C# cannot do for messageReceived.

#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <stdio.h>
#include "MinHook.h"

#pragma comment(lib, "ws2_32.lib")

// ═══════════════════════════════════════════════════════════════════
// TCP socket server — sends MQTT messages to Blazor app on localhost
// Protocol: [1B type=0x00][4B topicLen][topic][4B payloadLen][payload]
// Port 19880 on localhost (chosen to avoid conflicts)
// ═══════════════════════════════════════════════════════════════════

#define TCP_PORT 19880

static SOCKET g_clientSocket = INVALID_SOCKET;
static CRITICAL_SECTION g_tcpLock;
static volatile bool g_tcpInited = false;
static volatile bool g_tcpConnected = false;

static void InitTcp() {
    if (!g_tcpInited) {
        WSADATA wsaData;
        WSAStartup(MAKEWORD(2, 2), &wsaData);
        InitializeCriticalSection(&g_tcpLock);
        g_tcpInited = true;
    }
}

static bool SendAll(SOCKET s, const char* buf, int len) {
    int sent = 0;
    while (sent < len) {
        int r = send(s, buf + sent, len - sent, 0);
        if (r <= 0) return false;
        sent += r;
    }
    return true;
}

// TCP server thread — listens on localhost:19880, accepts one client
static DWORD WINAPI TcpServerThread(LPVOID param) {
    SOCKET listenSock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (listenSock == INVALID_SOCKET) return 1;

    // Allow quick rebind after restart
    int reuse = 1;
    setsockopt(listenSock, SOL_SOCKET, SO_REUSEADDR, (const char*)&reuse, sizeof(reuse));

    struct sockaddr_in addr = {};
    addr.sin_family = AF_INET;
    addr.sin_port = htons(TCP_PORT);
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK); // 127.0.0.1 only

    if (bind(listenSock, (struct sockaddr*)&addr, sizeof(addr)) == SOCKET_ERROR) {
        closesocket(listenSock);
        return 2;
    }
    listen(listenSock, 1);

    while (true) {
        SOCKET client = accept(listenSock, NULL, NULL);
        if (client == INVALID_SOCKET) {
            Sleep(1000);
            continue;
        }

        // Disable Nagle for low latency
        int nodelay = 1;
        setsockopt(client, IPPROTO_TCP, TCP_NODELAY, (const char*)&nodelay, sizeof(nodelay));

        EnterCriticalSection(&g_tcpLock);
        if (g_clientSocket != INVALID_SOCKET) closesocket(g_clientSocket);
        g_clientSocket = client;
        g_tcpConnected = true;
        LeaveCriticalSection(&g_tcpLock);

        // Wait until disconnected
        while (g_tcpConnected) {
            Sleep(100);
        }
    }
    return 0;
}

static void SendToTcp(const char* topic, int topicLen, const char* payload, int payloadLen) {
    if (!g_tcpInited || !g_tcpConnected) return;
    EnterCriticalSection(&g_tcpLock);
    __try {
        if (g_clientSocket == INVALID_SOCKET || !g_tcpConnected) __leave;
        char msgType = 0x00;
        if (!SendAll(g_clientSocket, &msgType, 1)) goto tcp_err;
        if (!SendAll(g_clientSocket, (const char*)&topicLen, 4)) goto tcp_err;
        if (topicLen > 0 && !SendAll(g_clientSocket, topic, topicLen)) goto tcp_err;
        if (!SendAll(g_clientSocket, (const char*)&payloadLen, 4)) goto tcp_err;
        if (payloadLen > 0 && !SendAll(g_clientSocket, payload, payloadLen)) goto tcp_err;
        __leave;
    tcp_err:
        g_tcpConnected = false;
        closesocket(g_clientSocket);
        g_clientSocket = INVALID_SOCKET;
    }
    __finally {
        LeaveCriticalSection(&g_tcpLock);
    }
}

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

static void __fastcall Detour_messageReceived(void* thisPtr, void* qByteArrayRef, void* qMqttTopicNameRef) {
    InterlockedIncrement(&g_messageCount);

    __try {
        // Skip topic reading — QMqttTopicName always returns empty/garbage.
        // Only read the QByteArray payload (offset=24 is standard QArrayData).
        int payloadLen = 0;
        const char* payload = ReadQByteArrayData(qByteArrayRef, &payloadLen);

        // Send directly via TCP socket to Blazor app
        SendToTcp("", 0, payload ? payload : "", payloadLen);
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
    InitTcp();
    // Start TCP server thread for sending data to Blazor app
    CreateThread(NULL, 0, TcpServerThread, NULL, 0, NULL);

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
