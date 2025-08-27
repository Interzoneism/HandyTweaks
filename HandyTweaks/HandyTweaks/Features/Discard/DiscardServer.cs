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

        // DiscardServer.cs (inside HandyTweaks.Features.Discard namespace)
        public DiscardServer(ICoreServerAPI sapi, int tagSeconds)
        {
            this.sapi = sapi;
            this.tagSeconds = tagSeconds;
        }

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

            sapi.Event.OnEntitySpawn += ent =>
            {
                if (ent is not EntityItem ei) return;

                // Debug: one line per spawn
                if (Debug)
                    sapi.Logger.Notification($"[DiscardDBG] Spawn item entId={ei.EntityId} by={ei.ByPlayerUid ?? "<null>"}");

                // Try tag immediately (works on servers where ByPlayerUid is set early)
                TryTagNow(ei, pass: 0);

                // If still not there, re-check a few times
                TryTagLater(ei.EntityId, pass: 1, delayMs: 10);
                TryTagLater(ei.EntityId, pass: 2, delayMs: 75);
                TryTagLater(ei.EntityId, pass: 3, delayMs: 200);
            };


            sapi.Event.PlayerLeave += sp => modeOnByUid.Remove(sp.PlayerUID);

            RegisterCommandsSafe();
        }

        // helpers (put inside DiscardServer)
        private void TryTagLater(long entId, int pass, int delayMs)
        {
            sapi.Event.RegisterCallback(_ =>
            {
                var e = sapi.World.GetEntityById(entId) as EntityItem;
                if (e != null) TryTagNow(e, pass);
            }, delayMs);
        }

        private void TryTagNow(EntityItem e, int pass)
        {
            // If the engine has set the UID now, tag it
            var uid = e.ByPlayerUid;
            if (!string.IsNullOrEmpty(uid) && IsModeOn(uid))
            {
                long nowMs = sapi.World.ElapsedMilliseconds;
                var wat = e.WatchedAttributes;
                wat.SetBool("ht_discard_marked", true);
                wat.SetString("ht_discard_uid", uid);
                wat.SetLong("ht_discard_until", tagSeconds <= 0 ? 0 : nowMs + tagSeconds * 1000L);
                wat.MarkAllDirty();

                if (Debug)
                    sapi.Logger.Notification($"[DiscardDBG] Marked drop pass={pass} by={uid} entId={e.EntityId} until={wat.GetLong("ht_discard_until")}");
            }
            else if (Debug)
            {
                sapi.Logger.Notification($"[DiscardDBG] Tag skipped pass={pass} by={(uid ?? "<null>")} entId={e.EntityId}");
            }
        }

        private void RegisterCommandsSafe()
        {
            try
            {
                sapi.ChatCommands
                    .Create("htdiscard")
                    .WithDescription("Handy Tweaks - Discard mode")
                    .RequiresPrivilege(Privilege.chat)   // <— root must have a requirement
                    .RequiresPlayer()                    // <— also good practice for player-only
                    .BeginSubCommand("on").WithDescription("Enable Discard mode")
                        .HandleWith(cmd =>
                        {
                            Set(cmd.Caller as IServerPlayer, true);
                            return TextCommandResult.Success("ON");
                        })
                    .EndSubCommand()
                    .BeginSubCommand("off").WithDescription("Disable Discard mode")
                        .HandleWith(cmd =>
                        {
                            Set(cmd.Caller as IServerPlayer, false);
                            return TextCommandResult.Success("OFF");
                        })
                    .EndSubCommand()
                    .BeginSubCommand("debug").WithDescription("Toggle discard debug")
                        .HandleWith(cmd =>
                        {
                            Debug = !Debug;
                            (cmd.Caller as IServerPlayer)?.SendMessage(
                                GlobalConstants.GeneralChatGroup,
                                $"[HandyTweaks] Discard debug {(Debug ? "ON" : "OFF")}.",
                                EnumChatType.Notification);
                            return TextCommandResult.Success("debug " + (Debug ? "ON" : "OFF"));
                        })
                    .EndSubCommand();

                sapi.Logger.Notification("[HandyTweaks] Registered /htdiscard command.");
            }
            catch (System.Exception e)
            {
                sapi.Logger.Warning("[HandyTweaks] /htdiscard already exists or failed to register: " + e.Message);
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
