using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace HandyTweaks.Features.Discard
{
    /// <summary>
    /// Server: keeps per-player "discard mode" and tags item entities at spawn.
    /// The toggle message is a BOOL (true/false), so no ProtoBuf ref is needed.
    /// </summary>
    public class DiscardServer
    {
        private readonly ICoreServerAPI sapi;
        private readonly Dictionary<string, bool> modeOnByUid = new();
        private readonly int tagSeconds;

        public bool Debug { get; private set; } = false;   // <— add

        public bool IsModeOn(string uid)
        {
            return uid != null && modeOnByUid.TryGetValue(uid, out var on) && on;
        }

        public void Start()
        {
            IServerNetworkChannel ch = sapi.Network.GetChannel(DiscardNet.Channel)
                                       ?? sapi.Network.RegisterChannel(DiscardNet.Channel);
            DiscardNet.RegisterTypes(ch);

            // Toggle handler
            ch.SetMessageHandler<bool>((from, enabled) =>
            {
                modeOnByUid[from.PlayerUID] = enabled;

                if (Debug)
                    sapi.Logger.Notification($"[DiscardDBG] Toggle from {from.PlayerUID}: {(enabled ? "ON" : "OFF")}");

                from.SendMessage(GlobalConstants.GeneralChatGroup,
                    $"[HandyTweaks] Discard mode {(enabled ? "ON" : "OFF")}.",
                    EnumChatType.Notification);
            });

            // Tag items dropped by players with mode ON
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

                    if (Debug)
                    {
                        sapi.Logger.Notification($"[DiscardDBG] Marked drop by={ei.ByPlayerUid} entityId={ei.EntityId} until={wat.GetLong("ht_discard_until")}");
                        var sp = sapi.World.PlayerByUid(ei.ByPlayerUid) as IServerPlayer;
                        sp?.SendMessage(GlobalConstants.GeneralChatGroup, "[DiscardDBG] Marked your dropped item", EnumChatType.Notification);
                    }
                }
            };

            sapi.Event.PlayerLeave += sp => modeOnByUid.Remove(sp.PlayerUID);

            RegisterCommandsSafe();
        }

        private void RegisterCommandsSafe()
        {
            try
            {
                sapi.ChatCommands
                    .Create("htdiscard")
                    .WithDescription("Handy Tweaks - Discard mode")
                    .BeginSubCommand("on").WithDescription("Enable Discard mode")
                        .HandleWith(cmd => { Set(cmd.Caller as IServerPlayer, true); return TextCommandResult.Success("ON"); })
                    .EndSubCommand()
                    .BeginSubCommand("off").WithDescription("Disable Discard mode")
                        .HandleWith(cmd => { Set(cmd.Caller as IServerPlayer, false); return TextCommandResult.Success("OFF"); })
                    .EndSubCommand()
                    // >>> Add this:
                    .BeginSubCommand("debug").WithDescription("Toggle discard debug")
                        .WithArgs(api.ChatCommands.Parsers.Bool("on"))
                        .HandleWith(cmd =>
                        {
                            Debug = cmd.Parsers[0].SuccessfullyParsed ? (bool)cmd.Parsers[0].GetValue() : !Debug;
                            return TextCommandResult.Success("debug " + (Debug ? "ON" : "OFF"));
                        })
                    .EndSubCommand();

                sapi.Logger.Notification("[HandyTweaks] Registered /htdiscard command.");
            }
            catch
            {
                sapi.Logger.Notification("[HandyTweaks] /htdiscard already exists, skipping registration.");
            }
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
