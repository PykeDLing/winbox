using System.Security.Cryptography;
using WinBox.Core.Boxes;

namespace WinBox.Core.Import;

public enum ImportStage
{
    Inspecting,
    Downloading,
    Extracting,
    CheckingCompatibility,
    Finishing,
}

public sealed record ImportProgress(ImportStage Stage, DownloadProgress? Download = null);

public enum ImportFailure
{
    UnsupportedFormat,
    StubWithoutRecipe,
    UntrustedPayload,
    NoEntryFound,
}

/// <summary>An import failure the UI can explain. <see cref="Detail"/> carries data, not prose.</summary>
public sealed class ImportException(ImportFailure failure, string? detail = null, Exception? inner = null)
    : Exception($"{failure}{(detail is null ? "" : ": " + detail)}", inner)
{
    public ImportFailure Failure { get; } = failure;
    public string? Detail { get; } = detail;
}

/// <summary>
/// Turns a dropped installer into a box: inspect, resolve stubs through recipes, unpack the payload
/// without running it, pick the entry executable, and commit the box atomically.
/// </summary>
public sealed class ImportPipeline(
    AppPaths appPaths,
    BoxStore store,
    IFileInspector inspector,
    IEnumerable<IPackageExtractor> extractors,
    IDownloader downloader,
    IEnumerable<IRecipe> recipes,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public InstallerFacts Inspect(string path)
    {
        var info = new FileInfo(path);
        var kind = InstallerDetector.Detect(path);
        var isExecutable = kind is InstallerKind.InnoSetup or InstallerKind.Nsis or InstallerKind.PortableExe;
        return new InstallerFacts
        {
            Path = info.FullName,
            Size = info.Length,
            Kind = kind,
            Executable = isExecutable ? inspector.ReadExecutableInfo(path) : null,
            Signature = isExecutable || kind is InstallerKind.Msi ? inspector.ReadSignature(path) : null,
        };
    }

    public async Task<LoadedBox> ImportAsync(string droppedFile, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new ImportProgress(ImportStage.Inspecting));
        var dropped = Inspect(droppedFile);
        var recipe = recipes.FirstOrDefault(r => r.Matches(dropped));

        var payload = dropped;
        var payloadSource = recipe?.ResolvePayload(dropped);
        if (payloadSource is not null)
        {
            progress?.Report(new ImportProgress(ImportStage.Downloading));
            var downloadProgress = progress is null ? null : new Progress<DownloadProgress>(d => progress.Report(new ImportProgress(ImportStage.Downloading, d)));
            var downloaded = await downloader.DownloadAsync(payloadSource.Url, appPaths.Downloads, downloadProgress, cancellationToken).ConfigureAwait(false);
            payload = Inspect(downloaded);
            if (payload.Signature is not { IsValid: true } || payload.Signature.CommonName != recipe!.ExpectedSigner)
                throw new ImportException(ImportFailure.UntrustedPayload, payload.Signature?.Subject ?? "unsigned");
            if (payload.Kind != payloadSource.ExpectedKind)
                throw new ImportException(ImportFailure.UnsupportedFormat, payload.Kind.ToString());
        }

        var extractor = extractors.FirstOrDefault(e => e.CanExtract(payload.Kind))
            ?? throw new ImportException(
                payload.Kind == InstallerKind.PortableExe && recipe is null ? ImportFailure.StubWithoutRecipe : ImportFailure.UnsupportedFormat,
                payload.Kind.ToString());

        var id = Guid.NewGuid();
        using var staging = store.BeginImport(id);
        var paths = staging.Paths;
        File.Copy(dropped.Path, Path.Combine(paths.Source, dropped.FileName));

        progress?.Report(new ImportProgress(ImportStage.Extracting));
        var extractRoot = Path.Combine(paths.Root, "_extract");
        var extraction = await extractor.ExtractAsync(payload.Path, payload.Kind, extractRoot, cancellationToken).ConfigureAwait(false);
        Directory.Delete(paths.App);
        Directory.Move(extraction.AppDirectory, paths.App);
        Directory.Delete(extractRoot, recursive: true);

        progress?.Report(new ImportProgress(ImportStage.CheckingCompatibility));
        RestorePlaceholderNames(paths.App, recipe?.PlaceholderFileNames ?? new Dictionary<string, string>());
        var candidates = FindExecutables(paths.App);
        var entry = EntryResolver.Resolve(candidates, recipe?.EntryFileName)
            ?? throw new ImportException(ImportFailure.NoEntryFound);

        var issues = new List<CompatibilityIssue>();
        if (payload.Kind is InstallerKind.InnoSetup or InstallerKind.Nsis or InstallerKind.Msi)
            issues.Add(new CompatibilityIssue(IssueCode.InstallerScriptsSkipped, IssueSeverity.Info));
        if (extraction.SkippedRoots.Count > 0)
            issues.Add(new CompatibilityIssue(IssueCode.FilesOutsideAppSkipped, IssueSeverity.Info, string.Join(";", extraction.SkippedRoots)));
        if (recipe is not null)
            issues.AddRange(recipe.KnownIssues.Where(k => !issues.Any(i => i.Code == k.Code)));

        progress?.Report(new ImportProgress(ImportStage.Finishing));
        var config = new BoxConfig
        {
            Id = id,
            Name = recipe?.DisplayName ?? entry.Info?.FileDescription ?? entry.Info?.ProductName ?? Path.GetFileNameWithoutExtension(entry.RelativePath),
            Publisher = entry.Info?.CompanyName ?? payload.Executable?.CompanyName,
            Version = entry.Info?.ProductVersion ?? payload.Executable?.ProductVersion,
            Entry = entry.RelativePath,
            LaunchArguments = recipe?.LaunchArguments ?? [],
            Source = new BoxSource
            {
                FileName = dropped.FileName,
                Kind = payloadSource is null ? dropped.Kind : InstallerKind.Stub,
                Sha256 = Sha256Of(payload.Path),
                RecipeId = recipe?.Id,
                DownloadUrl = payloadSource?.Url.ToString(),
            },
            Issues = issues,
            CreatedAt = _clock.GetUtcNow(),
        };
        return staging.Commit(config);
    }

    /// <summary>
    /// Inno Setup scripts can compute file names at install time ("code$GetAppExeDestName"). innoextract
    /// writes those literally; the real name is recoverable from the file's PE OriginalFilename, or else
    /// from the recipe.
    /// </summary>
    private void RestorePlaceholderNames(string appDirectory, IReadOnlyDictionary<string, string> recipeNames)
    {
        foreach (var file in Directory.EnumerateFiles(appDirectory, "code$*", SearchOption.AllDirectories).ToList())
        {
            var original = inspector.ReadExecutableInfo(file)?.OriginalFilename
                ?? recipeNames.GetValueOrDefault(Path.GetFileName(file));
            if (string.IsNullOrWhiteSpace(original) || original.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                continue;
            var target = Path.Combine(Path.GetDirectoryName(file)!, original);
            if (!File.Exists(target))
                File.Move(file, target);
        }
    }

    private List<ExecutableCandidate> FindExecutables(string appDirectory) =>
        Directory.EnumerateFiles(appDirectory, "*.exe", SearchOption.AllDirectories)
            .Select(f => new ExecutableCandidate(Path.GetRelativePath(appDirectory, f), new FileInfo(f).Length, inspector.ReadExecutableInfo(f)))
            .ToList();

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
