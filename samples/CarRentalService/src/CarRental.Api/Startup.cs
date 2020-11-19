using CarRental.Application.Behaviors;
using CarRental.Application.Commands;
using CarRental.Application.Services;
using CarRental.Infrastructure.Repositories;
using CarRental.Infrastructure.Repositories.Contexts;
using CarRental.Infrastructure.Services;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Reliability.Outbox;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using Samples.SharedKernel.Interfaces;
using System;

namespace CarRental.Api
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();

            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Car Rental Api", Version = "v1" });
            });

            services.AddScoped<IEventMapper, EventMapper>();
            services.AddScoped<IRepository<Domain.Aggregates.CarRental, Guid>, CarRentalRepository>();
            services.AddDbContext<CarRentalContext>();

            services.AddChatterCqrs(Configuration, typeof(BookRentalCarCommand))
                    .AddCommandPipeline(builder =>
                    {
                        builder.WithBehavior(typeof(LoggingBehavior<>))
                               //.WithUnitOfWorkBehavior<CarRentalContext>()
                               //.WithInboxBehavior<CarRentalContext>()
                               .WithOutboxProcessingBehavior<CarRentalContext>()
                               .WithRoutingSlipBehavior();
                    })
                    .AddMessageBrokers(builder =>
                    {
                        builder.UseExponentialDelayRecovery()
                               .UseCombGuidMessageIdGenerator();
                    })
                    .AddAzureServiceBus();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "Car Rental Api V1");
            });

            app.UseHttpsRedirection();

            app.UseRouting();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }
    }
}
