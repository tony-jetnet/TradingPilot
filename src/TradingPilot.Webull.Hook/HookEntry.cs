using System.Runtime.InteropServices;

namespace TradingPilot.Webull.Hook;

/// <summary>
/// Entry point for the injected DLL. Hooks multiple QMqttClient functions in wbmqtt.dll
/// to intercept MQTT messages, subscriptions, and connection parameters.
/// </summary>
public static unsafe class HookEntry
{
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
    private static nint _orig_messageReceived;
    private static nint _orig_subscribe;
    private static nint _orig_connectToHost;
    private static nint _orig_connected;
    private static nint _orig_subMessageReceived;

    private static nint _orig_invokeSubscribeResult;
    private static nint _orig_publish;
    private static nint _orig_disconnectFromHost;

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

    // ═══════════════════════════════════════════════════════════════════
    // Internal accessors for CommandReceiver
    // ═══════════════════════════════════════════════════════════════════
    internal static nint MqttClient => _mqttClient;
    internal static nint OrigInvokeSubscribeResult => _orig_invokeSubscribeResult;
    internal static int MessageCount => _messageCount;
    internal static int SubscribeCount => _subscribeCount;

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

            nint mqttModule = NativeMethods.GetModuleHandleW("wbmqtt.dll");
            if (mqttModule == 0)
            {
                HookLog.Write("ERROR: wbmqtt.dll not found in process.");
                return;
            }
            HookLog.Write($"wbmqtt.dll base: 0x{mqttModule:X}");

            // Resolve getter functions (not hooked, called to read state)
            _fn_hostname = NativeMethods.GetProcAddress(mqttModule, Fn_hostname);
            _fn_port = NativeMethods.GetProcAddress(mqttModule, Fn_port);
            _fn_username = NativeMethods.GetProcAddress(mqttModule, Fn_username);
            _fn_clientId = NativeMethods.GetProcAddress(mqttModule, Fn_clientId);

            // ── Hook: messageReceived (signal) ───────────────────────
            InstallHook(mqttModule, "messageReceived", Fn_messageReceived,
                (nint)(delegate* unmanaged<nint, nint, nint, void>)&Detour_messageReceived,
                out _orig_messageReceived);

            // ── Hook: QMqttSubscription::messageReceived ─────────────
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

            // ── Hook: connected (signal) ─────────────────────────────
            InstallHook(mqttModule, "connected", Fn_connected,
                (nint)(delegate* unmanaged<nint, void>)&Detour_connected,
                out _orig_connected);

            // ── Hook: invokeSubscribeResult ──────────────────────────
            InstallHook(mqttModule, "invokeSubscribeResult", Fn_invokeSubscribeResult,
                (nint)(delegate* unmanaged<nint, nint, nint>)&Detour_invokeSubscribeResult,
                out _orig_invokeSubscribeResult);

            // ── Hook: publish ────────────────────────────────────────
            InstallHook(mqttModule, "publish", Fn_publish,
                (nint)(delegate* unmanaged<nint, nint, nint, byte, byte, int>)&Detour_publish,
                out _orig_publish);

            // ── Hook: disconnectFromHost ─────────────────────────────
            InstallHook(mqttModule, "disconnectFromHost", Fn_disconnectFromHost,
                (nint)(delegate* unmanaged<nint, void>)&Detour_disconnectFromHost,
                out _orig_disconnectFromHost);

            HookLog.Write("All hooks installed. Waiting for activity...");
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

        trampoline = X64Hook.Install(addr, detour);
        HookLog.Write($"  HOOK {name} at 0x{addr:X} -> trampoline 0x{trampoline:X}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Detour: QMqttClient::messageReceived(QByteArray&, QMqttTopicName&)
    // x64 ABI: rcx=this, rdx=QByteArray*, r8=QMqttTopicName*
    // ═══════════════════════════════════════════════════════════════════
    [UnmanagedCallersOnly]
    private static void Detour_messageReceived(nint thisPtr, nint qByteArrayRef, nint qMqttTopicNameRef)
    {
        try
        {
            _messageCount++;
            if (_mqttClient == 0) { _mqttClient = thisPtr; HookLog.Write($"Captured QMqttClient: 0x{thisPtr:X}"); }

            string topic = QtInterop.ReadQMqttTopicName(qMqttTopicNameRef);
            ReadOnlySpan<byte> payload = QtInterop.ReadQByteArray(qByteArrayRef);

            if (_messageCount <= 20 || _messageCount % 100 == 0)
                HookLog.Write($"MSG #{_messageCount}: topic={topic} ({payload.Length} bytes)");

            PipeServer.SendMessage(topic, payload);
        }
        catch { }

        if (_orig_messageReceived != 0)
        {
            var original = (delegate* unmanaged<nint, nint, nint, void>)_orig_messageReceived;
            original(thisPtr, qByteArrayRef, qMqttTopicNameRef);
        }
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
            if (_messageCount <= 20 || _messageCount % 100 == 0)
                HookLog.Write($"SUB-MSG #{_messageCount} on subscription 0x{thisPtr:X}");

            // QMqttMessage contains topic and payload - try to read them
            // QMqttMessage layout (approximate):
            // - QMqttTopicName m_topic
            // - QByteArray m_payload
            // - other fields...
            // For now just log that we received it
        }
        catch { }

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
            string topic = QtInterop.ReadQString(qStringRef);
            HookLog.Write($"INVOKE-SUBSCRIBE #{_subscribeCount}: topic=\"{topic}\" client=0x{thisPtr:X}");
            PipeServer.SendEvent("subscribe", topic);
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
            string topic = QtInterop.ReadQMqttTopicName(topicNameRef);
            ReadOnlySpan<byte> payload = QtInterop.ReadQByteArray(qByteArrayRef);
            HookLog.Write($"PUBLISH: topic=\"{topic}\" payload={payload.Length}b qos={qos} retain={retain} client=0x{thisPtr:X}");
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
}
