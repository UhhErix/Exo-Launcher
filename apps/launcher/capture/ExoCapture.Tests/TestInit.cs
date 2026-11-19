using System.Runtime.CompilerServices;
using Vortice.MediaFoundation;

namespace ExoCapture.Tests;

internal static class TestInit
{
    [ModuleInitializer]
    internal static void Initialize() => MediaFactory.MFStartup(true).CheckError();
}
