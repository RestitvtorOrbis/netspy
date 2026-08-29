using System.Security.Cryptography;
using System.Text.Json;

if (args.Length == 0 || args[0].Equals("--validate", StringComparison.Ordinal)) {
	if (args.Length < 3 || !args[0].Equals("--validate", StringComparison.Ordinal))
		throw new ArgumentException("Usage: --validate <source-root> <output-root>");
	ValidateOutputRoot(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
	return;
}

var values = ParseArguments(args);
string variantRoot = GetRequired(values, "variant-root");
string publishRoot = GetRequired(values, "publish-root");
string variant = GetRequired(values, "variant");
string sdkVersion = GetRequired(values, "sdk-version");
string targetFramework = GetRequired(values, "target-framework");
string runtimeIdentifier = GetRequired(values, "runtime-identifier");
bool selfContained = GetBoolean(values, "self-contained");
bool compressed = GetBoolean(values, "compressed");
bool includesSymbols = GetBoolean(values, "includes-symbols");

if (!Directory.Exists(variantRoot) || !Directory.Exists(publishRoot))
	throw new DirectoryNotFoundException("Fixture output directory is missing.");
string bundle = GetSingleFile(publishRoot, ".exe");
string buildRoot = Path.Combine(variantRoot, "build");
string buildMain = GetSingleFile(buildRoot, "SingleFile.App.dll");
// The project reference is copied into the app output as well as emitted by
// its own project. Use the dependency project's isolated output so the byte
// comparison is against the canonical build artifact, not a copy operation.
string buildDependency = GetSingleFile(Path.Combine(buildRoot, "SingleFile.Dependency"), "SingleFile.Dependency.dll");

var publishedFiles = new List<PublishedFile>();
foreach (string path in Directory.EnumerateFiles(publishRoot, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal)) {
	using FileStream stream = File.OpenRead(path);
	string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
	publishedFiles.Add(new PublishedFile {
		Path = RelativePath(variantRoot, path),
		Length = new FileInfo(path).Length,
		Sha256 = hash,
	});
}

var record = new FixtureRecord {
	SchemaVersion = 1,
	SdkVersion = sdkVersion,
	TargetFramework = targetFramework,
	RuntimeIdentifier = runtimeIdentifier,
	Variant = variant,
	SelfContained = selfContained,
	Compressed = compressed,
	IncludesSymbols = includesSymbols,
	Bundle = RelativePath(variantRoot, bundle),
	BuildMainAssembly = RelativePath(variantRoot, buildMain),
	BuildDependencyAssembly = RelativePath(variantRoot, buildDependency),
	PublishedFiles = publishedFiles,
};

var options = new JsonSerializerOptions {
	WriteIndented = true,
	PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
};
File.WriteAllText(Path.Combine(variantRoot, "fixture.json"), JsonSerializer.Serialize(record, options));

static Dictionary<string, string> ParseArguments(string[] args) {
	var result = new Dictionary<string, string>(StringComparer.Ordinal);
	for (int i = 0; i < args.Length; i++) {
		if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
			throw new ArgumentException("Fixture metadata arguments must be --name value pairs.");
		result[args[i][2..]] = args[++i];
	}
	return result;
}

static string GetRequired(IReadOnlyDictionary<string, string> values, string key) =>
	values.TryGetValue(key, out string? value) && !String.IsNullOrWhiteSpace(value)
		? value
		: throw new ArgumentException("Missing fixture metadata argument --" + key);

static bool GetBoolean(IReadOnlyDictionary<string, string> values, string key) =>
	bool.TryParse(GetRequired(values, key), out bool value)
		? value
		: throw new ArgumentException("Invalid boolean fixture metadata argument --" + key);

static string GetSingleFile(string root, string suffix) {
	if (!Directory.Exists(root))
		throw new DirectoryNotFoundException("Required fixture build directory is missing: " + root);
	string[] matches = Directory.EnumerateFiles(root, "*" + suffix, SearchOption.AllDirectories)
		.OrderBy(path => path, StringComparer.Ordinal).ToArray();
	return matches.Length == 1
		? matches[0]
		: throw new InvalidDataException("Expected exactly one " + suffix + " under " + root + ".");
}

static string RelativePath(string root, string path) =>
	Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

static void ValidateOutputRoot(string sourceRoot, string outputRoot) {
	string source = CanonicalExisting(sourceRoot);
	if (!IsWithin(source, sourceRoot))
		throw new InvalidOperationException("The fixture source root resolves outside its repository subtree.");
	string expected = Path.Combine(source, "artifacts", "net10.0");
	if (Directory.Exists(Path.Combine(source, "artifacts")))
		expected = Path.Combine(CanonicalExisting(Path.Combine(source, "artifacts")), "net10.0");
	if (Directory.Exists(expected))
		expected = CanonicalExisting(expected);
	string expectedText = Path.Combine(source, "artifacts", "net10.0");
	if (!IsWithin(source, expected) || !StringComparer.OrdinalIgnoreCase.Equals(Normalize(expectedText), Normalize(expected)))
		throw new InvalidOperationException("The fixture artifact root must remain below Tests/TestAssets/SingleFile/Net10/artifacts/net10.0.");
	if (!StringComparer.OrdinalIgnoreCase.Equals(Normalize(expectedText), Normalize(outputRoot)))
		throw new InvalidOperationException("Refusing to clean outside the dedicated net10 fixture artifact root: " + outputRoot);
	if (Directory.Exists(Path.Combine(source, "artifacts")))
		_ = CanonicalExisting(Path.Combine(source, "artifacts"));
	if (Directory.Exists(outputRoot))
		_ = CanonicalExisting(outputRoot);
}

static string CanonicalExisting(string path) {
	if (!Directory.Exists(path))
		throw new DirectoryNotFoundException("Required fixture directory is missing: " + path);
	DirectoryInfo info = new(path);
	for (DirectoryInfo? current = info; current is not null; current = current.Parent) {
		if (current.Attributes.HasFlag(FileAttributes.ReparsePoint) || current.LinkTarget is not null)
			throw new InvalidOperationException("Fixture paths cannot be symbolic links or junctions: " + current.FullName);
	}
	return info.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

static bool IsWithin(string root, string path) {
	string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
	return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
		StringComparer.OrdinalIgnoreCase.Equals(root, path);
}

static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

sealed class FixtureRecord {
	public int SchemaVersion { get; set; }
	public string SdkVersion { get; set; } = null!;
	public string TargetFramework { get; set; } = null!;
	public string RuntimeIdentifier { get; set; } = null!;
	public string Variant { get; set; } = null!;
	public bool SelfContained { get; set; }
	public bool Compressed { get; set; }
	public bool IncludesSymbols { get; set; }
	public string Bundle { get; set; } = null!;
	public string BuildMainAssembly { get; set; } = null!;
	public string BuildDependencyAssembly { get; set; } = null!;
	public List<PublishedFile> PublishedFiles { get; set; } = new();
}

sealed class PublishedFile {
	public string Path { get; set; } = null!;
	public long Length { get; set; }
	public string Sha256 { get; set; } = null!;
}
