using DemoServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SimplPipelines;
using System;

namespace KestrelServer
{
    public class Startup : IDisposable
    {
        SimplPipelineServer _server = new ReverseServer();

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
            => services.Add(new ServiceDescriptor(typeof(SimplPipelineServer), _server));

        public void Dispose() => _server.Dispose();

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, IApplicationLifetime lifetime)
        {
            if (env.IsDevelopment()) app.UseDeveloperExceptionPage();
            app.Run(context => context.Response.WriteAsync($"clients: {_server.ClientCount}"));
        }
    }
}
