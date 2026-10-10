using System.Text.Json.Serialization;

namespace Repro;

/// <summary>
/// The System.Text.Json source generator ships with the SDK (no package, no build step), so this partial
/// context gains a real source-generated part at load time - the issue's actual case: a tiny hand-written
/// declaration beside a large generated one.
/// </summary>
[JsonSerializable(typeof(Wide))]
internal sealed partial class WideContext : JsonSerializerContext;
