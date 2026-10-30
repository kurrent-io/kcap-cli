using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class LocalCheckoutFinderTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string Checkout(string name) {
        var dir = Tmp.CreateDir(name);
        dir.CreateDir(".git");
        return dir;
    }

    static LocalCheckoutFinder Finder(Dictionary<string, string> origins) =>
        new(new RepoIdentityResolver(root => origins.GetValueOrDefault(root)));

    [Test]
    public async Task Prefers_the_sessions_own_checkout() {
        var own = Checkout("own");
        var other = Checkout("other");
        var finder = Finder(new() {
            [own] = "https://github.com/acme/widgets.git",
            [other] = "git@github.com:acme/widgets.git",
        });

        var found = await finder.FindAsync("acme", "widgets", Path.Combine(own, "src"), [other], CancellationToken.None);

        await Assert.That(found).IsEqualTo(own);
    }

    [Test]
    public async Task Skips_a_checkout_of_another_repository() {
        var unrelated = Checkout("unrelated");
        var widgets = Checkout("widgets");
        var finder = Finder(new() {
            [unrelated] = "https://github.com/acme/gadgets.git",
            [widgets] = "https://github.com/Acme/Widgets.git",
        });

        var found = await finder.FindAsync("acme", "widgets", Tmp.PathTo("gone"), [unrelated, widgets], CancellationToken.None);

        await Assert.That(found).IsEqualTo(widgets);
    }
}
