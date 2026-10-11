using Kubism.Desktop.Navigation;
using Kubism.Desktop.ViewModels;
using Xunit;

namespace Kubism.Desktop.Tests;

public class NavigationHistoryTests
{
    private static NavLocation Loc(string name, string[]? ns = null, ObjectRef? obj = null) => new(NavTarget.Kind, name, ns ?? [], obj, name, name);

    private static (NavigationHistory h, Func<string?> leaving) Make()
    {
        string? page = "state-of-first";
        string[] ns = ["a"];
        var h = new NavigationHistory(() => new LeaveSnapshot(page, ns));
        return (h, () => page);
    }

    [Fact]
    public void Back_and_forward_walk_the_entries()
    {
        var (h, _) = Make();
        h.Record(Loc("pods")); h.Record(Loc("secrets")); h.Record(Loc("nodes"));
        Assert.False(h.CanForward);
        Assert.Equal("secrets", h.Back()!.Location.Title);
        Assert.Equal("pods", h.Back()!.Location.Title);
        Assert.Null(h.Back());
        Assert.Equal("secrets", h.Forward()!.Location.Title);
        Assert.True(h.CanForward);
    }

    [Fact]
    public void A_new_place_drops_what_was_ahead_and_the_same_place_is_not_an_entry()
    {
        var (h, _) = Make();
        h.Record(Loc("pods")); h.Record(Loc("secrets")); h.Record(Loc("nodes"));
        h.Back(); h.Back();
        Assert.False(h.Record(Loc("pods")));   // already there
        Assert.True(h.CanForward);
        Assert.True(h.Record(Loc("jobs")));
        Assert.False(h.CanForward);
        Assert.Equal(["pods", "jobs"], h.Entries.Select(e => e.Location.Title));
    }

    [Fact]
    public void Same_kind_with_another_object_is_another_entry()
    {
        var (h, _) = Make();
        h.Record(Loc("pods"));
        Assert.True(h.Record(Loc("pods", obj: new ObjectRef("pods", "d", "web"))));
        Assert.True(h.Record(Loc("pods", obj: new ObjectRef("pods", "d", "api"))));
        Assert.Equal(3, h.Entries.Count);
    }

    [Fact]
    public void The_page_is_captured_when_left_not_when_arrived()
    {
        var states = new Queue<string>(["first-page", "second-page"]);
        var h = new NavigationHistory(() => new LeaveSnapshot(states.Dequeue(), ["x"]));
        h.Record(Loc("pods"));
        h.Record(Loc("secrets"));            // leaves pods
        Assert.Equal("first-page", h.Entries[0].State);
        h.Back();                             // leaves secrets
        Assert.Equal("second-page", h.Entries[1].State);
        Assert.Equal(["x"], h.Entries[0].Location.Namespaces);
    }

    [Fact]
    public void Begin_navigation_captures_once_before_the_namespaces_change()
    {
        var calls = 0; string[] ns = ["before"];
        var h = new NavigationHistory(() => { calls++; return new LeaveSnapshot(null, ns); });
        h.Record(Loc("pods"));
        h.BeginNavigation();
        ns = ["after"];
        h.Record(Loc("secrets", ns));
        h.EndNavigation();
        Assert.Equal(1, calls);
        Assert.Equal(["before"], h.Entries[0].Location.Namespaces);
    }

    [Fact]
    public void History_is_capped()
    {
        var (h, _) = Make();
        for (var i = 0; i < NavigationHistory.Max + 20; i++) h.Record(Loc("k" + i));
        Assert.Equal(NavigationHistory.Max, h.Entries.Count);
        Assert.Equal("k" + (NavigationHistory.Max + 19), h.Current!.Location.Title);
    }
}
