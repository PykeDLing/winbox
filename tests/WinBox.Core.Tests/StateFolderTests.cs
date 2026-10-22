using System.Runtime.Versioning;
using WinBox.Core.Sandbox;

namespace WinBox.Core.Tests;

[TestClass]
public sealed class StateFolderTests
{
    private string _root = null!;

    [TestInitialize]
    public void Init() => _root = Path.Combine(Path.GetTempPath(), "winbox-tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Dir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Put(string dir, string relative, string content)
    {
        var path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string? Read(string dir, string relative) =>
        File.Exists(Path.Combine(dir, relative)) ? File.ReadAllText(Path.Combine(dir, relative)) : null;

    [TestMethod]
    public void NothingToRestoreBeforeTheFirstSave()
    {
        var state = new StateFolder(Path.Combine(_root, "state"));
        Assert.IsNull(state.RestoreTo(Dir("local")));
    }

    [TestMethod]
    public void SavedStateComesBackInTheNextSandbox()
    {
        var state = new StateFolder(Path.Combine(_root, "state"));
        var run1 = Dir("run1");
        Put(run1, @"QuarkCloudDrive\User Data\persistence\1\download.db", "tasks");
        Put(run1, @"QuarkCloudDrive\account.json", "{}");

        var stats = state.Save(run1, []);
        var run2 = Dir("run2");
        state.RestoreTo(run2);

        Assert.AreEqual(2, stats.Files);
        Assert.AreEqual("tasks", Read(run2, @"QuarkCloudDrive\User Data\persistence\1\download.db"));
        Assert.AreEqual("{}", Read(run2, @"QuarkCloudDrive\account.json"));
    }

    [TestMethod]
    public void ExcludedTopLevelFoldersAreNotCarried()
    {
        var state = new StateFolder(Path.Combine(_root, "state"));
        var run = Dir("run");
        Put(run, @"Temp\scratch.tmp", "x");
        Put(run, @"App\Temp\kept.txt", "nested folders of the same name stay");

        state.Save(run, StateFolder.DefaultExclusions);
        var next = Dir("next-run");
        state.RestoreTo(next);

        Assert.IsNull(Read(next, @"Temp\scratch.tmp"));
        Assert.IsNotNull(Read(next, @"App\Temp\kept.txt"));
    }

    [TestMethod]
    public void ANewSaveReplacesFilesTheAppDeleted()
    {
        var state = new StateFolder(Path.Combine(_root, "state"));
        var run1 = Dir("run1");
        Put(run1, "old.txt", "1");
        state.Save(run1, []);

        var run2 = Dir("run2");
        Put(run2, "new.txt", "2");
        state.Save(run2, []);
        var run3 = Dir("run3");
        state.RestoreTo(run3);

        Assert.IsNull(Read(run3, "old.txt"));
        Assert.AreEqual("2", Read(run3, "new.txt"));
    }

    [TestMethod]
    public void IncompleteSaveIsDiscarded()
    {
        var root = Path.Combine(_root, "state");
        var state = new StateFolder(root);
        var run = Dir("run");
        Put(run, "a.txt", "good");
        state.Save(run, []);
        // Power cut while copying: next\ exists but was never marked complete.
        Put(Path.Combine(root, "next"), "a.txt", "half");

        var restored = Dir("restored");
        state.RestoreTo(restored);

        Assert.AreEqual("good", Read(restored, "a.txt"));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "next")));
    }

    [TestMethod]
    public void CompleteSaveInterruptedMidSwapIsFinished()
    {
        var root = Path.Combine(_root, "state");
        var state = new StateFolder(root);
        // Power cut between the two renames: current\ already moved to previous\, next\ complete.
        Put(Path.Combine(root, "previous"), "a.txt", "old");
        Put(Path.Combine(root, "next"), "a.txt", "new");
        File.WriteAllText(Path.Combine(root, "next.complete"), "");

        var restored = Dir("restored");
        state.RestoreTo(restored);

        Assert.AreEqual("new", Read(restored, "a.txt"));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "previous")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "next.complete")));
    }

    [TestMethod]
    public void CompleteSaveNotYetSwappedIsSwappedIn()
    {
        var root = Path.Combine(_root, "state");
        var state = new StateFolder(root);
        Put(Path.Combine(root, "current"), "a.txt", "old");
        Put(Path.Combine(root, "next"), "a.txt", "new");
        File.WriteAllText(Path.Combine(root, "next.complete"), "");

        var restored = Dir("restored");
        state.RestoreTo(restored);

        Assert.AreEqual("new", Read(restored, "a.txt"));
    }

    [TestMethod]
    public void FailingToWriteTheNewCopyKeepsTheLastGoodOne()
    {
        var root = Path.Combine(_root, "state");
        var run1 = Dir("run1");
        Put(run1, "a.txt", "v1");
        Put(run1, "b.txt", "v1");
        new StateFolder(root).Save(run1, []);

        var run2 = Dir("run2");
        Put(run2, "a.txt", "v2");
        Put(run2, "b.txt", "v2");
        // The disk fills up while the new copy is written.
        var full = new StateFolder(root)
        {
            OpenDestination = path => Path.GetFileName(path) == "b.txt"
                ? throw new IOException("There is not enough space on the disk.")
                : new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None),
        };
        Assert.ThrowsExactly<IOException>(() => full.Save(run2, []));
        var run3 = Dir("run3");
        new StateFolder(root).RestoreTo(run3);

        Assert.AreEqual("v1", Read(run3, "a.txt"));
        Assert.AreEqual("v1", Read(run3, "b.txt"));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")] // Byte-range locks.
    public void FileThatFailsHalfwayIsNotSavedTruncated()
    {
        var state = new StateFolder(Path.Combine(_root, "state"));
        var run = Dir("run");
        var big = Path.Combine(run, "big.db");
        File.WriteAllBytes(big, new byte[200_000]);
        Put(run, "small.txt", "fine");
        CopyStats stats;
        using (var holder = new FileStream(big, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            // Another process holds a byte-range lock beyond the first chunk the copy reads.
            holder.Lock(150_000, 1_000);
            stats = state.Save(run, []);
        }
        var restored = Dir("restored");
        state.RestoreTo(restored);

        CollectionAssert.AreEqual(new[] { "big.db" }, stats.Skipped);
        Assert.IsFalse(File.Exists(Path.Combine(restored, "big.db")));
        Assert.AreEqual("fine", Read(restored, "small.txt"));
    }

    [TestMethod]
    public void FilesTheRestoreCouldNotReadSurviveTheNextSave()
    {
        var state = new StateFolder(Path.Combine(_root, "state"));
        var run1 = Dir("run1");
        Put(run1, "a.txt", "v1");
        Put(run1, "token.dat", "secret");
        state.Save(run1, []);

        var run2 = Dir("run2");
        CopyStats restored;
        using (new FileStream(Path.Combine(state.Current, "token.dat"), FileMode.Open, FileAccess.Read, FileShare.None))
            restored = state.RestoreTo(run2)!;
        Put(run2, "a.txt", "v2");
        state.Save(run2, [], restored.Skipped);
        var run3 = Dir("run3");
        state.RestoreTo(run3);

        CollectionAssert.AreEqual(new[] { "token.dat" }, restored.Skipped);
        Assert.AreEqual("v2", Read(run3, "a.txt"));
        Assert.AreEqual("secret", Read(run3, "token.dat"));
    }

    [TestMethod]
    public void LockedFileKeepsItsPreviousVersion()
    {
        var state = new StateFolder(Path.Combine(_root, "state"));
        var run1 = Dir("run1");
        Put(run1, "db.sqlite", "v1");
        state.Save(run1, []);

        var run2 = Dir("run2");
        Put(run2, "db.sqlite", "v2");
        Put(run2, "other.txt", "fine");
        CopyStats stats;
        using (new FileStream(Path.Combine(run2, "db.sqlite"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            stats = state.Save(run2, []);
        var run3 = Dir("run3");
        state.RestoreTo(run3);

        CollectionAssert.AreEqual(new[] { "db.sqlite" }, stats.KeptPrevious);
        Assert.AreEqual("v1", Read(run3, "db.sqlite"));
        Assert.AreEqual("fine", Read(run3, "other.txt"));
    }
}
