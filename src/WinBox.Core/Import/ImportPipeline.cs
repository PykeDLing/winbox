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

    /// <summary>The box exists; its package is being registered (see <see cref="Boxes.BoxManager"/>).</summary>
    Registering,
}

public sealed record ImportProgress(ImportStage Stage, DownloadProgress? Download = null);

public enum ImportFailure
{
    UnsupportedFormat,
    StubWithoutRecipe,
    UntrustedPayload,
    NoEntryFound,

    /// <summary>An MSIX or APPX package: Windows installs and isolates those itself.</summary>
    PackagedApp,

    /// <summary>The installer could not be unpacked (damaged, encrypted, or a variant the tools do not know).</summary>
    ExtractionFailed,
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

        if (payload.Kind == InstallerKind.Msix)
            throw new ImportException(ImportFailure.PackagedApp);
        var (extractor, kind) = extractors
            .Select(e => (Extractor: e, Kind: e.Recognize(payload)))
            .FirstOrDefault(x => x.Kind is not null);
        if (extractor is null || kind is not { } payloadKind)
            throw new ImportException(
                payload.Kind == InstallerKind.PortableExe && recipe is null ? ImportFailure.StubWithoutRecipe : ImportFailure.UnsupportedFormat,
                payload.Kind.ToString());

        var id = Guid.NewGuid();
        using var staging = store.BeginImport(id);
        var paths = staging.Paths;
        File.Copy(dropped.Path, Path.Combine(paths.Source, dropped.FileName));

        progress?.Report(new ImportProgress(ImportStage.Extracting));
        var extractRoot = Path.Combine(paths.Root, "_extract");
        var extraction = await extractor.ExtractAsync(payload.Path, payloadKind, extractRoot, cancellationToken).ConfigureAwait(false);
        Directory.Delete(paths.App);
        Directory.Move(extraction.AppDirectory, paths.App);
        Directory.Delete(extractRoot, recursive: true);

        progress?.Report(new ImportProgress(ImportStage.CheckingCompatibility));
        RestorePlaceholderNames(paths.App, recipe?.PlaceholderFileNames ?? new Dictionary<string, string>());
        var candidates = FindExecutables(paths.App);
        IReadOnlyList<string> names = [.. recipe is null ? [] : new[] { recipe.DisplayName }, .. EntryResolver.NamesOf(payload), .. EntryResolver.NamesOf(dropped)];
        var entry = EntryResolver.Resolve(candidates, recipe?.EntryFileName, extraction.EntryHints, names)
            ?? throw new ImportException(ImportFailure.NoEntryFound);
        var arguments = recipe?.LaunchArguments ?? (entry.Chromium ? ChromiumApp.LaunchArguments : []);

        var issues = new List<CompatibilityIssue>();
        var installer = payloadKind is InstallerKind.InnoSetup or InstallerKind.Nsis or InstallerKind.Msi or InstallerKind.Squirrel
            || (payloadKind == InstallerKind.SevenZip && payload.Executable is not null);
        if (installer)
            issues.Add(new CompatibilityIssue(IssueCode.InstallerScriptsSkipped, IssueSeverity.Info));
        if (extraction.SkippedRoots.Count > 0)
            issues.Add(new CompatibilityIssue(IssueCode.FilesOutsideAppSkipped, IssueSeverity.Info, string.Join(";", extraction.SkippedRoots)));
        if (payloadKind == InstallerKind.Squirrel)
            issues.Add(new CompatibilityIssue(IssueCode.SelfUpdaterDisabled, IssueSeverity.Info, "Squirrel"));
        if (recipe is not null)
            issues.AddRange(recipe.KnownIssues.Where(k => !issues.Any(i => i.Code == k.Code)));
        if (arguments.Contains(ChromiumApp.LaunchArguments[0]))
            issues.Add(new CompatibilityIssue(IssueCode.InnerSandboxDisabled, IssueSeverity.Info));

        progress?.Report(new ImportProgress(ImportStage.Finishing));
        var config = new BoxConfig
        {
            Id = id,
            Name = recipe?.DisplayName ?? NonBlank(entry.Info?.FileDescription) ?? NonBlank(entry.Info?.ProductName)
                ?? names.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(entry.RelativePath),
            Publisher = NonBlank(entry.Info?.CompanyName) ?? NonBlank(payload.Executable?.CompanyName),
            Version = NonBlank(entry.Info?.ProductVersion) ?? NonBlank(payload.Executable?.ProductVersion),
            Entry = entry.RelativePath,
            LaunchArguments = arguments,
            Source = new BoxSource
            {
                FileName = dropped.FileName,
                Kind = payloadSource is null ? payloadKind : InstallerKind.Stub,
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
    /// Inno Setup scripts can compute file names at install time ("{code_GetAppExeDestName}"), which the unpacker
    /// writes literally. Names the installer's own shortcuts give are restored while unpacking; for the rest, the
    /// real name comes from the recipe, or else from the file's PE OriginalFilename.
    /// </summary>
    private void RestorePlaceholderNames(string appDirectory, IReadOnlyDictionary<string, string> recipeNames)
    {
        var placeholders = Directory.EnumerateFiles(appDirectory, "*", SearchOption.AllDirectories)
            .Where(f => InstallerLayout.PlaceholderName(Path.GetFileName(f)) is not null)
            .ToList();
        foreach (var file in placeholders)
        {
            var function = InstallerLayout.PlaceholderName(Path.GetFileName(file))!;
            var original = recipeNames.GetValueOrDefault(function) ?? inspector.ReadExecutableInfo(file)?.OriginalFilename;
            if (string.IsNullOrWhiteSpace(original) || original.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                continue;
            var target = Path.Combine(Path.GetDirectoryName(file)!, original);
            if (!File.Exists(target))
                File.Move(file, target);
        }
    }

    private List<ExecutableCandidate> FindExecutables(string appDirectory) =>
        Directory.EnumerateFiles(appDirectory, "*.exe", SearchOption.AllDirectories)
            .Select(f => new ExecutableCandidate(
                Path.GetRelativePath(appDirectory, f),
                new FileInfo(f).Length,
                inspector.ReadExecutableInfo(f),
                PeFile.IsGui(f),
                ChromiumApp.IsMainProgram(f)))
            .ToList();

    private static string? NonBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
