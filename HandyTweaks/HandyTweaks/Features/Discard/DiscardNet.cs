using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace HandyTweaks.Features.Discard
{
    // Simple toggle message
    public class DiscardToggleMsg
    {
        public bool Enabled;
    }

    public static class DiscardNet
    {
        public const string Channel = "handytweaks:discard";

        // Works for both client and server channels
        public static void RegisterTypes(INetworkChannel ch)
        {
            ch.RegisterMessageType<DiscardToggleMsg>();
        }
    }
}
