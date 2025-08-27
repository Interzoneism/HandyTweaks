using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace HandyTweaks.Features.Discard
{
    /// <summary>
    /// Client: toggle hotkey and small chat feedback.
    /// Sends a BOOL over the network (no ProtoBuf attributes needed).
    /// </summary>
    public class DiscardClient
    {
        private readonly ICoreClientAPI capi;
        private IClientNetworkChannel ch;
        private bool enabled;

        public DiscardClient(ICoreClientAPI capi, bool startEnabled = false)
        {
            this.capi = capi;
            enabled = startEnabled;
        }

        public void Start()
        {
            // Create or get the channel safely
            ch = capi.Network.GetChannel(DiscardNet.Channel)
                 ?? capi.Network.RegisterChannel(DiscardNet.Channel);
            DiscardNet.RegisterTypes(ch);

            // Toggle hotkey (change default if you want)
            capi.Input.RegisterHotKey("ht-toggle-discard", "HandyTweaks: Toggle Discard mode",
                GlKeys.B, HotkeyType.GUIOrOtherControls);

            capi.Input.SetHotKeyHandler("ht-toggle-discard", _ =>
            {
                enabled = !enabled;

                // Send 'true' or 'false' as the packet
                ch.SendPacket(enabled);

                capi.ShowChatMessage("[HandyTweaks] Discard " + (enabled ? "ON" : "OFF"));
                return true;
            });
        }
    }
}
