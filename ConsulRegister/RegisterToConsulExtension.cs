using Consul;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ConsulRegister
{
    public static class RegisterToConsulExtension
    {
        /// <summary>
        /// Add Consul
        /// 添加consul
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IServiceCollection AddConsul(this IServiceCollection services, IConfiguration configuration)
        {
            // configuration Consul register address
            //配置consul注册地址
            services.Configure<ServiceDiscoveryOptions>(configuration.GetSection("ServiceDiscovery"));

            //configuration Consul client
            //配置consul客户端
            services.AddSingleton<IConsulClient>(sp => new Consul.ConsulClient(config =>
            {
                var consulOptions = sp.GetRequiredService<IOptions<ServiceDiscoveryOptions>>().Value;
                if (!string.IsNullOrWhiteSpace(consulOptions.Consul.HttpEndPoint))
                {
                    config.Address = new Uri(consulOptions.Consul.HttpEndPoint);
                }
            }));

            return services;
        }

        /// <summary>
        /// use Consul
        /// 使用consul
        /// The default health check interface format is http://host:port/HealthCheck
        /// 默认的健康检查接口格式是 http://host:port/HealthCheck
        /// </summary>
        /// <param name="app"></param>
        /// <returns></returns>
        public static IApplicationBuilder UseConsul(this IApplicationBuilder app)
        {
            IConsulClient consul = app.ApplicationServices.GetRequiredService<IConsulClient>();
            IApplicationLifetime appLife = app.ApplicationServices.GetRequiredService<IApplicationLifetime>();
            IOptions<ServiceDiscoveryOptions> serviceOptions = app.ApplicationServices.GetRequiredService<IOptions<ServiceDiscoveryOptions>>();
            var features = app.Properties["server.Features"] as FeatureCollection;

            var port = new Uri(features.Get<IServerAddressesFeature>()
                .Addresses
                .FirstOrDefault()).Port;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"application port is :{port}");
            var addressIpv4Hosts = NetworkInterface.GetAllNetworkInterfaces()
            .OrderByDescending(c => c.Speed)
            .Where(c => c.NetworkInterfaceType != NetworkInterfaceType.Loopback && c.OperationalStatus == OperationalStatus.Up);

            foreach (var item in addressIpv4Hosts)
            {
                var props = item.GetIPProperties();
                //this is ip for ipv4
                //这是ipv4的ip地址
                var firstIpV4Address = props.UnicastAddresses
                    .Where(c => c.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(c => c.Address)
                    .FirstOrDefault().ToString();
                var serviceId = $"{serviceOptions.Value.ServiceName}_{firstIpV4Address}:{port}";

                var httpCheck = new AgentServiceCheck()
                {
                    DeregisterCriticalServiceAfter = TimeSpan.FromMinutes(1),
                    Interval = TimeSpan.FromSeconds(30),
                    //this is default health check interface
                    //这个是默认健康检查接口
                    HTTP = $"{Uri.UriSchemeHttp}://{firstIpV4Address}:{port}/HealthCheck",
                };

                var registration = new AgentServiceRegistration()
                {
                    Checks = new[] { httpCheck },
                    Address = firstIpV4Address.ToString(),
                    ID = serviceId,
                    Name = serviceOptions.Value.ServiceName,
                    Port = port
                };

                consul.Agent.ServiceRegister(registration).GetAwaiter().GetResult();

                //send consul request after service stop
                //当服务停止后想consul发送的请求
                appLife.ApplicationStopping.Register(() =>
                {
                    consul.Agent.ServiceDeregister(serviceId).GetAwaiter().GetResult();
                });

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"health check service:{httpCheck.HTTP}");
            }

            //register localhost address
            //注册本地地址
            var localhostregistration = new AgentServiceRegistration()
            {
                Checks = new[] { new AgentServiceCheck()
                {
                    DeregisterCriticalServiceAfter = TimeSpan.FromMinutes(1),
                    Interval = TimeSpan.FromSeconds(30),
                    HTTP = $"{Uri.UriSchemeHttp}://localhost:{port}/HealthCheck",
                } },
                Address = "localhost",
                ID = $"{serviceOptions.Value.ServiceName}_localhost:{port}",
                Name = serviceOptions.Value.ServiceName,
                Port = port
            };

            consul.Agent.ServiceRegister(localhostregistration).GetAwaiter().GetResult();

            //send consul request after service stop
            //当服务停止后想consul发送的请求
            appLife.ApplicationStopping.Register(() =>
            {
                consul.Agent.ServiceDeregister(localhostregistration.ID).GetAwaiter().GetResult();
            });

            app.Map("/HealthCheck", s =>
            {
                s.Run(async context =>
                {
                    await context.Response.WriteAsync("ok");
                });
            });
            return app;
        }
    }
}