using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace Antfarm.Core;

/// <summary>
/// Makes a dedicated server keep running the world with nobody connected.
///
/// Terraria's dedicated server loop looks like this:
///
///     while (!Netplay.Disconnect) {
///         if (Netplay.HasClients) this.Update(new GameTime());
///         else                    saveTime.Stop();
///         OnTickForThirdPartySoftwareOnly?.Invoke();
///         ... sleep to 16.67ms ...
///     }
///
/// So an empty server spins at sixty times a second doing nothing at all. The
/// world does not advance, which for this mod means ten tribes freeze mid dig
/// until somebody logs in. That defeats the entire point of leaving it running.
///
/// The fix uses the hook on the line below the gate. It is invoked every
/// iteration regardless of whether anyone is connected, so when there are no
/// clients this drives the update the loop skipped. When a player IS connected
/// the game has already updated this iteration and this does nothing, so the
/// world never ticks twice.
///
/// This is deliberately not an IL patch. The gate could have been rewritten
/// with MonoMod, but a reflection call onto an existing hook survives a
/// tModLoader update far better than a rewritten branch does, and if it ever
/// stops working it fails in the open rather than corrupting the loop.
/// </summary>
public class HeadlessTicker : ModSystem
{
    private static FieldInfo _hookField;
    private static Action _hook;
    private static Action<GameTime> _update;

    private static readonly GameTime Time = new();

    private static bool _bindFailed;
    private static bool _announced;
    private static long _drivenTicks;

    /// <summary>Ticks this has driven that the game would otherwise have skipped.</summary>
    public static long DrivenTicks => _drivenTicks;

    public override void Load()
    {
        // Only the dedicated server has the gate. Single player already ticks.
        if (!Main.dedServ)
            return;

        _hookField = typeof(Main).GetField(
            "OnTickForThirdPartySoftwareOnly",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        if (_hookField == null)
        {
            Mod.Logger.Warn(
                "antfarm: Main.OnTickForThirdPartySoftwareOnly not found, so an empty server " +
                "will freeze the colony until somebody connects.");
            return;
        }

        _hook = ServerTick;
        _hookField.SetValue(null, Delegate.Combine((Action)_hookField.GetValue(null), _hook));

        Mod.Logger.Info("antfarm: headless ticker attached, the world will run with nobody connected");
    }

    public override void Unload()
    {
        if (_hookField != null && _hook != null)
            _hookField.SetValue(null, Delegate.Remove((Action)_hookField.GetValue(null), _hook));

        _hookField = null;
        _hook = null;
        _update = null;
        _bindFailed = false;
        _announced = false;
    }

    private static void ServerTick()
    {
        // Somebody is connected, so the loop already called Update this pass.
        if (Netplay.HasClients)
            return;

        if (AntfarmConfig.Instance is { TickWhenEmpty: false })
            return;

        if (!EnsureUpdateBound())
            return;

        try
        {
            _update(Time);
            _drivenTicks++;

            if (!_announced)
            {
                _announced = true;
                ModContent.GetInstance<Antfarm>()?.Logger.Info(
                    "antfarm: driving the world with no players connected");
            }
        }
        catch (Exception ex)
        {
            _bindFailed = true;
            ModContent.GetInstance<Antfarm>()?.Logger.Error(
                "antfarm: headless tick failed, falling back to vanilla behaviour: " + ex);
        }
    }

    private static bool EnsureUpdateBound()
    {
        if (_update != null)
            return true;

        if (_bindFailed || Main.instance == null)
            return false;

        try
        {
            MethodInfo mi = typeof(Main).GetMethod(
                "Update",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { typeof(GameTime) },
                null);

            if (mi == null)
            {
                _bindFailed = true;
                return false;
            }

            _update = (Action<GameTime>)Delegate.CreateDelegate(typeof(Action<GameTime>), Main.instance, mi);
            return true;
        }
        catch (Exception ex)
        {
            _bindFailed = true;
            ModContent.GetInstance<Antfarm>()?.Logger.Error("antfarm: could not bind Main.Update: " + ex);
            return false;
        }
    }
}
