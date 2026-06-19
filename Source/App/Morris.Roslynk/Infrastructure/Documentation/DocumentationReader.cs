using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Infrastructure.Documentation;

/// <summary>
/// Turns a symbol's XML documentation comment into a <see cref="SymbolDocumentation"/>: the standard
/// sections, with inline tags (<c>&lt;see cref&gt;</c>, <c>&lt;paramref&gt;</c>, <c>&lt;c&gt;</c>)
/// normalized to markdown. <c>&lt;inheritdoc/&gt;</c> is resolved by following the overridden or
/// implemented member (Roslyn does not expand it), so the inherited text is returned directly.
/// </summary>
public static partial class DocumentationReader
{
	private const int MaxInheritDepth = 8;

	public static SymbolDocumentation Read(ISymbol symbol, CancellationToken cancellationToken = default) =>
		Read(symbol, depth: 0, cancellationToken);

	private static SymbolDocumentation Read(ISymbol symbol, int depth, CancellationToken cancellationToken)
	{
		if (depth > MaxInheritDepth)
			return SymbolDocumentation.None;

		string? xml = symbol.GetDocumentationCommentXml(preferredCulture: null, expandIncludes: true, cancellationToken);
		if (string.IsNullOrWhiteSpace(xml))
			return SymbolDocumentation.None;

		XElement root;
		try
		{
			root = XElement.Parse(xml, LoadOptions.PreserveWhitespace);
		}
		catch (XmlException)
		{
			return SymbolDocumentation.None;
		}

		if (root.Elements("inheritdoc").Any())
		{
			ISymbol? inheritedSymbol = GetInheritedSymbol(symbol);
			if (inheritedSymbol is null)
				return SymbolDocumentation.None;

			SymbolDocumentation inherited = Read(inheritedSymbol, depth + 1, cancellationToken);
			return inherited.Source == "none"
				? SymbolDocumentation.None
				: inherited with { Source = "inherited", InheritedFrom = Describe(inheritedSymbol) };
		}

		return Parse(root);
	}

	private static SymbolDocumentation Parse(XElement root)
	{
		DocumentationParam[] parameters = root.Elements("param")
			.Select(element => new DocumentationParam(element.Attribute("name")?.Value ?? string.Empty, Normalize(element) ?? string.Empty))
			.ToArray();

		DocumentationException[] exceptions = root.Elements("exception")
			.Select(element => new DocumentationException(SimpleCref(element.Attribute("cref")?.Value), Normalize(element) ?? string.Empty))
			.ToArray();

		return new SymbolDocumentation(
			Summary: Normalize(root.Element("summary")),
			Params: parameters,
			Returns: Normalize(root.Element("returns")),
			Remarks: Normalize(root.Element("remarks")),
			Exceptions: exceptions,
			Source: "own",
			InheritedFrom: null,
			IsLiteralSourceText: false);
	}

	private static string? Normalize(XElement? element)
	{
		if (element is null)
			return null;

		var builder = new StringBuilder();
		AppendNodes(element, builder);
		string text = WhitespaceRegex().Replace(builder.ToString(), " ").Trim();
		return text.Length == 0 ? null : text;
	}

	private static void AppendNodes(XElement element, StringBuilder builder)
	{
		foreach (XNode node in element.Nodes())
		{
			switch (node)
			{
				case XText textNode:
					builder.Append(textNode.Value);
					break;
				case XElement child:
					AppendElement(child, builder);
					break;
			}
		}
	}

	private static void AppendElement(XElement element, StringBuilder builder)
	{
		switch (element.Name.LocalName)
		{
			case "see":
			case "seealso":
				builder.Append(SeeText(element));
				break;
			case "paramref":
			case "typeparamref":
				builder.Append('`').Append(element.Attribute("name")?.Value ?? string.Empty).Append('`');
				break;
			case "c":
			case "code":
				builder.Append('`').Append(element.Value.Trim()).Append('`');
				break;
			case "para":
				builder.Append(' ');
				AppendNodes(element, builder);
				builder.Append(' ');
				break;
			default:
				AppendNodes(element, builder);
				break;
		}
	}

	private static string SeeText(XElement element)
	{
		string? cref = element.Attribute("cref")?.Value;
		if (!string.IsNullOrEmpty(cref))
			return $"`{SimpleCref(cref)}`";

		string? langword = element.Attribute("langword")?.Value;
		if (!string.IsNullOrEmpty(langword))
			return $"`{langword}`";

		return element.Value;
	}

	private static string SimpleCref(string? cref)
	{
		if (string.IsNullOrEmpty(cref))
			return string.Empty;

		string name = cref;
		if (name.Length > 2 && name[1] == ':')
			name = name[2..];

		int parenthesis = name.IndexOf('(');
		if (parenthesis >= 0)
			name = name[..parenthesis];

		int lastDot = name.LastIndexOf('.');
		return lastDot >= 0 ? name[(lastDot + 1)..] : name;
	}

	private static ISymbol? GetInheritedSymbol(ISymbol symbol) =>
		symbol switch
		{
			IMethodSymbol method => method.OverriddenMethod ?? FirstInterfaceMember(method),
			IPropertySymbol property => property.OverriddenProperty ?? FirstInterfaceMember(property),
			IEventSymbol @event => @event.OverriddenEvent ?? FirstInterfaceMember(@event),
			INamedTypeSymbol type when type.BaseType is INamedTypeSymbol baseType && baseType.SpecialType != SpecialType.System_Object => baseType,
			_ => null,
		};

	private static ISymbol? FirstInterfaceMember(ISymbol symbol)
	{
		ISymbol? explicitImplementation = symbol switch
		{
			IMethodSymbol method => method.ExplicitInterfaceImplementations.FirstOrDefault(),
			IPropertySymbol property => property.ExplicitInterfaceImplementations.FirstOrDefault(),
			IEventSymbol @event => @event.ExplicitInterfaceImplementations.FirstOrDefault(),
			_ => null,
		};
		if (explicitImplementation is not null)
			return explicitImplementation;

		INamedTypeSymbol? containingType = symbol.ContainingType;
		if (containingType is null)
			return null;

		foreach (INamedTypeSymbol @interface in containingType.AllInterfaces)
		{
			foreach (ISymbol member in @interface.GetMembers())
			{
				if (SymbolEqualityComparer.Default.Equals(containingType.FindImplementationForInterfaceMember(member), symbol))
					return member;
			}
		}

		return null;
	}

	private static DocumentationInheritedFrom Describe(ISymbol symbol)
	{
		Location? location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
		string? sourcePath = location?.GetDisplaySpan().Path;
		string? assembly = location is null ? symbol.ContainingAssembly?.Name : null;
		return new DocumentationInheritedFrom(SymbolResolver.FullyQualifiedName(symbol), sourcePath, assembly);
	}

	[GeneratedRegex(@"\s+")]
	private static partial Regex WhitespaceRegex();
}
