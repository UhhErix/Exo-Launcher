namespace ExoLauncher.Helpers;

/// <summary>
/// A failure whose message Exo itself wrote for the player (one plain sentence). Only this type's message reaches the
/// page as it is (WebHostBridge.UserMessage); every other exception, framework ones included, says something generic.

/// </summary>
internal sealed class UserFacingException(string message) : Exception(message);
