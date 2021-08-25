﻿using NebulaModel;
using NebulaModel.Attributes;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Trash;
using NebulaWorld;
using NebulaWorld.Trash;

namespace NebulaNetwork.PacketProcessors.Trash
{
    [RegisterPacketProcessor]
    class TrashSystemClearAllTrashProcessor : PacketProcessor<TrashSystemClearAllTrashPacket>
    {
        private IPlayerManager playerManager;

        public TrashSystemClearAllTrashProcessor()
        {
            playerManager = Multiplayer.Session?.Network.PlayerManager;
        }

        public override void ProcessPacket(TrashSystemClearAllTrashPacket packet, NebulaConnection conn)
        {
            bool valid = true;
            if (IsHost)
            {
                NebulaPlayer player = playerManager.GetPlayer(conn);
                if (player != null)
                {
                    playerManager.SendPacketToOtherPlayers(packet, player);
                }
                else
                {
                    valid = false;
                }
            }

            if (valid)
            {
                using (Multiplayer.Session.Trashes.ClearAllTrashFromOtherPlayers.On())
                {
                    GameMain.data.trashSystem.ClearAllTrash();
                }
            }
        }
    }
}