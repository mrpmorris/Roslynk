namespace Morris.Roslynk.Infrastructure.Outlines;

/// <summary>
/// Renders a set of file-bearing items into a folder -> file body, collapsing files that share a folder under
/// a single folder line so the folder is not repeated on every sibling. Used by the body writers that group
/// with explicit <see cref="OutlineBuilder.Line"/> calls rather than a <see cref="SymbolNode"/> tree
/// (get_members, get_diagnostics and the changed-file list); the tree-based writers get the same shape from
/// <see cref="SymbolNode.ChildPath"/>.
/// </summary>
internal static class FolderFiles
{
	/// <summary>
	/// Groups <paramref name="items"/> by the folder then the file name of <paramref name="pathOf"/>, writing
	/// the folder line (when the path has one) at <paramref name="baseDepth"/> and each file name beneath it,
	/// then invoking <paramref name="writeBody"/> with the depth one level below the file line and that file's
	/// items, so the caller can render the file's own leaves. A file with no folder part is written directly at
	/// <paramref name="baseDepth"/>.
	/// </summary>
	public static void Write<T>(
		OutlineBuilder builder,
		int baseDepth,
		IEnumerable<T> items,
		Func<T, string> pathOf,
		Action<int, IEnumerable<T>> writeBody)
	{
		IEnumerable<IGrouping<string?, T>> byFolder = items
			.GroupBy(item => OutlinePath.Split(pathOf(item)).Folder)
			.OrderBy(group => group.Key, StringComparer.Ordinal);

		foreach (IGrouping<string?, T> folder in byFolder)
		{
			int fileDepth = baseDepth;
			if (folder.Key is string folderPath)
			{
				builder.Line(baseDepth, folderPath);
				fileDepth = baseDepth + 1;
			}

			IEnumerable<IGrouping<string, T>> byFile = folder
				.GroupBy(item => OutlinePath.Split(pathOf(item)).Name)
				.OrderBy(group => group.Key, StringComparer.Ordinal);

			foreach (IGrouping<string, T> file in byFile)
			{
				builder.Line(fileDepth, file.Key);
				writeBody(fileDepth + 1, file);
			}
		}
	}
}