using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Morris.Roslynk.Plugins.V1;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// Reads a candidate entry assembly's API version without loading it for execution: a
/// <see cref="MetadataLoadContext"/> load that is disposed immediately after the attribute read.
/// When the attribute type cannot be resolved (for example the plugin deployed without its contract
/// assembly), a System.Reflection.Metadata blob decode reads the same value out of the raw
/// metadata, because an enum argument is encoded as its underlying integer.
/// </summary>
public sealed class PluginProber
{
	private const string AttributeName = "RoslynkApiVersionAttribute";
	private const string AttributeNamespacePrefix = "Morris.Roslynk.Plugins.V";
	private const string ContractAssemblyName = "Morris.Roslynk.Plugins.V1";

	private readonly string? RuntimeDirectory;
	private readonly string? ContractAssemblyPath;

	public PluginProber(string? runtimeDirectory = null, string? contractAssemblyPath = null)
	{
		RuntimeDirectory = runtimeDirectory ?? Path.GetDirectoryName(typeof(object).Assembly.Location);
		ContractAssemblyPath = contractAssemblyPath ?? typeof(ApiVersion).Assembly.Location;
	}

	/// <summary>Reflects over <paramref name="entryAssemblyPath"/> and reads its API version.</summary>
	public PluginProbeResult Probe(string entryAssemblyPath)
	{
		IReadOnlyList<PluginAssemblyReference> references;
		try
		{
			using var loadContext = CreateLoadContext(entryAssemblyPath);
			Assembly entry = loadContext.LoadFromAssemblyPath(entryAssemblyPath);
			references = entry
				.GetReferencedAssemblies()
				.Select(name => new PluginAssemblyReference(name.Name ?? "", name.Version?.ToString() ?? ""))
				.ToArray();
			ApiVersion? version = ReadApiVersionTyped(entry) ?? ReadApiVersionFromBlob(entryAssemblyPath);
			if (version is null)
				return PluginProbeResult.Failed(PluginDiscoveryStage.MissingApiVersionAttribute, "The entry assembly carries no RoslynkApiVersion attribute", references);
			return PluginProbeResult.Succeeded(version.Value, references);
		}
		catch (Exception exception) when (exception is BadImageFormatException or FileNotFoundException or IOException or UnauthorizedAccessException)
		{
			return PluginProbeResult.Failed(
				exception is BadImageFormatException ? PluginDiscoveryStage.Malformed : PluginDiscoveryStage.UnreadableOrLocked,
				$"The entry assembly could not be read: {exception.Message}");
		}
	}

	private MetadataLoadContext CreateLoadContext(string entryAssemblyPath)
	{
		var paths = new List<string>();

		// The runtime's own assemblies act as the core set, so the context can bind System.* types.
		if (RuntimeDirectory is { } runtimeDirectory)
			paths.AddRange(Directory.EnumerateFiles(runtimeDirectory, "*.dll"));

		// Prefer the contract assembly the plugin deployed with; the host's own copy is the fallback
		// that keeps the typed read possible for a bare entry assembly.
		string? contractPath = ResolveContractPath(entryAssemblyPath);
		if (contractPath is not null)
			paths.Add(contractPath);
		else if (File.Exists(ContractAssemblyPath))
			paths.Add(ContractAssemblyPath);

		paths.Add(entryAssemblyPath);
		return new MetadataLoadContext(new PathAssemblyResolver(paths));
	}

	private static string? ResolveContractPath(string entryAssemblyPath)
	{
		try
		{
			return new AssemblyDependencyResolver(entryAssemblyPath).ResolveAssemblyToPath(new AssemblyName(ContractAssemblyName));
		}
		catch (Exception exception) when (exception is IOException or ArgumentException or BadImageFormatException)
		{
			// No deps.json (or an unreadable one) is expected for hand-deployed plugins; the typed
			// read falls back to the host's contract and then to the blob decode.
			return null;
		}
	}

	private static ApiVersion? ReadApiVersionTyped(Assembly entryAssembly)
	{
		try
		{
			CustomAttributeData? data = entryAssembly
				.GetCustomAttributesData()
				.FirstOrDefault(attribute => IsApiVersionAttributeFamily(attribute.AttributeType));
			if (data is null)
				return null;

			return data.ConstructorArguments.Single().Value switch
			{
				ApiVersion version => version,
				int underlying => (ApiVersion)underlying,
				_ => null,
			};
		}
		catch (Exception)
		{
			// The attribute's declaring assembly may be absent from the context; the blob decode
			// decides then. Any resolution failure is the fallback's cue, not a plugin fault.
			return null;
		}
	}

	private static bool IsApiVersionAttributeFamily(Type? type) =>
		type is { }
		&& type.Name == AttributeName
		&& type.Namespace is { } ns
		&& ns.StartsWith(AttributeNamespacePrefix, StringComparison.Ordinal);

	/// <summary>Reads the stamped API version straight from the assembly's metadata tables.</summary>
	internal static ApiVersion? ReadApiVersionFromBlob(string entryAssemblyPath)
	{
		try
		{
			using var stream = File.OpenRead(entryAssemblyPath);
			using var peReader = new PEReader(stream);
			if (!peReader.HasMetadata)
				return null;

			var reader = peReader.GetMetadataReader();
			foreach (CustomAttributeHandle handle in reader.GetAssemblyDefinition().GetCustomAttributes())
			{
				CustomAttribute attribute = reader.GetCustomAttribute(handle);
				if (!IsApiVersionAttributeConstructor(reader, attribute.Constructor))
					continue;

				byte[] blob = reader.GetBlobBytes(attribute.Value);

				// A fixed argument carries no element-type code (the constructor's signature names the
				// type): the blob is the two-byte prolog (0x01 0x00), the little-endian int32 value of
				// the enum, then the named-argument count.
				if (blob.Length >= 6 && blob[0] == 0x01 && blob[1] == 0x00)
					return (ApiVersion)BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(2));
			}

			return null;
		}
		catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static bool IsApiVersionAttributeConstructor(MetadataReader reader, EntityHandle constructor)
	{
		// An attribute whose declaring type lives in the same module has a MethodDefinition
		// constructor; one pulled in from the contract assembly has a MemberReference.
		if (constructor.Kind == HandleKind.MethodDefinition)
		{
			MethodDefinition method = reader.GetMethodDefinition((MethodDefinitionHandle)constructor);
			TypeDefinition declaringDefinition = reader.GetTypeDefinition(method.GetDeclaringType());
			return MatchesApiVersionAttribute(declaringDefinition.Name, declaringDefinition.Namespace, reader);
		}

		if (constructor.Kind != HandleKind.MemberReference)
			return false;

		MemberReference member = reader.GetMemberReference((MemberReferenceHandle)constructor);
		if (member.Parent.Kind != HandleKind.TypeReference)
			return false;

		TypeReference declaringType = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
		return MatchesApiVersionAttribute(declaringType.Name, declaringType.Namespace, reader);
	}

	private static bool MatchesApiVersionAttribute(StringHandle nameHandle, StringHandle namespaceHandle, MetadataReader reader) =>
		reader.GetString(nameHandle) == AttributeName
		&& !namespaceHandle.IsNil
		&& reader.GetString(namespaceHandle).StartsWith(AttributeNamespacePrefix, StringComparison.Ordinal);
}
