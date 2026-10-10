#if !NET8_0_OR_GREATER
using System;
#endif

namespace Repro;

// Binds only in netstandard2.0, so the copy found first (net8.0) is the one whose types fail to bind.
public class Legacy
{
    public void Wide(Int32 value) { } // Error type in net8.0 ('Int32'); binds in netstandard2.0 (renders 'int').

    public void Size(Int32 value) { } // Overloaded, so get_callees prints the parameter list.

    public void Size(string text) { }

    public void Caller()
    {
        Wide(1);
        Size(1);
    }
}
