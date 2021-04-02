﻿using NebulaModel.Attributes;
using NebulaModel.DataStructures;
using NebulaModel.Logger;
using NebulaModel.Networking;
using NebulaModel.Packets.Processors;
using NebulaModel.Packets.Universe;
using NebulaWorld.Universe;

namespace NebulaClient.PacketProcessors.Universe
{
    [RegisterPacketProcessor]
    class DysonSwarmAddOrbitProcessor : IPacketProcessor<DysonSwarmAddOrbitPacket>
    {
        public void ProcessPacket(DysonSwarmAddOrbitPacket packet, NebulaConnection conn)
        {
            Log.Info($"Processing DysonSwarm New Orbit notification for system {GameMain.data.galaxy.stars[packet.StarIndex].name} (Index: {GameMain.data.galaxy.stars[packet.StarIndex].index})");
            DysonSphere_Manager.IncomingDysonSwarmPacket = true;
            GameMain.data.dysonSpheres[packet.StarIndex]?.swarm?.NewOrbit(packet.Radius, DataStructureExtensions.ToUnity(packet.Rotation));
            DysonSphere_Manager.IncomingDysonSwarmPacket = false;
        }
    }
}