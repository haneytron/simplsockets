using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Libuv.Internal.Networking;
using SimplPipelines;
using System.IO;
using System.Threading.Tasks;

namespace KestrelServer
{
    public class SimplConnectionHandler : ConnectionHandler
    {
        private readonly SimplPipelineServer _server;
        public SimplConnectionHandler(SimplPipelineServer server) => _server = server;
        public override async Task OnConnectedAsync(ConnectionContext connection)
        {
            try
            {
                await _server.RunClientAsync(connection.Transport);
            }
            catch (IOException io) when (io.InnerException is UvException uv && uv.StatusCode == -4077)
            { } //swallow libuv disconnect
        }
    }
}
