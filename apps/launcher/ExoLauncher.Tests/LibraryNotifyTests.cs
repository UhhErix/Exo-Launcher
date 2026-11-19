using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a library update could go unheard: one listener that throws stops the ones after it (a multicast delegate
/// does), or hides the failure from the log so nobody learns the shell stopped refreshing.
/// </summary>
public sealed class LibraryNotifyTests
{
    [Fact]
    public void AListenerThatThrowsDoesNotStopTheOnesAfterIt()
    {
        var heard = new List<string>();
        Action? listeners = () => heard.Add("first");
        listeners += () => throw new InvalidOperationException("broken");
        listeners += () => heard.Add("third");

        LibraryService.NotifyEach(listeners);

        Assert.Equal(["first", "third"], heard);
    }

    [Fact]
    public void NoListenersIsNotAnError() => LibraryService.NotifyEach(null);
}
