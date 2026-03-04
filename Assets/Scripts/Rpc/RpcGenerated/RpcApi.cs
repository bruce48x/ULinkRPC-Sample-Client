using System;
using Shared.Interfaces;
using ULinkRPC.Core;

namespace Shared.Interfaces.Runtime.Generated
{
    public sealed class RpcApi
    {
        public RpcApi(IRpcClient client)
        {
            if (client is null) throw new ArgumentNullException(nameof(client));
            Game = new GameRpcGroup(client);
        }

        public GameRpcGroup Game { get; }
    }

    public sealed class GameRpcGroup
    {
        public GameRpcGroup(IRpcClient client)
        {
            if (client is null) throw new ArgumentNullException(nameof(client));
            MyFirst = client.CreateMyFirstService();
        }

        public IMyFirstService MyFirst { get; }
    }

    public static class RpcApiExtensions
    {
        public static RpcApi CreateRpcApi(this IRpcClient client)
        {
            if (client is null) throw new ArgumentNullException(nameof(client));
            return new RpcApi(client);
        }
    }
}
