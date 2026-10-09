#if NET8_0_OR_GREATER
using System;
#endif

namespace Repro;

public class Api
{
    public bool Accepts(Type type) => type is not null; // Type is an error type in netstandard2.0.

    public int Count() => 0;

    public string Echo(string text) => text; // Binds in every target framework.
}
