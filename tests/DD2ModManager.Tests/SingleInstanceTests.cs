using DD2ModManager.Services;

namespace DD2ModManager.Tests;

public class SingleInstanceTests
{
    // Synchronous on purpose: the named mutex must be released on the thread that acquired it.
#pragma warning disable xUnit1031
    [Fact]
    public void SecondLaunchForwardsItsLinkToTheFirst()
    {
        var received = new TaskCompletionSource<string>();
        using var first = new SingleInstance();
        Assert.True(first.IsFirst);
        first.Listen(m => received.TrySetResult(m));

        var secondWasFirst = true;
        var thread = new Thread(() =>
        {
            using var second = new SingleInstance();
            secondWasFirst = second.IsFirst;
            SingleInstance.Send("nxm://dragonsdogma2/mods/1196/files/9?key=k&expires=1");
        });
        thread.Start();
        thread.Join();

        Assert.False(secondWasFirst);
        Assert.True(received.Task.Wait(5000));
        Assert.Equal("nxm://dragonsdogma2/mods/1196/files/9?key=k&expires=1", received.Task.Result);
    }
#pragma warning restore xUnit1031
}
