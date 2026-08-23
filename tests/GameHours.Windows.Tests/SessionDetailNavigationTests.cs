using System.Windows;
using System.Windows.Documents;
using GameHours.Desktop;

namespace GameHours.Windows.Tests;

public sealed class SessionDetailNavigationTests
{
    [Fact]
    public void GetParent_UnattachedContentElement_DoesNotUseVisualTreeTraversal()
    {
        var source = new Run("session");

        var parent = SessionDetailNavigation.GetParent(source);

        Assert.Null(parent);
    }

    [Fact]
    public void GetParent_PlainDependencyObject_ReturnsNoParentWithoutThrowing()
    {
        var source = new DependencyObject();

        var parent = SessionDetailNavigation.GetParent(source);

        Assert.Null(parent);
    }
}
