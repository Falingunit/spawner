namespace Spawner;

internal static class FileSystemPath
{
	public static StringComparison Comparison =>
		OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

	public static StringComparer Comparer =>
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	public static bool Equals(string? left, string? right) =>
		string.Equals(Normalize(left), Normalize(right), Comparison);

	public static bool IsSameOrDescendant(string rootFull, string candidateFull)
	{
		var normalizedRoot = Normalize(rootFull);
		var normalizedCandidate = Normalize(candidateFull);
		if (normalizedRoot.Length == 0 || normalizedCandidate.Length == 0) return false;

		var rootWithSep = normalizedRoot + Path.DirectorySeparatorChar;
		return normalizedCandidate.StartsWith(rootWithSep, Comparison) ||
			   string.Equals(normalizedCandidate, normalizedRoot, Comparison);
	}

	public static bool LooksRootedOrDriveQualified(string path)
	{
		if (Path.IsPathRooted(path)) return true;

		return path.Length >= 2 &&
			   char.IsLetter(path[0]) &&
			   path[1] == ':';
	}

	private static string Normalize(string? path) =>
		(path ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
