using System.IO;
using Terraria.ModLoader;
using Antfarm.Core;

namespace Antfarm;

// Entry point. Almost everything lives in AntfarmSystem, which is the only
// thing that touches Terraria's main thread. This exists to route packets.
public class Antfarm : Mod
{
    public override void HandlePacket(BinaryReader reader, int whoAmI)
    {
        byte kind = reader.ReadByte();

        switch (kind)
        {
            case VillagerSync.PacketVillagers:
                VillagerSync.Receive(reader);
                break;
        }
    }
}
