using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;

namespace KestrelServer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateWebHostBuilder(args).Build().Run();
        }

        public static IWebHostBuilder CreateWebHostBuilder(string[] args) =>
            WebHost.CreateDefaultBuilder(args)
                // .UseLibuv()
                // ^^^ use a different network stack
                .UseKestrel(options =>
                {
                    // options.ApplicationSchedulingMode = SchedulingMode.Inline;
                    // ^^^ avoid dispatch on incoming payloads (use the IO thread)

                    // HTTP 5001
                    options.ListenLocalhost(5001);

                    // TCP 5000 for the service
                    options.ListenLocalhost(5000, builder =>
                    {
                        builder.UseConnectionHandler<SimplConnectionHandler>();
                    });
                }).UseStartup<Startup>();
    }
}
