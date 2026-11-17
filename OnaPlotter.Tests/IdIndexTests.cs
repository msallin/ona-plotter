using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class IdIndexTests
{
    private sealed record Item(string Id, int V = 0);

    private static string IdOf(Item i) => i.Id;

    [Test]
    public async Task Build_MapsIdsToPositions()
    {
        var list = new List<Item> { new("a"), new("b"), new("c") };

        var index = IdIndex.Build(list, IdOf);

        await Assert.That(index["a"]).IsEqualTo(0);
        await Assert.That(index["c"]).IsEqualTo(2);
    }

    [Test]
    public async Task IndexOf_UsesAValidHint()
    {
        var list = new List<Item> { new("a"), new("b") };
        var hint = new Dictionary<string, int> { ["b"] = 1 };

        await Assert.That(IdIndex.IndexOf(list, "b", IdOf, hint)).IsEqualTo(1);
    }

    [Test]
    public async Task IndexOf_StaleHint_FallsBackToScan()
    {
        var list = new List<Item> { new("a"), new("b") };
        var hint = new Dictionary<string, int> { ["b"] = 0 };

        await Assert.That(IdIndex.IndexOf(list, "b", IdOf, hint)).IsEqualTo(1);
    }

    [Test]
    public async Task IndexOf_OutOfRangeHint_FallsBackToScan()
    {
        var list = new List<Item> { new("a") };
        var hint = new Dictionary<string, int> { ["a"] = 7 };

        await Assert.That(IdIndex.IndexOf(list, "a", IdOf, hint)).IsEqualTo(0);
    }

    [Test]
    public async Task IndexOf_Missing_ReturnsMinusOne()
    {
        var list = new List<Item> { new("a") };

        await Assert.That(IdIndex.IndexOf(list, "x", IdOf, new Dictionary<string, int>())).IsEqualTo(-1);
    }

    [Test]
    public async Task Upsert_ReplacesExisting_AndReportsNotNew()
    {
        var list = new List<Item> { new("a", 1) };
        var hint = IdIndex.Build(list, IdOf);

        bool isNew = IdIndex.Upsert(list, "a", new Item("a", 2), IdOf, hint);

        await Assert.That(isNew).IsFalse();
        await Assert.That(list.Count).IsEqualTo(1);
        await Assert.That(list[0].V).IsEqualTo(2);
    }

    [Test]
    public async Task Upsert_AppendsNew_AndUpdatesHint()
    {
        var list = new List<Item> { new("a") };
        var hint = IdIndex.Build(list, IdOf);

        bool isNew = IdIndex.Upsert(list, "b", new Item("b"), IdOf, hint);

        await Assert.That(isNew).IsTrue();
        await Assert.That(list.Count).IsEqualTo(2);
        await Assert.That(hint["b"]).IsEqualTo(1);
    }

    [Test]
    public async Task Upsert_TwiceInOneBatch_DoesNotDuplicate()
    {
        var list = new List<Item>();
        var hint = new Dictionary<string, int>();

        IdIndex.Upsert(list, "a", new Item("a", 1), IdOf, hint);
        bool second = IdIndex.Upsert(list, "a", new Item("a", 2), IdOf, hint);

        await Assert.That(second).IsFalse();
        await Assert.That(list.Count).IsEqualTo(1);
        await Assert.That(list[0].V).IsEqualTo(2);
    }
}
