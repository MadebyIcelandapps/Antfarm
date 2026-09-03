using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace Antfarm;

// Labels and tooltips live in Localization/en-US.hjson. tModLoader deprecated
// the [Label] and [Tooltip] attributes in favour of localisation files, and
// wires them up automatically from the property names below.
public class AntfarmConfig : ModConfig
{
    public static AntfarmConfig Instance;

    public override ConfigScope Mode => ConfigScope.ServerSide;

    [Header("Colony")]

    [Range(1, 32)]
    [DefaultValue(10)]
    public int TribeCount { get; set; }

    [Range(4, 400)]
    [DefaultValue(40)]
    public int VillagersPerTribe { get; set; }

    [Header("Performance")]

    [Range(1, 4096)]
    [DefaultValue(96)]
    public int TileOpsPerTick { get; set; }

    [Range(1, 64)]
    [DefaultValue(8)]
    public int UnfocusedBudgetMultiplier { get; set; }

    [Range(10, 240)]
    [DefaultValue(60)]
    public int SimHz { get; set; }

    [DefaultValue(true)]
    public bool TickWhenEmpty { get; set; }

    [Header("Visuals")]

    [DefaultValue(true)]
    public bool DrawVillagers { get; set; }

    [Header("ObservationWindow")]

    [DefaultValue(true)]
    public bool ObserverEnabled { get; set; }

    [Range(1024, 65535)]
    [DefaultValue(7778)]
    [ReloadRequired]
    public int ObserverPort { get; set; }

    [DefaultValue(true)]
    public bool TimelapseEnabled { get; set; }

    [Range(1, 240)]
    [DefaultValue(15)]
    public int TimelapseMinutes { get; set; }

    public override void OnLoaded() => Instance = this;
}
