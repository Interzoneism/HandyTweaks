using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace HandyTweaks.Features.Discard
{
    /// <summary>
    /// Server keeps "discard mode" on/off per player.
    /// Also tags newly spawned item entities when the dropper has the mode ON.
    /// </summary>
    public class DiscardServer
    {
        private readonly ICoreServerAPI sapi;
        private readonly Dictionary<string, bool> modeOnByUid = new(); // PlayerUID -> ON/OFF
        private readonly int tagSeconds; // 0 = only while mode is ON; >0 extends per-entity ignore

        public DiscardServer(ICoreServerAPI sapi, int tagSeconds = 0)
        {
            this.sapi = sapi;
            this.tagSeconds = tagSeconds;
        }

        public bool IsModeOn(string uid)
        {
            return uid != null && modeOnByUid.TryGetValue(uid, out var on) && on;
        }

        public void Start()
        {
            IServerNetworkChannel ch = sapi.Network.GetChannel(DiscardNet.Channel);
            DiscardNet.RegisterTypes(ch);

            ch.SetMessageHandler<DiscardToggleMsg>((from, msg) =>
            {
                modeOnByUid[from.PlayerUID] = msg.Enabled;
                from.SendMessage(GlobalConstants.GeneralChatGroup,
                    $"[HandyTweaks] Discard mode {(msg.Enabled ? "ON" : "OFF")}.",
                    EnumChatType.Notification);
            });

            // Tag items dropped by players who currently have Discard mode ON
            sapi.Event.OnEntitySpawn += ent =>
            {
                if (ent is EntityItem ei && !string.IsNullOrEmpty(ei.ByPlayerUid) && IsModeOn(ei.ByPlayerUid))
                {
                    long nowMs = sapi.World.ElapsedMilliseconds;

                    var wat = ei.WatchedAttributes;
                    wat.SetBool("ht_discard_marked", true);
                    wat.SetString("ht_discard_uid", ei.ByPlayerUid);
                    wat.SetLong("ht_discard_until", tagSeconds <= 0 ? 0 : nowMs + tagSeconds * 1000L);
                    wat.MarkAllDirty();
                }
            };

            sapi.Event.PlayerLeave += sp => modeOnByUid.Remove(sp.PlayerUID);

            // Optional quick commands
            sapi.ChatCommands
                .Create("htdiscard")
                .WithDescription("Handy Tweaks - Discard mode")
                .BeginSubCommand("on").WithDescription("Enable Discard mode")
                    .HandleWith(cmd => { Set(cmd.Caller.Player as IServerPlayer, true); return TextCommandResult.Success("ON"); })
                .EndSubCommand()
                .BeginSubCommand("off").WithDescription("Disable Discard mode")
                    .HandleWith(cmd => { Set(cmd.Caller.Player as IServerPlayer, false); return TextCommandResult.Success("OFF"); })
                .EndSubCommand();
        }

        private void Set(IServerPlayer sp, bool on)
        {
            if (sp == null) return;
            modeOnByUid[sp.PlayerUID] = on;
            sp.SendMessage(GlobalConstants.GeneralChatGroup,
                $"[HandyTweaks] Discard mode {(on ? "ON" : "OFF")}.",
                EnumChatType.Notification);
        }
    }
}
