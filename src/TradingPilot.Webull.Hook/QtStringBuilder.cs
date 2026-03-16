using System.Runtime.InteropServices;

namespace TradingPilot.Webull.Hook;

/// <summary>
/// Builds a Qt5 QString in unmanaged memory so it can be passed to Qt functions
/// like invokeSubscribeResult. The caller must call Dispose() to free memory.
///
/// Qt5 QString layout:
///   struct { QTypedArrayData&lt;QChar&gt;* d; }
/// QArrayData layout (x64):
///   int ref       (offset 0)  - atomic ref count
///   int size      (offset 4)  - number of QChar (UTF-16 code units)
///   uint alloc    (offset 8)  - capacity (31 bits) + capacityReserved (1 bit)
///   long offset   (offset 16) - byte offset from QArrayData* to first QChar
/// Data starts at: (byte*)d + d->offset
/// </summary>
internal readonly unsafe struct QtString : IDisposable
{
    /// <summary>Pointer to the QString struct (which contains a pointer to QArrayData).</summary>
    public nint Ptr { get; }

    private QtString(nint ptr) => Ptr = ptr;

    /// <summary>
    /// Allocate a QString in unmanaged memory from a .NET string.
    /// </summary>
    public static QtString Create(string text)
    {
        int charCount = text.Length;
        // QArrayData header = 24 bytes, followed by UTF-16 data + null terminator
        int headerSize = 24;
        int dataSize = (charCount + 1) * 2; // +1 for null terminator, *2 for UTF-16
        int totalArrayData = headerSize + dataSize;

        // Allocate: 8 bytes for QString (pointer to QArrayData) + QArrayData itself
        nint mem = Marshal.AllocHGlobal(8 + totalArrayData);
        nint arrayData = mem + 8;

        // Fill QArrayData header
        *(int*)(arrayData + 0) = -1;           // ref = -1 (static/immortal, won't be freed by Qt)
        *(int*)(arrayData + 4) = charCount;     // size
        *(uint*)(arrayData + 8) = (uint)charCount; // alloc (capacity)
        *(long*)(arrayData + 16) = headerSize;  // offset from QArrayData* to data

        // Copy UTF-16 data
        char* dst = (char*)(arrayData + headerSize);
        for (int i = 0; i < charCount; i++)
            dst[i] = text[i];
        dst[charCount] = '\0'; // null terminator

        // QString struct: just a pointer to the QArrayData
        *(nint*)mem = arrayData;

        return new QtString(mem);
    }

    public void Dispose()
    {
        if (Ptr != 0)
            Marshal.FreeHGlobal(Ptr);
    }
}
