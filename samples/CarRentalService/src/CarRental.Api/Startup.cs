using CarRental.Application.Behaviors;
using CarRental.Application.Commands;
using CarRental.Application.Events;
using CarRental.Application.Services;
using CarRental.Infrastructure.Repositories;
using CarRental.Infrastructure.Repositories.Contexts;
using CarRental.Infrastructure.Services;
using Chatter.SqlChangeFeed.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
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
            services.AddDbContext<CarRentalContext>(o => o.UseSqlServer(Configuration.GetValue<string>("ConnectionStrings:CarRentals"),
                b => b.MigrationsAssembly(typeof(CarRentalContext).Assembly.FullName).EnableRetryOnFailure(5)));

            services.AddChatterCqrs(Configuration, builder =>
            {
                builder.WithBehavior(typeof(LoggingBehavior<>))
                       .WithOutboxProcessingBehavior<CarRentalContext>()
                       .WithInboxBehavior<CarRentalContext>()
                       .WithRoutingSlipBehavior();
            },
            typeof(BookRentalCarCommand))
            .AddMessageBrokers(builder =>
            {
                builder.AddRecoveryOptions(r =>
                {
                    r.UseConstantDelayRecovery(2000).WithMaxRetryAttempts(6); //r.UseExponentialDelayRecovery(5);
                    r.WithCircuitBreaker(b => b.SetNumberOfFailuresBeforeOpen(3).SetOpenToHalfOpenWaitTime(10).SetNumberOfHalfOpenSuccessesBeforeClose(2));
                });
            })
            .AddAzureServiceBus(builder =>
            {
                builder.UseAadTokenProviderWithSecret(Configuration.GetValue<string>("Chatter:Infrastructure:AzureServiceBus:Auth:ClientId"),
                                                      Configuration.GetValue<string>("Chatter:Infrastructure:AzureServiceBus:Auth:ClientSecret"),
                                                      Configuration.GetValue<string>("Chatter:Infrastructure:AzureServiceBus:Auth:Authority"));
            })
            .AddSqlChangeFeed<OutboxChangedEvent>(Configuration.GetValue<string>("ConnectionStrings:CarRentals"), "CarRentals", "OutboxMessage", b => b.WithMaxReceiveAttempts(8));
            //.AddSqlChangeFeed<CarRentalAggregateChangedEvent>(Configuration.GetValue<string>("ConnectionStrings:CarRentals"), "CarRentals", "CarRental", b => b.WithMaxReceiveAttempts(8));
            //.AddSqlServiceBroker(builder =>
            //{
            //    builder.AddSqlServiceBrokerOptions(Configuration.GetValue<string>("ConnectionStrings:CarRentals"))
            //           .AddQueueReceiver<CarRentalAggregateChangedEvent>("Chatter_Queue_CarRentalAggregateChangedEvent");
            //});
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseChangeFeedSqlMigrations<OutboxChangedEvent>();
                //app.UseChangeFeedSqlMigrations<CarRentalAggregateChangedEvent>();
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
