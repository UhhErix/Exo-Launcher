using System.Reflection;
using System.Runtime.CompilerServices;
using ExoLauncher.Services.DiscordClient;

namespace ExoLauncher.Tests;

/// <summary>
/// A call that ends because its session was torn down (a lost connection) has no call event of its own. The shell's sleep
/// rule must still hear it, or a hidden Exo stays awake until the next window change. Ways this could fail: the session is
/// dropped without anyone being told.
/// </summary>
public sealed class DiscordSessionDropTests
{
    private static readonly FieldInfo Current = typeof(DiscordSession).GetField("_current", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void DroppingTheCurrentSessionTellsTheShellOnceAndAnotherSessionIsLeftAlone()
    {
        var mine = (DiscordSession)RuntimeHelpers.GetUninitializedObject(typeof(DiscordSession));
        var other = (DiscordSession)RuntimeHelpers.GetUninitializedObject(typeof(DiscordSession));
        var told = 0;
        Action heard = () => Interlocked.Increment(ref told);
        DiscordSession.CallStateChanged += heard;
        var before = Current.GetValue(null);
        try
        {
            Current.SetValue(null, mine);
            Assert.False(DiscordSession.Drop(other));
            Assert.Equal(0, told);
            Assert.Same(mine, DiscordSession.Current);
            Assert.True(DiscordSession.Drop(mine));
            Assert.Equal(1, told);
            Assert.Null(DiscordSession.Current);
            Assert.False(DiscordSession.Drop(mine));
            Assert.Equal(1, told);
        }
        finally
        {
            DiscordSession.CallStateChanged -= heard;
            Current.SetValue(null, before);
        }
    }
}
