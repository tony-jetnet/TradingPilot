using System.Runtime.InteropServices;

namespace TradingPilot.Webull.Hook;

/// <summary>
/// Entry point for the injected DLL. Hooks multiple QMqttClient functions in wbmqtt.dll
/// to intercept MQTT messages, subscriptions, and connection parameters.
/// </summary>
public static unsafe class HookEntry
{
    // ═══════════════════════════════════════════════════════════════════
    // Mangled export names from wbgrpc.dll
    // ═══════════════════════════════════════════════════════════════════

    // QString WBQuoteGrpcAsyncClientCall::getHeadMd5Sign(const QMap<QString,QString>&)
    // MSVC x64: rcx=this, rdx=hidden return (QString*), r8=const QMap*
    internal const string Fn_getHeadMd5Sign_Mangled =
        "?getHeadMd5Sign@WBQuoteGrpcAsyncClientCall@@AEAA?AVQString@@AEBV?$QMap@VQString@@V1@@@@Z";
    private const string Fn_getHeadMd5Sign = Fn_getHeadMd5Sign_Mangled;

    // void WBQuoteGrpcAsyncClient::request(ClientRequest)
    // MSVC x64: rcx=this, rdx=ClientRequest (by value → hidden pointer)
    private const string Fn_grpcRequest =
        "?request@WBQuoteGrpcAsyncClient@@QEAAXVClientRequest@v1@gateway@@@Z";

    // int WBQuoteGrpcAsyncClientCall::write(const ClientRequest&)
    // MSVC x64: rcx=this, rdx=const ClientRequest*
    private const string Fn_grpcWrite =
        "?write@WBQuoteGrpcAsyncClientCall@@AEAAHAEBVClientRequest@v1@gateway@@@Z";

    // ═══════════════════════════════════════════════════════════════════
    // Mangled export names from wbmqtt.dll (dumpbin /exports)
    // ═══════════════════════════════════════════════════════════════════

    // void QMqttClient::messageReceived(const QByteArray&, const QMqttTopicName&) [signal, ordinal 223]
    private const string Fn_messageReceived =
        "?messageReceived@QMqttClient@@QEAAXAEBVQByteArray@@AEBVQMqttTopicName@@@Z";

    // void QMqttClient::messageReceived(QMqttMessage) on QMqttSubscription [ordinal 224]
    private const string Fn_subscriptionMessageReceived =
        "?messageReceived@QMqttSubscription@@QEAAXVQMqttMessage@@@Z";

    // QMqttSubscription* QMqttClient::subscribe(const QMqttTopicFilter&, quint8 qos) [ordinal 377]
    private const string Fn_subscribe =
        "?subscribe@QMqttClient@@QEAAPEAVQMqttSubscription@@AEBVQMqttTopicFilter@@E@Z";

    // void QMqttClient::connectToHost() [ordinal 138]
    private const string Fn_connectToHost =
        "?connectToHost@QMqttClient@@QEAAXXZ";

    // void QMqttClient::connected() [signal, ordinal 140]
    private const string Fn_connected =
        "?connected@QMqttClient@@QEAAXXZ";

    // ReasonCode QMqttClient::invokeSubscribeResult(const QString&) [ordinal 197]
    // This is the actual implementation - invokeSubscribe (ordinal 196) is a jmp to this
    private const string Fn_invokeSubscribeResult =
        "?invokeSubscribeResult@QMqttClient@@QEAA?AW4ReasonCode@QMqtt@@AEBVQString@@@Z";

    // int QMqttClient::publish(const QMqttTopicName&, const QByteArray&, quint8, bool) [ordinal 247]
    private const string Fn_publish =
        "?publish@QMqttClient@@QEAAHAEBVQMqttTopicName@@AEBVQByteArray@@E_N@Z";

    // void QMqttClient::disconnectFromHost() [ordinal 166]
    private const string Fn_disconnectFromHost =
        "?disconnectFromHost@QMqttClient@@QEAAXXZ";

    // QString QMqttClient::hostname() const [ordinal 186]
    private const string Fn_hostname =
        "?hostname@QMqttClient@@QEBA?AVQString@@XZ";

    // quint16 QMqttClient::port() const [ordinal 241]
    private const string Fn_port =
        "?port@QMqttClient@@QEBAGXZ";

    // QString QMqttClient::username() const [ordinal 411]
    private const string Fn_username =
        "?username@QMqttClient@@QEBA?AVQString@@XZ";

    // QString QMqttClient::clientId() const [ordinal 134]
    private const string Fn_clientId =
        "?clientId@QMqttClient@@QEBA?AVQString@@XZ";

    // ═══════════════════════════════════════════════════════════════════
    // Trampolines for calling original functions
    // ═══════════════════════════════════════════════════════════════════
    private static nint _orig_messageReceived; // set by native DLL hook (unused here)
    private static nint _nativeHookDll; // handle to hook_native.dll
    private static nint _nativeMessageReceivedTarget; // target addr for cleanup
    private static nint _orig_subscribe;
    private static nint _orig_connectToHost;
    private static nint _orig_connected;
    private static nint _orig_subMessageReceived;

    private static nint _orig_invokeSubscribeResult;
    private static nint _orig_publish;
    private static nint _orig_disconnectFromHost;

    // gRPC hook trampolines
    private static nint _orig_getHeadMd5Sign;
    private static nint _orig_grpcRequest;
    private static nint _orig_grpcWrite;

    // Resolved getter function pointers (not hooked, just called)
    private static nint _fn_hostname;
    private static nint _fn_port;
    private static nint _fn_username;
    private static nint _fn_clientId;

    // Captured QMqttClient pointer (set when any hook fires with a client)
    private static nint _mqttClient;

    // Stats
    private static volatile int _messageCount;
    private static volatile int _subscribeCount;
    private static volatile int _grpcRequestCount;

    // ═══════════════════════════════════════════════════════════════════
    // Internal accessors for CommandReceiver
    // ═══════════════════════════════════════════════════════════════════
    internal static nint MqttClient => _mqttClient;
    internal static nint OrigInvokeSubscribeResult => _orig_invokeSubscribeResult;
    internal static nint OrigDisconnectFromHost => _orig_disconnectFromHost;
    internal static nint OrigConnectToHost => _orig_connectToHost;
    internal static int MessageCount => _messageCount;
    internal static int SubscribeCount => _subscribeCount;
    internal static int GrpcRequestCount => _grpcRequestCount;

    /// <summary>
    /// Exported entry point called by the injector after LoadLibraryW.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "Initialize")]
    public static uint Initialize(nint parameter)
    {
        var thread = new Thread(InitializeCore)
        {
            IsBackground = true,
            Name = "WebullHook-Init"
        };
        thread.Start();
        return 1;
    }

    /// <summary>
    /// Call invokeSubscribeResult on the captured QMqttClient to subscribe to a topic.
    /// Parameter points to a null-terminated UTF-16 string (the topic/symbol).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SubscribeTopic")]
    public static uint SubscribeTopic(nint topicStringPtr)
    {
        try
        {
            if (_mqttClient == 0)
            {
                HookLog.Write("SubscribeTopic: No QMqttClient captured yet!");
                return 0;
            }

            string topic = Marshal.PtrToStringUni(topicStringPtr) ?? "";
            HookLog.Write($"SubscribeTopic: subscribing to \"{topic}\" on client 0x{_mqttClient:X}");

            // Call the original (unhooked) invokeSubscribeResult
            // We need to create a QString on the stack for Qt
            // Actually, let's call through the trampoline which calls the original
            if (_orig_invokeSubscribeResult != 0)
            {
                // We need to pass a QString*, not a raw string. Create a temporary one.
                // For now, call subscribe with the QMqttTopicFilter variant
                HookLog.Write($"  Using invokeSubscribeResult trampoline at 0x{_orig_invokeSubscribeResult:X}");
            }

            // Simpler: use the subscribe function with a topic filter string
            if (_orig_subscribe != 0)
            {
                HookLog.Write($"  (subscribe trampoline available at 0x{_orig_subscribe:X})");
            }

            HookLog.Write($"SubscribeTopic: done for \"{topic}\"");
            return 1;
        }
        catch (Exception ex)
        {
            HookLog.Write($"SubscribeTopic error: {ex}");
            return 0;
        }
    }

    /// <summary>
    /// Returns the captured QMqttClient pointer (0 if not yet captured).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "GetMqttClient")]
    public static nint GetMqttClient(nint unused)
    {
        return _mqttClient;
    }

    private static void InitializeCore()
    {
        try
        {
            HookLog.Write("══════════════════════════════════════════════════");
            HookLog.Write("WebullHook v2 loaded. Initializing...");

            PipeServer.Start();
            CommandReceiver.Start();

            // Poll for wbmqtt.dll — inject happens before Webull fully loads
            nint mqttModule = 0;
            for (int i = 0; i < 30; i++)
            {
                mqttModule = NativeMethods.GetModuleHandleW("wbmqtt.dll");
                if (mqttModule != 0) break;
                if (i == 0) HookLog.Write("Waiting for wbmqtt.dll...");
                Thread.Sleep(200);
            }
            if (mqttModule == 0)
            {
                HookLog.Write("ERROR: wbmqtt.dll not found after 6s.");
                return;
            }
            HookLog.Write($"wbmqtt.dll found: 0x{mqttModule:X}. Installing hooks...");

            HookLog.Write("Suspending other threads for safe hook installation...");
            var suspendedThreads = SuspendOtherThreads();
            HookLog.Write($"Suspended {suspendedThreads.Count} threads.");

            // Resolve getter functions (not hooked, called to read state)
            _fn_hostname = NativeMethods.GetProcAddress(mqttModule, Fn_hostname);
            _fn_port = NativeMethods.GetProcAddress(mqttModule, Fn_port);
            _fn_username = NativeMethods.GetProcAddress(mqttModule, Fn_username);
            _fn_clientId = NativeMethods.GetProcAddress(mqttModule, Fn_clientId);

            // ── Hook: messageReceived via native MinHook DLL ──────────
            // The C# X64Hook crashes on messageReceived due to security cookie /
            // anti-tamper. MinHook handles CFG, cookies, and instruction relocation.
            InstallNativeMessageReceivedHook(mqttModule);

            // ── Hook: sub.messageReceived (per-subscription messages) ─
            InstallHook(mqttModule, "sub.messageReceived", Fn_subscriptionMessageReceived,
                (nint)(delegate* unmanaged<nint, nint, void>)&Detour_subMessageReceived,
                out _orig_subMessageReceived);

            // ── Hook: subscribe ──────────────────────────────────────
            InstallHook(mqttModule, "subscribe", Fn_subscribe,
                (nint)(delegate* unmanaged<nint, nint, byte, nint>)&Detour_subscribe,
                out _orig_subscribe);

            // ── Hook: connectToHost ──────────────────────────────────
            InstallHook(mqttModule, "connectToHost", Fn_connectToHost,
                (nint)(delegate* unmanaged<nint, void>)&Detour_connectToHost,
                out _orig_connectToHost);

            // ── Hook: invokeSubscribeResult ──────────────────────────
            InstallHook(mqttModule, "invokeSubscribeResult", Fn_invokeSubscribeResult,
                (nint)(delegate* unmanaged<nint, nint, nint>)&Detour_invokeSubscribeResult,
                out _orig_invokeSubscribeResult);

            // ── Hook: connected (signal) ─────────────────────────────
            InstallHook(mqttModule, "connected", Fn_connected,
                (nint)(delegate* unmanaged<nint, void>)&Detour_connected,
                out _orig_connected);

            // ── Hook: publish (captures client via keepalive pings) ────
            InstallHook(mqttModule, "publish", Fn_publish,
                (nint)(delegate* unmanaged<nint, nint, nint, byte, byte, int>)&Detour_publish,
                out _orig_publish);

            // ── Hook: disconnectFromHost ─────────────────────────────
            InstallHook(mqttModule, "disconnectFromHost", Fn_disconnectFromHost,
                (nint)(delegate* unmanaged<nint, void>)&Detour_disconnectFromHost,
                out _orig_disconnectFromHost);

            // ═══════════════════════════════════════════════════
            // gRPC hooks on wbgrpc.dll
            // ═══════════════════════════════════════════════════
            nint grpcModule = NativeMethods.GetModuleHandleW("wbgrpc.dll");
            if (grpcModule == 0)
            {
                HookLog.Write("WARN: wbgrpc.dll not found, skipping gRPC hooks.");
            }
            else
            {
                HookLog.Write($"wbgrpc.dll base: 0x{grpcModule:X}");
                InstallHook(grpcModule, "grpcWrite", Fn_grpcWrite,
                    (nint)(delegate* unmanaged<nint, nint, int>)&Detour_grpcWrite,
                    out _orig_grpcWrite);
            }

            HookLog.Write("All hooks installed. Resuming threads...");
            ResumeThreads(suspendedThreads);
            HookLog.Write($"Resumed {suspendedThreads.Count} threads. Waiting for activity...");
            HookLog.Write("══════════════════════════════════════════════════");
        }
        catch (Exception ex)
        {
            HookLog.Write($"FATAL: {ex}");
        }
    }

    private static void InstallHook(nint module, string name, string mangledName, nint detour, out nint trampoline)
    {
        nint addr = NativeMethods.GetProcAddress(module, mangledName);
        if (addr == 0)
        {
            HookLog.Write($"  SKIP {name}: export not found");
            trampoline = 0;
            return;
        }

        // Dump first 20 bytes for debugging instruction alignment
        byte[] pre = new byte[20];
        Marshal.Copy(addr, pre, 0, 20);
        HookLog.Write($"  {name} at 0x{addr:X} bytes: {Convert.ToHexString(pre)}");

        try
        {
            trampoline = X64Hook.Install(addr, detour);
            HookLog.Write($"  HOOK {name} -> trampoline 0x{trampoline:X}");
        }
        catch (Exception ex)
        {
            HookLog.Write($"  HOOK {name} FAILED: {ex.Message}");
            trampoline = 0;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Native MinHook DLL integration for messageReceived
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Load hook_native.dll and install a MinHook-based hook on messageReceived.
    /// The native DLL calls back into NativeMessageCallback with decoded message data.
    /// </summary>
    private static void InstallNativeMessageReceivedHook(nint mqttModule)
    {
        try
        {
            // Resolve messageReceived target address
            nint msgRecvAddr = NativeMethods.GetProcAddress(mqttModule, Fn_messageReceived);
            if (msgRecvAddr == 0)
            {
                HookLog.Write("  SKIP messageReceived: export not found");
                return;
            }

            // Dump first bytes for debugging
            byte[] pre = new byte[20];
            Marshal.Copy(msgRecvAddr, pre, 0, 20);
            HookLog.Write($"  messageReceived at 0x{msgRecvAddr:X} bytes: {Convert.ToHexString(pre)}");

            // Load hook_native.dll from same directory as this DLL
            // In NativeAOT, Assembly.Location is empty. Use GetModuleHandleW + GetModuleFileName
            // or just try loading from the current working directory / known paths.
            nint hNative = NativeMethods.LoadLibraryW("hook_native.dll");
            if (hNative == 0)
            {
                // Try from the WebullHook directory
                hNative = NativeMethods.LoadLibraryW(@"D:\Third-Parties\WebullHook\hook_native.dll");
            }
            if (hNative == 0)
            {
                HookLog.Write("  WARN: hook_native.dll not found, messageReceived hook skipped.");
                HookLog.Write("  Place hook_native.dll next to the hook DLL or in D:\\Third-Parties\\WebullHook\\");
                return;
            }
            _nativeHookDll = hNative;
            HookLog.Write($"  hook_native.dll loaded at 0x{hNative:X}");

            // Resolve exports
            nint setCallbackFn = NativeMethods.GetProcAddress(hNative, "SetMessageCallback");
            nint installFn = NativeMethods.GetProcAddress(hNative, "InstallMessageReceivedHook");

            if (setCallbackFn == 0 || installFn == 0)
            {
                HookLog.Write("  ERROR: hook_native.dll exports not found");
                return;
            }

            // Set callback: the native detour will call this with decoded UTF-8 topic + raw payload
            var setCallback = (delegate* unmanaged<nint, void>)setCallbackFn;
            setCallback((nint)(delegate* unmanaged<byte*, int, byte*, int, void>)&NativeMessageCallback);
            HookLog.Write("  Native callback registered");

            // Install the MinHook hook
            var install = (delegate* unmanaged<nint, int>)installFn;
            int result = install(msgRecvAddr);

            if (result == 0)
            {
                _nativeMessageReceivedTarget = msgRecvAddr;
                HookLog.Write($"  HOOK messageReceived (native MinHook) -> OK");
            }
            else
            {
                HookLog.Write($"  HOOK messageReceived (native MinHook) FAILED: error code {result}");
            }
        }
        catch (Exception ex)
        {
            HookLog.Write($"  HOOK messageReceived (native) EXCEPTION: {ex.Message}");
        }
    }

    /// <summary>
    /// Callback invoked by hook_native.dll's detour with decoded message data.
    /// Parameters are UTF-8 topic and raw payload bytes.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void NativeMessageCallback(byte* topicUtf8, int topicLen, byte* payload, int payloadLen)
    {
        try
        {
            _messageCount++;

            string topic = topicLen > 0
                ? System.Text.Encoding.UTF8.GetString(topicUtf8, topicLen)
                : string.Empty;

            ReadOnlySpan<byte> payloadSpan = payloadLen > 0
                ? new ReadOnlySpan<byte>(payload, payloadLen)
                : [];

            if (_messageCount <= 20 || _messageCount % 500 == 0)
                HookLog.Write($"MSG #{_messageCount}: topic={topic} ({payloadSpan.Length} bytes)");

            PipeServer.SendMessage(topic, payloadSpan);
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Detour: QMqttSubscription::messageReceived(QMqttMessage)
    // This is the per-subscription signal that fires for each message.
    // QMqttMessage is passed by value (copy) on x64.
    // ═══════════════════════════════════════════════════════════════════
    [UnmanagedCallersOnly]
    private static void Detour_subMessageReceived(nint thisPtr, nint qMqttMessagePtr)
    {
        try
        {
            _messageCount++;

            // QMqttMessage (Qt5, MSVC x64) is passed by hidden pointer in rdx.
            // Layout: QMqttMessagePrivate* d_ptr (8 bytes)
            // QMqttMessagePrivate layout:
            //   QSharedData base (4 bytes ref count + padding)
            //   QMqttTopicName m_topic  → { QString → QArrayData* }
            //   QByteArray m_payload → { QArrayData* }
            //   quint16 m_id
            //   quint8 m_qos
            //   bool m_duplicate
            //   bool m_retain
            // Try reading topic and payload from the QMqttMessage
            if (qMqttMessagePtr != 0)
            {
                // QMqttMessage has a d_ptr to QMqttMessagePrivate
                nint dPtr = *(nint*)qMqttMessagePtr;
                if (dPtr != 0)
                {
                    // QMqttMessagePrivate: skip QSharedData (8 bytes on x64 with alignment)
                    // m_topic at offset 8 (QMqttTopicName = QString = { QArrayData* d })
                    // m_payload at offset 16 (QByteArray = { QArrayData* d })
                    nint topicField = dPtr + 8;
                    nint payloadField = dPtr + 16;

                    string topic = QtInterop.ReadQString(topicField);
                    ReadOnlySpan<byte> payload = QtInterop.ReadQByteArray(payloadField);

                    if (_messageCount <= 5)
                        HookLog.Write($"SUB-MSG #{_messageCount}: topic={topic} ({payload.Length}b) sub=0x{thisPtr:X}");
                    else if (_messageCount % 500 == 0)
                        HookLog.Write($"SUB-MSG #{_messageCount}: topic={topic} ({payload.Length}b)");

                    PipeServer.SendMessage(topic, payload);
                }
            }
        }
        catch (Exception ex)
        {
            if (_messageCount <= 5)
                HookLog.Write($"SUB-MSG #{_messageCount} ERROR: {ex.Message}");
        }

        if (_orig_subMessageReceived != 0)
        {
            var original = (delegate* unmanaged<nint, nint, void>)_orig_subMessageReceived;
            original(thisPtr, qMqttMessagePtr);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Detour: QMqttClient::subscribe(QMqttTopicFilter&, quint8 qos)
    // x64 ABI: rcx=this, rdx=QMqttTopicFilter*, r8=qos byte
    // Returns: QMqttSubscription*
    // ═══════════════════════════════════════════════════════════════════
    [UnmanagedCallersOnly]
    private static nint Detour_subscribe(nint thisPtr, nint topicFilterRef, byte qos)
    {
        try
        {
            _subscribeCount++;
            if (_mqttClient == 0) { _mqttClient = thisPtr; HookLog.Write($"Captured QMqttClient: 0x{thisPtr:X}"); }
            string filter = QtInterop.ReadQString(topicFilterRef);
            HookLog.Write($"SUBSCRIBE #{_subscribeCount}: filter=\"{filter}\" qos={qos} client=0x{thisPtr:X}");
        }
        catch (Exception ex)
        {
            HookLog.Write($"SUBSCRIBE error reading filter: {ex.Message}");
        }

        if (_orig_subscribe != 0)
        {
            var original = (delegate* unmanaged<nint, nint, byte, nint>)_orig_subscribe;
            return original(thisPtr, topicFilterRef, qos);
        }
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Detour: QMqttClient::connectToHost()
    // x64 ABI: rcx=this
    // ═══════════════════════════════════════════════════════════════════
    [UnmanagedCallersOnly]
    private static void Detour_connectToHost(nint thisPtr)
    {
        try
        {
            if (_mqttClient == 0) { _mqttClient = thisPtr; HookLog.Write($"Captured QMqttClient: 0x{thisPtr:X}"); }
            HookLog.Write($"CONNECT: client=0x{thisPtr:X}");

            // Read connection parameters using getter functions
            if (_fn_hostname != 0)
            {
                // QString hostname() is: rcx=this, returns QString by value
                // On x64 MSVC, small types returned in RAX, but QString is returned via hidden param
                // Actually for Qt, hostname() returns QString - need hidden return pointer
                // Let's use a simpler approach: read the hostname by calling through a delegate
                HookLog.Write($"  (getter addresses: hostname=0x{_fn_hostname:X}, port=0x{_fn_port:X}, username=0x{_fn_username:X}, clientId=0x{_fn_clientId:X})");
            }

            // Read port directly (returns ushort in ax, simple)
            if (_fn_port != 0)
            {
                var getPort = (delegate* unmanaged<nint, ushort>)_fn_port;
                ushort port = getPort(thisPtr);
                HookLog.Write($"  port={port}");
            }
        }
        catch (Exception ex)
        {
            HookLog.Write($"CONNECT error: {ex.Message}");
        }

        if (_orig_connectToHost != 0)
        {
            var original = (delegate* unmanaged<nint, void>)_orig_connectToHost;
            original(thisPtr);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Detour: QMqttClient::connected() [signal]
    // x64 ABI: rcx=this
    // ═══════════════════════════════════════════════════════════════════
    [UnmanagedCallersOnly]
    private static void Detour_connected(nint thisPtr)
    {
        try
        {
            if (_mqttClient == 0) { _mqttClient = thisPtr; HookLog.Write($"Captured QMqttClient: 0x{thisPtr:X}"); }
            HookLog.Write($"CONNECTED: client=0x{thisPtr:X}");
            PipeServer.SendEvent("connected", $"0x{thisPtr:X}");

            // Now that we're connected, try to read connection info
            if (_fn_port != 0)
            {
                var getPort = (delegate* unmanaged<nint, ushort>)_fn_port;
                ushort port = getPort(thisPtr);
                HookLog.Write($"  port={port}");
            }
        }
        catch (Exception ex)
        {
            HookLog.Write($"CONNECTED error: {ex.Message}");
        }

        if (_orig_connected != 0)
        {
            var original = (delegate* unmanaged<nint, void>)_orig_connected;
            original(thisPtr);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Detour: QMqttClient::invokeSubscribeResult(const QString&)
    // x64 ABI: rcx=this, rdx=QString*
    // Returns: QMqtt::ReasonCode (int/enum)
    // ═══════════════════════════════════════════════════════════════════
    [UnmanagedCallersOnly]
    private static nint Detour_invokeSubscribeResult(nint thisPtr, nint qStringRef)
    {
        try
        {
            _subscribeCount++;
            if (_mqttClient == 0) { _mqttClient = thisPtr; HookLog.Write($"Captured QMqttClient: 0x{thisPtr:X}"); }
            string topic = QtInterop.ReadQString(qStringRef);
            HookLog.Write($"INVOKE-SUBSCRIBE #{_subscribeCount}: topic=\"{topic}\" client=0x{thisPtr:X}");
            PipeServer.SendEvent("subscribe", topic);

            // Persist auth header directly to file (since TCP doesn't carry events)
            try
            {
                if (topic.Contains("\"header\""))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(topic);
                    if (doc.RootElement.TryGetProperty("header", out var header))
                    {
                        string headerJson = header.ToString();
                        string authPath = System.IO.Path.Combine(@"D:\Third-Parties\WebullHook", "auth_header.json");
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(authPath)!);
                        System.IO.File.WriteAllText(authPath, headerJson);
                    }
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            HookLog.Write($"INVOKE-SUBSCRIBE error: {ex.Message}");
        }

        if (_orig_invokeSubscribeResult != 0)
        {
            var original = (delegate* unmanaged<nint, nint, nint>)_orig_invokeSubscribeResult;
            return original(thisPtr, qStringRef);
        }
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Detour: QMqttClient::publish(QMqttTopicName&, QByteArray&, quint8, bool)
    // x64 ABI: rcx=this, rdx=QMqttTopicName*, r8=QByteArray*, r9=qos
    // Stack: [rsp+0x28]=retain (bool)
    // Returns: int (packet id)
    // ═══════════════════════════════════════════════════════════════════
    [UnmanagedCallersOnly]
    private static int Detour_publish(nint thisPtr, nint topicNameRef, nint qByteArrayRef, byte qos, byte retain)
    {
        try
        {
            if (_mqttClient == 0) { _mqttClient = thisPtr; HookLog.Write($"Captured QMqttClient via publish: 0x{thisPtr:X}"); }
            string topic = QtInterop.ReadQMqttTopicName(topicNameRef);
            ReadOnlySpan<byte> payload = QtInterop.ReadQByteArray(qByteArrayRef);
            HookLog.Write($"PUBLISH: topic=\"{topic}\" payload={payload.Length}b qos={qos} retain={retain}");
        }
        catch (Exception ex)
        {
            HookLog.Write($"PUBLISH error: {ex.Message}");
        }

        if (_orig_publish != 0)
        {
            var original = (delegate* unmanaged<nint, nint, nint, byte, byte, int>)_orig_publish;
            return original(thisPtr, topicNameRef, qByteArrayRef, qos, retain);
        }
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Detour: QMqttClient::disconnectFromHost()
    // x64 ABI: rcx=this
    // ═══════════════════════════════════════════════════════════════════
    [UnmanagedCallersOnly]
    private static void Detour_disconnectFromHost(nint thisPtr)
    {
        try
        {
            HookLog.Write($"DISCONNECT: client=0x{thisPtr:X}");
            PipeServer.SendEvent("disconnected", $"0x{thisPtr:X}");
        }
        catch { }

        if (_orig_disconnectFromHost != 0)
        {
            var original = (delegate* unmanaged<nint, void>)_orig_disconnectFromHost;
            original(thisPtr);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // gRPC Detours
    // ═══════════════════════════════════════════════════════════════════

    // ── Detour: getHeadMd5Sign(const QMap<QString,QString>&) → QString
    // MSVC x64 with return-by-value: rcx=this, rdx=hidden return (QString*), r8=QMap*
    // We call original, then try to read the result.
    [UnmanagedCallersOnly]
    private static void Detour_getHeadMd5Sign(nint thisPtr, nint resultQString, nint qMapPtr)
    {
        // Call original first — it fills resultQString with the MD5 sign
        if (_orig_getHeadMd5Sign != 0)
        {
            var original = (delegate* unmanaged<nint, nint, nint, void>)_orig_getHeadMd5Sign;
            original(thisPtr, resultQString, qMapPtr);
        }

        try
        {
            _grpcRequestCount++;

            // Read the result QString (the computed sign)
            // resultQString points to a QString constructed by the original function
            string sign = QtInterop.ReadQString(resultQString);

            HookLog.Write($"GRPC-SIGN #{_grpcRequestCount}: sign={sign}");
            PipeServer.SendEvent("grpc_sign", sign);
        }
        catch (Exception ex)
        {
            HookLog.Write($"GRPC-SIGN error: {ex.Message}");
        }
    }

    // ── Detour: write(const ClientRequest&)
    // MSVC x64: rcx=this, rdx=const ClientRequest*
    // Dumps raw memory of the C++ protobuf ClientRequest for analysis.
    [UnmanagedCallersOnly]
    private static int Detour_grpcWrite(nint thisPtr, nint clientRequestPtr)
    {
        _grpcRequestCount++;

        try
        {
            HookLog.Write($"GRPC-WRITE #{_grpcRequestCount}: ClientRequest ptr=0x{clientRequestPtr:X}");

            // Log that we saw the gRPC write (skip raw memory dump to avoid crashes)
            if (clientRequestPtr != 0)
            {
                PipeServer.SendEvent("grpc_write", $"ptr=0x{clientRequestPtr:X}");
            }
        }
        catch (Exception ex)
        {
            HookLog.Write($"GRPC-WRITE #{_grpcRequestCount} error: {ex.Message}");
        }

        if (_orig_grpcWrite != 0)
        {
            var original = (delegate* unmanaged<nint, nint, int>)_orig_grpcWrite;
            return original(thisPtr, clientRequestPtr);
        }
        return 0;
    }

    /// <summary>Suspend all threads in the current process except our own.</summary>
    private static List<nint> SuspendOtherThreads()
    {
        var handles = new List<nint>();
        uint myTid = NativeMethods.GetCurrentThreadId();
        uint myPid = NativeMethods.GetCurrentProcessId();

        nint snap = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPTHREAD, 0);
        if (snap == -1) return handles;

        try
        {
            var te = new NativeMethods.THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<NativeMethods.THREADENTRY32>() };
            if (NativeMethods.Thread32First(snap, ref te))
            {
                do
                {
                    if (te.th32OwnerProcessID == myPid && te.th32ThreadID != myTid)
                    {
                        nint hThread = NativeMethods.OpenThread(NativeMethods.THREAD_SUSPEND_RESUME, false, te.th32ThreadID);
                        if (hThread != 0)
                        {
                            NativeMethods.SuspendThread(hThread);
                            handles.Add(hThread);
                        }
                    }
                } while (NativeMethods.Thread32Next(snap, ref te));
            }
        }
        finally
        {
            NativeMethods.CloseHandle(snap);
        }

        return handles;
    }

    /// <summary>Resume previously suspended threads.</summary>
    private static void ResumeThreads(List<nint> handles)
    {
        foreach (nint h in handles)
        {
            NativeMethods.ResumeThread(h);
            NativeMethods.CloseHandle(h);
        }
    }
}
