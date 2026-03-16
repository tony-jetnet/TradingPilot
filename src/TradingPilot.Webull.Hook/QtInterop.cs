using System.Runtime.InteropServices;
using System.Text;

namespace TradingPilot.Webull.Hook;

/// <summary>
/// Helpers to read Qt5 types from memory (QByteArray, QString, QMqttTopicName).
/// </summary>
internal static unsafe class QtInterop
{
    // Qt5 QArrayData layout (x64):
    //   int ref;           // offset 0, 4 bytes (atomic ref count)
    //   int size;           // offset 4, 4 bytes
    //   uint alloc:31;      // offset 8, 4 bytes (packed with capacityReserved)
    //   uint caps:1;
    //   long offset;        // offset 16, 8 bytes (offset from QArrayData* to actual data)
    // Data is at: (byte*)d + d->offset

    /// <summary>
    /// Read a QByteArray's content. QByteArray is a pointer to QArrayData.
    /// The QByteArray object itself is just { QArrayData* d; }.
    /// </summary>
    public static ReadOnlySpan<byte> ReadQByteArray(nint qByteArrayPtr)
    {
        if (qByteArrayPtr == 0) return [];

        // QByteArray is { QTypedArrayData<char>* d; }
        nint d = *(nint*)qByteArrayPtr;
        if (d == 0) return [];

        int size = *(int*)(d + 4);
        if (size <= 0 || size > 10 * 1024 * 1024) return []; // sanity check: max 10MB

        long offset = *(long*)(d + 16);
        byte* data = (byte*)d + offset;

        return new ReadOnlySpan<byte>(data, size);
    }

    /// <summary>
    /// Read a QString's content as UTF-16. QString is { QTypedArrayData{QChar}* d; }.
    /// QChar is 2 bytes (UTF-16).
    /// </summary>
    public static string ReadQString(nint qStringPtr)
    {
        if (qStringPtr == 0) return string.Empty;

        nint d = *(nint*)qStringPtr;
        if (d == 0) return string.Empty;

        int size = *(int*)(d + 4); // number of QChar (UTF-16 code units)
        if (size <= 0 || size > 1024 * 1024) return string.Empty;

        long offset = *(long*)(d + 16);
        char* data = (char*)((byte*)d + offset);

        return new string(data, 0, size);
    }

    /// <summary>
    /// Read a QMqttTopicName. QMqttTopicName is { QString m_name; } which is { QArrayData* d; }.
    /// So QMqttTopicName* points to a QString which is just a QArrayData*.
    /// </summary>
    public static string ReadQMqttTopicName(nint topicNamePtr)
    {
        // QMqttTopicName wraps QString, same memory layout
        return ReadQString(topicNamePtr);
    }
}
