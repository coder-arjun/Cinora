namespace Cinora.Application.Tests;

public class SmokeTests
{
    [Fact]
    public void Application_test_project_builds_and_runs()
    {
        var sum = 2 + 2;

        Assert.Equal(4, sum);
    }
}
