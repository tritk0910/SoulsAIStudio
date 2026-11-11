namespace SoulsAiStudio.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void CoreAssemblyLoads() => Assert.NotNull(typeof(SoulsAiStudio.Core.AssemblyMarker).Assembly);
}
