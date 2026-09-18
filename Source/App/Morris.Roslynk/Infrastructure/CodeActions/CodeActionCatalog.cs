using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;

namespace Morris.Roslynk.Infrastructure.CodeActions;

/// <summary>
/// Discovers Roslyn's built-in C# code-fix and refactoring providers from the Features assemblies by
/// reflection, instantiating those with an accessible parameterless constructor. Providers whose
/// constructor needs host services we do not compose (MEF <c>[ImportingConstructor]</c>) are skipped
/// rather than failing the whole catalog, so we expose the broad subset that works headlessly. Built once.
/// </summary>
public sealed class CodeActionCatalog
{
	private const string CSharp = "C#";

	private static readonly Lazy<CodeActionCatalog> Shared = new(Build);

	public static CodeActionCatalog Instance => Shared.Value;

	public IReadOnlyList<CodeFixProvider> FixProviders { get; }
	public IReadOnlyList<CodeRefactoringProvider> RefactoringProviders { get; }

	/// <summary>Every diagnostic id some fix provider in the catalog handles, read once per process.</summary>
	public ImmutableHashSet<string> FixableDiagnosticIds { get; }

	private CodeActionCatalog(IReadOnlyList<CodeFixProvider> fixProviders, IReadOnlyList<CodeRefactoringProvider> refactoringProviders)
	{
		FixProviders = fixProviders;
		RefactoringProviders = refactoringProviders;
		FixableDiagnosticIds = fixProviders.SelectMany(provider => FixableIds(provider).AsEnumerable()).ToImmutableHashSet(StringComparer.Ordinal);
	}

	/// <summary>
	/// Fixer trigger ids that are not the id a caller would name, mapped to the id they would. Roslyn's
	/// unnecessary-imports fixer does not register against IDE0005: its analyzer reports a second, hidden
	/// diagnostic whose id is the fixer's private trigger, and the IDE matches the fix on that. A fix found
	/// that way is reported under the public id so 'apply_code_fix IDE0005' finds it.
	/// </summary>
	private static readonly ImmutableDictionary<string, string> PublicIds = ImmutableDictionary
		.CreateRange(StringComparer.Ordinal, [KeyValuePair.Create("RemoveUnnecessaryImportsFixable", "IDE0005")]);

	/// <summary>The id a caller would name for a fix triggered by <paramref name="diagnosticId"/>.</summary>
	public static string PublicId(string diagnosticId) =>
		PublicIds.TryGetValue(diagnosticId, out string? publicId) ? publicId : diagnosticId;

	/// <summary>
	/// Whether the id exists only to trigger a fixer. Such a diagnostic carries no message and duplicates
	/// the public one it accompanies, so it is worth listing nowhere - the public id is what a caller acts on.
	/// </summary>
	public static bool IsPrivateFixTrigger(string diagnosticId) => PublicIds.ContainsKey(diagnosticId);

	/// <summary>The ids a provider claims to fix, or none when asking it throws.</summary>
	public static ImmutableArray<string> FixableIds(CodeFixProvider provider)
	{
		try
		{
			return provider.FixableDiagnosticIds;
		}
		catch
		{
			return [];
		}
	}

	private static CodeActionCatalog Build()
	{
		Assembly[] assemblies = FeatureAssemblies();
		return new CodeActionCatalog(
			Instantiate<CodeFixProvider>(assemblies, "ExportCodeFixProviderAttribute"),
			Instantiate<CodeRefactoringProvider>(assemblies, "ExportCodeRefactoringProviderAttribute"));
	}

	private static List<T> Instantiate<T>(Assembly[] assemblies, string exportAttributeName)
		where T : class
	{
		var result = new List<T>();
		foreach (Assembly assembly in assemblies)
		{
			foreach (Type type in SafeGetTypes(assembly))
			{
				if (type.IsAbstract || !typeof(T).IsAssignableFrom(type) || !SupportsCSharp(type, exportAttributeName))
					continue;

				try
				{
					if (Activator.CreateInstance(type, nonPublic: true) is T instance)
						result.Add(instance);
				}
				catch
				{
					// The provider needs imports we do not compose; skip it.
				}
			}
		}

		return result;
	}

	private static bool SupportsCSharp(Type type, string exportAttributeName)
	{
		foreach (CustomAttributeData attribute in type.GetCustomAttributesData())
		{
			if (attribute.AttributeType.Name != exportAttributeName)
				continue;

			foreach (CustomAttributeTypedArgument argument in attribute.ConstructorArguments)
			{
				if (argument.Value is string language && language == CSharp)
					return true;
				if (argument.Value is IReadOnlyList<CustomAttributeTypedArgument> languages
					&& languages.Any(item => item.Value is string value && value == CSharp))
				{
					return true;
				}
			}
		}

		return false;
	}

	private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
	{
		try
		{
			return assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException exception)
		{
			return exception.Types.Where(type => type is not null)!;
		}
	}

	private static Assembly[] FeatureAssemblies()
	{
		var assemblies = new List<Assembly> { typeof(CodeFixProvider).Assembly };
		foreach (string name in new[] { "Microsoft.CodeAnalysis.Features", "Microsoft.CodeAnalysis.CSharp.Features" })
		{
			try
			{
				assemblies.Add(Assembly.Load(name));
			}
			catch
			{
				// Assembly not present; the providers it would contribute are simply unavailable.
			}
		}

		return assemblies.Distinct().ToArray();
	}
}
