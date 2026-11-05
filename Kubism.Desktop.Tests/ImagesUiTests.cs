using Avalonia.Headless.XUnit;
using Kubism.Core.Images;
using Kubism.Desktop.ViewModels;
using Xunit;

namespace Kubism.Desktop.Tests;

public class ImagesUiTests
{
    private const string Seed = """
        {"apiVersion":"v1","kind":"Pod","metadata":{"name":"old-1","namespace":"img-demo","uid":"i1","ownerReferences":[{"apiVersion":"apps/v1","kind":"ReplicaSet","name":"shop-6d4cf56db6","uid":"r","controller":true}]},"spec":{"containers":[{"name":"c","image":"registry.example.com/team/shop:1.0","imagePullPolicy":"IfNotPresent"}]},"status":{"containerStatuses":[{"name":"c","state":{"running":{}},"imageID":"docker-pullable://registry.example.com/team/shop@sha256:aaaaaaaaaaaaaaaaaaaa"}]}}
        ---
        {"apiVersion":"v1","kind":"Pod","metadata":{"name":"new-1","namespace":"img-demo","uid":"i2","ownerReferences":[{"apiVersion":"apps/v1","kind":"ReplicaSet","name":"shop-7f9d8c4b5","uid":"r2","controller":true}]},"spec":{"containers":[{"name":"c","image":"registry.example.com/team/shop:1.1"}]},"status":{"containerStatuses":[{"name":"c","state":{"running":{}}}]}}
        ---
        {"apiVersion":"v1","kind":"Pod","metadata":{"name":"lat-1","namespace":"img-demo","uid":"i3"},"spec":{"containers":[{"name":"c","image":"redis"}]},"status":{"containerStatuses":[{"name":"c","state":{"running":{}}}]}}
        ---
        {"apiVersion":"v1","kind":"Pod","metadata":{"name":"bad-1","namespace":"img-demo","uid":"i4"},"spec":{"containers":[{"name":"c","image":"registry.example.com/team/missing:9"}]},"status":{"containerStatuses":[{"name":"c","state":{"waiting":{"reason":"ImagePullBackOff"}}}]}}
        """;

    [AvaloniaFact]
    public async Task Images_page_lists_images_with_what_needs_a_look_and_who_uses_them()
    {
        await using var h = await UiHarness.StartAsync();
        h.Demo.Server.SeedYaml(Seed);
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.SelectAll();

        var node = ws.FindNav(NavTarget.Images);
        Assert.NotNull(node);
        ws.Navigate(node!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ImagesViewModel v && v.Rows.Any(r => r.Registry == "registry.example.com"), what: "images");
        var vm = (ImagesViewModel)ws.CurrentContent!;

        // Same repository in two versions: both rows say so.
        var v10 = vm.Rows.Single(r => r.Reference == "registry.example.com/team/shop:1.0");
        Assert.True(v10.Row.Issues.HasFlag(ImageIssues.MixedVersions));
        Assert.Equal("shop", v10.Row.Users.Single().Name);
        Assert.Equal("sha256:aaaaaaaaaaaa", v10.Digest);
        Assert.Equal(Kubism.Desktop.ViewModels.Severity.Error, vm.Rows.Single(r => r.Reference.EndsWith("missing:9")).Severity);
        Assert.Contains(vm.Rows, r => r.Reference == "redis" && r.Row.Issues.HasFlag(ImageIssues.Latest));
        h.Screenshot("60-images");

        // Selecting a row lists its users.
        vm.Selected = v10;
        Assert.Equal("Deployment/img-demo/shop", Assert.Single(vm.Users).Workload);

        // Filters.
        vm.Show = ImageShow.PullFailing;
        Assert.All(vm.Rows, r => Assert.True(r.Row.Issues.HasFlag(ImageIssues.PullFailing)));
        Assert.Single(vm.Rows);
        vm.Show = ImageShow.All;

        // Repositories group the versions.
        vm.Section = 1;
        var shop = Assert.Single(vm.Groups, g => g.Name == "registry.example.com/team/shop");
        Assert.Equal(("1.0, 1.1", "Several versions"), (shop.Versions, shop.Note));
        h.Screenshot("61-images-repositories");

        // From a repository back to its images.
        vm.SelectedGroup = shop;
        vm.ShowImagesOfGroupCommand.Execute(null);
        Assert.Equal(0, vm.Section);
        Assert.Equal(2, vm.Rows.Count);

        // Leaving and coming back restores the page.
        vm.Selected = vm.Rows[0];
        var key = vm.Selected.Key;
        ws.OpenKind(ws.Session.Discovery.FindByName("pods")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel, what: "pods");
        ws.GoBack();
        await UiHarness.WaitFor(() => ws.CurrentContent is ImagesViewModel v2 && !ReferenceEquals(v2, vm) && v2.Rows.Count == 2, what: "images again");
        var back = (ImagesViewModel)ws.CurrentContent!;
        Assert.Equal(key, back.Selected?.Key);
        Assert.Equal("registry.example.com/team/shop", back.Filter);
    }
}
