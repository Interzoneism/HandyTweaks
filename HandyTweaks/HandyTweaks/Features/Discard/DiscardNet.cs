using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace HandyTweaks.Features.Discard
{
    /// <summary>
    /// Net helpers for Discard feature.
    /// We register the message type as a simple 'bool' to avoid ProtoBuf contracts.
    /// </summary>
    public static class DiscardNet
    {
        public const string Channel = "handytweaks:discard";

        public static void RegisterTypes(INetworkChannel ch)
        {
            if (ch == null) return;
            try
            {
                // Only used for the toggle -> just send a bool (true=ON / false=OFF)
                ch.RegisterMessageType<bool>();
            }
            catch
            {
                // Already registered – safe to ignore
            }
        }
    }
}
