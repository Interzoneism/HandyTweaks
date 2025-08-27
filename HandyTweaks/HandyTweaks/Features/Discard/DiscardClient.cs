using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace HandyTweaks.Features.Discard
{
    /// <summary>
    /// Client-side hotkey & tiny feedback.
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
            ch = capi.Network.GetChannel(DiscardNet.Channel);
            DiscardNet.RegisterTypes(ch);

            // Toggle key (change default if you wish)
            capi.Input.RegisterHotKey("ht-toggle-discard", "HandyTweaks: Toggle Discard mode",
                GlKeys.B, HotkeyType.GUIOrOtherControls);

            capi.Input.SetHotKeyHandler("ht-toggle-discard", _ =>
            {
                enabled = !enabled;
                ch.SendPacket(new DiscardToggleMsg { Enabled = enabled });
                capi.ShowChatMessage("[HandyTweaks] Discard " + (enabled ? "ON" : "OFF"));
                return true;
            });
        }
    }
}
