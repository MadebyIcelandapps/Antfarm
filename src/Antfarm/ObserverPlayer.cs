using Terraria;
using Terraria.ModLoader;

namespace Antfarm;

/// <summary>
/// Everyone on this server is a spectator, not a participant.
///
/// The point of the world is watching ten tribes tear it apart, and they have
/// quarried the surface into pits and shafts that are genuinely impossible to
/// walk across. Dying to a fall while trying to look at a tower, or being
/// unable to reach one at all, is not the game. So every player is immortal
/// and can fly.
/// </summary>
public class ObserverPlayer : ModPlayer
{
    /// <summary>Hold jump to rise. Deliberately brisk: the world is 8,400 tiles wide.</summary>
    private const float FlySpeed = 9f;

    public override void PostUpdate()
    {
        Player p = Player;

        // Immortal. Topped up every tick rather than blocking damage, so the
        // hit still registers and knockback still feels like something.
        p.statLife = p.statLifeMax2;
        p.breath = p.breathMax;
        p.lavaImmune = true;
        p.noKnockback = true;

        // Never take fall damage, however deep the shaft they dug.
        p.fallStart = (int)(p.position.Y / 16f);

        // Flight, without needing wings. Holding jump climbs, and gravity is
        // cancelled while doing it so it holds altitude rather than sagging.
        if (p.controlJump)
        {
            p.velocity.Y = -FlySpeed;
            p.gravity = 0f;
        }

        // Sink gently rather than plummeting when not holding jump, so hovering
        // over a settlement to watch it is actually possible.
        else if (p.velocity.Y > 2f && p.controlDown == false)
        {
            p.velocity.Y = 2f;
        }
    }

    public override bool CanBeHitByNPC(NPC npc, ref int cooldownSlot) => false;

    public override bool CanBeHitByProjectile(Projectile proj) => false;
}
