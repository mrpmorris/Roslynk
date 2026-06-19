using Morris.Roslynk.Infrastructure.Outlines;

namespace Morris.Roslynk.Tests.Infrastructure.Outlines;

public class SymbolNodeTests
{
	[Fact]
	public void WhenChildIsCalledTwiceWithTheSameKey_ThenTheSameNodeIsReturnedAndPrintedOnce()
	{
		var root = new SymbolNode();

		root.Child("file.cs").Child("N").Child("class,A").AddLocation(3, 1, 3, 1);
		root.Child("file.cs").Child("N").Child("class,A").AddLocation(9, 2, 9, 2);

		string result = Render(root);

		Assert.Equal("file.cs\n\tN\n\t\tclass,A,3:1|9:2\n", result);
	}

	[Fact]
	public void WhenANodeOnlyParentsChildren_ThenItRendersItsKeyWithNoLocations()
	{
		var root = new SymbolNode();

		SymbolNode outer = root.Child("file.cs").Child("N").Child("class,Outer");
		outer.Child("class,Nested").AddLocation(5, 1, 5, 1);

		string result = Render(root);

		Assert.Equal("file.cs\n\tN\n\t\tclass,Outer\n\t\t\tclass,Nested,5:1\n", result);
	}

	[Fact]
	public void WhenLocationsAreAdded_ThenTheyAreSortedByLineThenColumn()
	{
		var root = new SymbolNode();

		SymbolNode node = root.Child("method,M");
		node.AddLocation(10, 5, 10, 9);
		node.AddLocation(3, 2, 3, 4);
		node.AddLocation(10, 1, 10, 3);

		Assert.Equal("method,M,3:2|10:1|10:5\n", Render(root));
	}

	[Fact]
	public void WhenALocationSpansLines_ThenItRendersStartAndEnd()
	{
		var root = new SymbolNode();

		root.Child("method,M").AddLocation(4, 2, 6, 8);

		Assert.Equal("method,M,4:2-6:8\n", Render(root));
	}

	[Fact]
	public void WhenSiblingsExist_ThenTheyRenderInOrdinalKeyOrder()
	{
		var root = new SymbolNode();

		root.Child("method,Foo").AddLocation(2, 1, 2, 1);
		root.Child("field,Bar").AddLocation(1, 1, 1, 1);

		Assert.Equal("field,Bar,1:1\nmethod,Foo,2:1\n", Render(root));
	}

	[Fact]
	public void WhenChildPathHasAFolder_ThenTheFileNestsUnderAFolderNode()
	{
		var root = new SymbolNode();

		root.ChildPath("A/B/File.cs").Child("class,X").AddLocation(1, 1, 1, 1);

		Assert.Equal("A/B\n\tFile.cs\n\t\tclass,X,1:1\n", Render(root));
	}

	[Fact]
	public void WhenChildPathHasNoFolder_ThenTheFileIsADirectChild()
	{
		var root = new SymbolNode();

		root.ChildPath("File.cs").Child("class,X").AddLocation(1, 1, 1, 1);

		Assert.Equal("File.cs\n\tclass,X,1:1\n", Render(root));
	}

	[Fact]
	public void WhenTwoFilesShareAFolder_ThenTheFolderIsPrintedOnce()
	{
		var root = new SymbolNode();

		root.ChildPath("A/One.cs").Child("class,X").AddLocation(1, 1, 1, 1);
		root.ChildPath("A/Two.cs").Child("class,Y").AddLocation(2, 1, 2, 1);

		Assert.Equal("A\n\tOne.cs\n\t\tclass,X,1:1\n\tTwo.cs\n\t\tclass,Y,2:1\n", Render(root));
	}

	private static string Render(SymbolNode root)
	{
		var builder = new OutlineBuilder();
		root.Render(builder);
		return builder.ToString();
	}
}
