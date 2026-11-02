using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NLog;
using NLog.Web;
using System;
using System.IO;
using WhatSearch.Core;
using WhatSearch.Middlewares;
using WhatSearch.Utility;

namespace WhatSearch
{
    public class Startup
    {
        static ILogger logger = LogManager.GetCurrentClassLogger();

        public Startup(IWebHostEnvironment env)
        {
            logger.Info("Startup");
        }

        public void ConfigureServices(IServiceCollection services)
        {
            //services.AddCors(cfg =>
            //{
            //    cfg.AddPolicy("AllowMyOrigin",
            //        builder => builder.WithOrigins("http://localhost:7777", "http://uni2.tw:7777"));
            //});

            //.net 5 mvc setting
            services.AddControllers()                                
                .AddJsonOptions(options => options.JsonSerializerOptions.PropertyNameCaseInsensitive = true);

            services.AddControllersWithViews()
                .AddJsonOptions(options => options.JsonSerializerOptions.PropertyNameCaseInsensitive = true);

            services.AddRazorPages()
                .AddJsonOptions(options => options.JsonSerializerOptions.PropertyNameCaseInsensitive = true);

            //services.AddMvc((options) => { options.SerializerOptions.WriteIndented = true; })
            //    .AddJsonOptions(options =>
            //    {
            //        options.SerializerSettings.Formatting = Formatting.Indented;
            //        options.SerializerSettings.ContractResolver
            //            = new CamelCasePropertyNamesContractResolver();
            //    });
            services.AddHttpContextAccessor();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IHostApplicationLifetime appLifetime)
        {
            var config = Ioc.GetConfig();
            config.ContentRootPath = env.ContentRootPath;
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler(new ExceptionHandlerOptions()
                {                     
                    ExceptionHandler = async context =>
                    {
                        var errorFeature = context.Features.Get<IExceptionHandlerFeature>();
                        var exception = errorFeature.Error;
                        NLog.LogManager.GetLogger("Unhandled").Error(exception);
                        dynamic problemDetails = new
                        {
                            Title = "An unexpected error occurred!",
                            Status = 500,
                            Detail = exception.ToString()
                        };
                        context.Response.StatusCode = 500;
                        context.Response.WriteAsync(exception.ToString());
                        //context.Response.Redirect("/error");
                    }
                });
            }

            app.UseStaticFiles(new StaticFileOptions
            {                 
                RequestPath = new PathString("/assets"),
                ServeUnknownFileTypes = false
            });

            app.UseMiddleware<UserTrackingMiddleware>();

            app.UseMiddleware<CustomSecurityHeadersMiddleware>();
            app.UseMiddleware<UserAuthenticationMiddleware>();
            app.UseMiddleware<PlaybackLogMiddleware>();
            app.UseMiddleware<PlaybackMonitorMiddleware>();
            //app.MapWhen(context => context.Request.Path.ToString().EndsWith(".md"),
            //    appBuilder =>
            //    {
            //        appBuilder.UseCustomHanlderMiddleware();
            //    });
            app.MapWhen(context => context.Request.Path.ToString() == "/about",
                appBuilder =>
                {
                    appBuilder.UseCustomHanlderMiddleware();
                });
            
            WebContext.Configure(app.ApplicationServices
                                  .GetRequiredService<IHttpContextAccessor>());

            app.UseRouting();
            app.UseEndpoints(configure =>
            {
                configure.MapControllers();
            });

            
        }

    }

}
