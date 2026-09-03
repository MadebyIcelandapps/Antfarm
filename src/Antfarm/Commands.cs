using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Antfarm.Core;
using Antfarm.Sim;

namespace Antfarm;

/// <summary>
/// Lists the tribes and how far away they are.
///
/// A player standing in an 8,400 tile world has no way to find a colony that
/// moved on months ago. Reported as "I see no one" with 12,000 villagers alive,
/// because the nearest one was 609 tiles away.
/// </summary>
public class TribesCommand : ModCommand
{
    // Server ONLY, deliberately.
    //
    // Chat runs on the client. Setting Chat|Server does not help: the client
    // handler wins and the command never reaches the server. The client holds
    // tribe names and colours but no villagers, because villagers are streamed
    // separately and only within range, so it answered "10 tribes, none of them
    // have any villagers" while 12,202 were working on the server.
    public override CommandType Type => CommandType.Server;
    public override string Command => "tribes";
    public override string Usage => "/tribes";
    public override string Description => "List the tribes, where they are, and how far away";

    public override void Action(CommandCaller caller, string input, string[] args)
    {
        AntfarmSystem sys = ModContent.GetInstance<AntfarmSystem>();

        if (sys == null || sys.Tribes.Count == 0)
        {
            caller.Reply("No tribes.", Color.Orange);
            return;
        }

        int px = (int)(caller.Player.Center.X / 16f);
        int py = (int)(caller.Player.Center.Y / 16f);

        caller.Reply($"You are at {px},{py}", Color.LightGray);

        lock (sys.Tribes)
        {
            foreach (Tribe t in sys.Tribes)
            {
                Villager nearest = null;
                long best = long.MaxValue;

                foreach (Villager v in t.Villagers)
                {
                    long dx = v.TileX - px, dy = v.TileY - py;
                    long d = dx * dx + dy * dy;
                    if (d < best) { best = d; nearest = v; }
                }

                string where = nearest == null
                    ? "no villagers"
                    : $"nearest at {nearest.TileX},{nearest.TileY} ({(int)System.Math.Sqrt(best)} tiles)";

                caller.Reply(
                    $"{t.Name}{(t.Undead ? " (risen)" : "")}: {t.Villagers.Count} villagers, {where}",
                    new Color(t.ColorR, t.ColorG, t.ColorB));
            }
        }

        caller.Reply("Use /goto <tribe> to jump to one, or /goto for the nearest.", Color.LightGray);
    }
}

/// <summary>Teleport to a tribe's nearest villager, because walking 600 tiles through their tunnels is not a game.</summary>
public class GotoCommand : ModCommand
{
    // Server ONLY, deliberately.
    //
    // Chat runs on the client. Setting Chat|Server does not help: the client
    // handler wins and the command never reaches the server. The client holds
    // tribe names and colours but no villagers, because villagers are streamed
    // separately and only within range, so it answered "10 tribes, none of them
    // have any villagers" while 12,202 were working on the server.
    public override CommandType Type => CommandType.Server;
    public override string Command => "goto";
    public override string Usage => "/goto [tribe]";
    public override string Description => "Teleport to the nearest villager, or to a named tribe";

    public override void Action(CommandCaller caller, string input, string[] args)
    {
        AntfarmSystem sys = ModContent.GetInstance<AntfarmSystem>();

        if (sys == null || sys.Tribes.Count == 0)
        {
            caller.Reply("No tribes.", Color.Orange);
            return;
        }

        string want = args.Length > 0 ? args[0] : null;

        Player player = caller.Player;
        int px = (int)(player.Center.X / 16f);
        int py = (int)(player.Center.Y / 16f);

        Villager target = null;
        Tribe targetTribe = null;
        long best = long.MaxValue;

        lock (sys.Tribes)
        {
            foreach (Tribe t in sys.Tribes)
            {
                if (want != null && !t.Name.StartsWith(want, System.StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (Villager v in t.Villagers)
                {
                    long dx = v.TileX - px, dy = v.TileY - py;
                    long d = dx * dx + dy * dy;

                    if (d >= best)
                        continue;

                    best = d;
                    target = v;
                    targetTribe = t;
                }
            }
        }

        if (target == null)
        {
            caller.Reply(want == null ? "No villagers anywhere." : $"No tribe matching '{want}'.", Color.Orange);
            return;
        }

        // Land just above them rather than inside the tunnel wall.
        var pos = new Vector2(target.X - 8f, target.Y - 48f);

        player.Teleport(pos, 1);
        NetMessage.SendData(MessageID.TeleportEntity, -1, -1, null, 0, player.whoAmI, pos.X, pos.Y, 1);

        caller.Reply(
            $"Sent you to {targetTribe.Name} at {target.TileX},{target.TileY}. " +
            $"{targetTribe.Villagers.Count} of them here.",
            new Color(targetTribe.ColorR, targetTribe.ColorG, targetTribe.ColorB));
    }
}
