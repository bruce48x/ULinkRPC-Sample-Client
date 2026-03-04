using System;
using System.Threading;
using System.Threading.Tasks;
using Shared.Interfaces;
using ULinkRPC.Core;

namespace Shared.Interfaces.Runtime.Generated
{
    public sealed class MyFirstServiceClient : IMyFirstService
    {
        private const int ServiceId = 1;
        private static readonly RpcMethod<(int, int), int> sumAsyncRpcMethod = new(ServiceId, 1);

        private readonly IRpcClient _client;

        public MyFirstServiceClient(IRpcClient client) { _client = client; }

        public ValueTask<int> SumAsync(int x, int y)
        {
            return SumAsync(x, y, CancellationToken.None);
        }

        public ValueTask<int> SumAsync(int x, int y, CancellationToken ct)
        {
            return _client.CallAsync(sumAsyncRpcMethod, (x, y), ct);
        }
    }

    public static class MyFirstServiceClientExtensions
    {
        public static IMyFirstService CreateMyFirstService(this IRpcClient client)
        {
            if (client is null) throw new ArgumentNullException(nameof(client));
            return new MyFirstServiceClient(client);
        }
    }
}
