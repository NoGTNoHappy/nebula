﻿using HarmonyLib;
using NebulaAPI;
using NebulaModel;
using NebulaModel.DataStructures;
using NebulaModel.Networking;
using NebulaModel.Networking.Serialization;
using NebulaModel.Packets.GameHistory;
using NebulaModel.Packets.GameStates;
using NebulaModel.Packets.Universe;
using NebulaModel.Utils;
using NebulaWorld;
using Open.Nat;
using NebulaWorld.SocialIntegration;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Net;
using WebSocketSharp.Server;

namespace NebulaNetwork
{
    public class Server : NetworkProvider, IServer
    {
        private const float GAME_RESEARCH_UPDATE_INTERVAL = 2;
        private const float STATISTICS_UPDATE_INTERVAL = 1;
        private const float LAUNCH_UPDATE_INTERVAL = 4;
        private const float DYSONSPHERE_UPDATE_INTERVAL = 2;
        private const float WARNING_UPDATE_INTERVAL = 1;

        private float gameResearchHashUpdateTimer = 0;
        private float productionStatisticsUpdateTimer = 0;
        private float dysonLaunchUpateTimer = 1;
        private float dysonSphereUpdateTimer = 0;
        private float warningUpdateTimer = 0;

        private WebSocketServer socket;
        private Ngrok.NgrokManager ngrokManager;

        private readonly ushort port;
        private readonly bool loadSaveFile;

        public ushort Port => port;
        public string NgrokAddress => ngrokManager.NgrokAddress;
        public bool NgrokActive => ngrokManager.IsNgrokActive();
        public bool NgrokEnabled => ngrokManager.NgrokEnabled;
        public string NgrokLastErrorCode => ngrokManager.NgrokLastErrorCode;

        public Server(ushort port, bool loadSaveFile = false) : base(new PlayerManager())
        {
            this.port = port;
            this.loadSaveFile = loadSaveFile;
        }

        public override void Start()
        {
            if (loadSaveFile)
            {
                SaveManager.LoadServerData();
            }

            foreach (Assembly assembly in AssembliesUtils.GetNebulaAssemblies())
            {
                PacketUtils.RegisterAllPacketNestedTypesInAssembly(assembly, PacketProcessor);
            }
            PacketUtils.RegisterAllPacketProcessorsInCallingAssembly(PacketProcessor, true);

            foreach (Assembly assembly in NebulaModAPI.TargetAssemblies)
            {
                PacketUtils.RegisterAllPacketNestedTypesInAssembly(assembly, PacketProcessor);
                PacketUtils.RegisterAllPacketProcessorsInAssembly(assembly, PacketProcessor, true);
            }
#if DEBUG
            PacketProcessor.SimulateLatency = true;
#endif

            if (Config.Options.EnableUPnpOrPmpSupport)
            {
                Task.Run(async () => {
                    var discoverer = new NatDiscoverer();
                    try
                    {
                        var device = await discoverer.DiscoverDeviceAsync();
                        await device.CreatePortMapAsync(new Mapping(Protocol.Tcp, port, port, "DSP nebula"));
                        NebulaModel.Logger.Log.Info($"Successfully created UPnp or Pmp port mapping for {port}");
                    }
                    catch (NatDeviceNotFoundException)
                    {
                        NebulaModel.Logger.Log.WarnInform("No UPnp or Pmp compatible/enabled NAT device found");
                    }
                    catch (MappingException)
                    {
                        NebulaModel.Logger.Log.WarnInform("Could not create UPnp or Pmp port mapping");
                    }
                });
            }

            ngrokManager = new Ngrok.NgrokManager(port);                

            socket = new WebSocketServer(System.Net.IPAddress.IPv6Any, port);
            socket.Log.Level = LogLevel.Debug;
            socket.Log.Output = NebulaModel.Logger.Log.SocketOutput;
            socket.AllowForwardedRequest = true; // This is required to make the websocket play nice with tunneling services like ngrok

            if (!string.IsNullOrWhiteSpace(Config.Options.ServerPassword))
            {
                socket.AuthenticationSchemes = AuthenticationSchemes.Basic;
                socket.UserCredentialsFinder = id => {
                    var name = id.Name;

                    // Return user name, password, and roles.
                    return name == "nebula-player"
                           ? new NetworkCredential(name, Config.Options.ServerPassword)
                           : null; // If the user credentials are not found.
                };
            }

            DisableNagleAlgorithm(socket);
            WebSocketService.PacketProcessor = PacketProcessor;
            WebSocketService.PlayerManager = PlayerManager;
            socket.AddWebSocketService<WebSocketService>("/socket", wse => new WebSocketService());
            try
            {
                socket.KeepClean = Config.Options.CleanupInactiveSessions;
                socket.Start();
            }catch(System.InvalidOperationException e)
            {
                InGamePopup.ShowError("Error", "An error occurred while hosting the game: " + e.Message, "Close");
                Stop();
                return;
            }

            ((LocalPlayer)Multiplayer.Session.LocalPlayer).IsHost = true;

            ((LocalPlayer)Multiplayer.Session.LocalPlayer).SetPlayerData(new PlayerData(
                PlayerManager.GetNextAvailablePlayerId(),
                GameMain.localPlanet?.id ?? -1,
                !string.IsNullOrWhiteSpace(Config.Options.Nickname) ? Config.Options.Nickname : GameMain.data.account.userName), loadSaveFile);

            Task.Run(async () =>
            {
                if(ngrokManager.IsNgrokActive())
                {
                    string ip = await ngrokManager.GetNgrokAddressAsync();
                    DiscordManager.UpdateRichPresence(ip: ip, updateTimestamp: true);
                    if (Multiplayer.IsDedicated)
                    {
                        NebulaModel.Logger.Log.Info($">> Ngrok address: {ip}");
                    }
                }
                else
                {
                    DiscordManager.UpdateRichPresence(ip: $"{(Config.Options.IPConfiguration != IPUtils.IPConfiguration.IPv6 ? await IPUtils.GetWANv4Address() : string.Empty)};" +
                                                          $"{(Config.Options.IPConfiguration != IPUtils.IPConfiguration.IPv4 ? await IPUtils.GetWANv6Address() : string.Empty)};" +
                                                          $"{port}",
                                                      updateTimestamp: true);
                }
            });

            NebulaModAPI.OnMultiplayerGameStarted?.Invoke();
        }

        public override void Stop()
        {
            socket?.Stop();

            ngrokManager?.StopNgrok();

            NebulaModAPI.OnMultiplayerGameEnded?.Invoke();
        }

        public override void Dispose()
        {
            Stop();
        }

        public override void SendPacket<T>(T packet)
        {
            PlayerManager.SendPacketToAllPlayers(packet);
        }

        public override void SendPacketToLocalStar<T>(T packet)
        {
            PlayerManager.SendPacketToLocalStar(packet);
        }

        public override void SendPacketToLocalPlanet<T>(T packet)
        {
            PlayerManager.SendPacketToLocalPlanet(packet);
        }

        public override void SendPacketToPlanet<T>(T packet, int planetId)
        {
            PlayerManager.SendPacketToPlanet(packet, planetId);
        }

        public override void SendPacketToStar<T>(T packet, int starId)
        {
            PlayerManager.SendPacketToStar(packet, starId);
        }

        public override void SendPacketExclude<T>(T packet, INebulaConnection exclude)
        {
            PlayerManager.SendPacketToOtherPlayers(packet, exclude);
        }

        public override void SendPacketToStarExclude<T>(T packet, int starId, INebulaConnection exclude)
        {
            PlayerManager.SendPacketToStarExcept(packet, starId, exclude);
        }

        public override void Update()
        {
            PacketProcessor.ProcessPacketQueue();

            if (Multiplayer.Session.IsGameLoaded)
            {
                gameResearchHashUpdateTimer += Time.deltaTime;
                productionStatisticsUpdateTimer += Time.deltaTime;
                dysonLaunchUpateTimer += Time.deltaTime;
                dysonSphereUpdateTimer += Time.deltaTime;
                warningUpdateTimer += Time.deltaTime;

                if (gameResearchHashUpdateTimer > GAME_RESEARCH_UPDATE_INTERVAL)
                {
                    gameResearchHashUpdateTimer = 0;
                    if (GameMain.data.history.currentTech != 0)
                    {
                        TechState state = GameMain.data.history.techStates[GameMain.data.history.currentTech];
                        SendPacket(new GameHistoryResearchUpdatePacket(GameMain.data.history.currentTech, state.hashUploaded, state.hashNeeded));
                    }
                }

                if (productionStatisticsUpdateTimer > STATISTICS_UPDATE_INTERVAL)
                {
                    productionStatisticsUpdateTimer = 0;
                    Multiplayer.Session.Statistics.SendBroadcastIfNeeded();
                }

                if (dysonLaunchUpateTimer > LAUNCH_UPDATE_INTERVAL)
                {
                    dysonLaunchUpateTimer = 0;
                    Multiplayer.Session.Launch.SendBroadcastIfNeeded();
                }

                if (dysonSphereUpdateTimer > DYSONSPHERE_UPDATE_INTERVAL)
                {
                    dysonSphereUpdateTimer = 0;
                    Multiplayer.Session.DysonSpheres.UpdateSphereStatusIfNeeded();
                }

                if (warningUpdateTimer > WARNING_UPDATE_INTERVAL)
                {
                    warningUpdateTimer = 0;
                    Multiplayer.Session.Warning.SendBroadcastIfNeeded();
                }
            }            
        }

        private void DisableNagleAlgorithm(WebSocketServer socketServer)
        {
            TcpListener listener = AccessTools.FieldRefAccess<WebSocketServer, TcpListener>("_listener")(socketServer);
            listener.Server.NoDelay = true;
        }

        private class WebSocketService : WebSocketBehavior
        {
            public static IPlayerManager PlayerManager;
            public static NetPacketProcessor PacketProcessor;

            public WebSocketService() { }

            public WebSocketService(IPlayerManager playerManager, NetPacketProcessor packetProcessor)
            {
                PlayerManager = playerManager;
                PacketProcessor = packetProcessor;
            }

            protected override void OnOpen()
            {
                if (Multiplayer.Session.IsGameLoaded == false && Multiplayer.Session.IsInLobby == false)
                {
                    // Reject any connection that occurs while the host's game is loading.
                    Context.WebSocket.Close((ushort)DisconnectionReason.HostStillLoading, "Host still loading, please try again later.");
                    return;
                }

                NebulaModel.Logger.Log.Info($"Client connected ID: {ID}");
                NebulaConnection conn = new NebulaConnection(Context.WebSocket, Context.UserEndPoint, PacketProcessor);
                PlayerManager.PlayerConnected(conn);
            }

            protected override void OnMessage(MessageEventArgs e)
            {
                PacketProcessor.EnqueuePacketForProcessing(e.RawData, new NebulaConnection(Context.WebSocket, Context.UserEndPoint, PacketProcessor));
            }

            protected override void OnClose(CloseEventArgs e)
            {
                // If the reason of a client disconnect is because we are still loading the game,
                // we don't need to inform the other clients since the disconnected client never
                // joined the game in the first place.
                if (e.Code == (short)DisconnectionReason.HostStillLoading)
                {
                    return;
                }

                NebulaModel.Logger.Log.Info($"Client disconnected: {ID}, reason: {e.Reason}");
                UnityDispatchQueue.RunOnMainThread(() =>
                {
                    // This is to make sure that we don't try to deal with player disconnection
                    // if it is because we have stopped the server and are not in a multiplayer game anymore.
                    if (Multiplayer.IsActive)
                    {
                        PlayerManager.PlayerDisconnected(new NebulaConnection(Context.WebSocket, Context.UserEndPoint, PacketProcessor));
                    }
                });
            }

            protected override void OnError(ErrorEventArgs e)
            {
                // TODO: seems like clients erroring out in the sync process can lock the host with the joining player message, maybe this fixes it
                NebulaModel.Logger.Log.Info($"Client disconnected because of an error: {ID}, reason: {e.Exception}");
                UnityDispatchQueue.RunOnMainThread(() =>
                {
                    // This is to make sure that we don't try to deal with player disconnection
                    // if it is because we have stopped the server and are not in a multiplayer game anymore.
                    if (Multiplayer.IsActive)
                    {
                        PlayerManager.PlayerDisconnected(new NebulaConnection(Context.WebSocket, Context.UserEndPoint, PacketProcessor));
                    }
                });
            }
        }
    }
}
