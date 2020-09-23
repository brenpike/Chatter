using CarRental.Application.Behaviors;
using CarRental.Application.Commands;
using CarRental.Application.Services;
using CarRental.Infrastructure.Repositories;
using CarRental.Infrastructure.Repositories.Contexts;
using CarRental.Infrastructure.Services;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.EntityFramework;
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
            services.AddDbContext<CarRentalContext>();
            services.AddControllers();

            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Car Rental Api", Version = "v1" });
            });

            services.AddScoped<IEventMapper, EventMapper>();
            services.AddScoped<IRepository<Domain.Aggregates.CarRental, Guid>, CarRentalRepository>();
            services.AddScoped<ITransactionalBrokeredMessageOutbox, TransactionalOutbox<CarRentalContext>>();

            services.AddChatterCqrs(typeof(BookRentalCarCommand))
                    .AddCommandPipeline(builder =>
                    {
                        builder.WithBehavior(typeof(LoggingBehavior<>))
                               .WithUnitOfWorkBehavior<CarRentalContext>(services)
                               .WithBehavior(typeof(AnotherLoggingBehavior<>));
                    })
                    .AddMessageBrokers((options) =>
                    {
                        options.AddReliabilityOptions(Configuration);
                    }, typeof(BookRentalCarCommand))
                    .AddAzureServiceBus(options =>
                    {
                        options.AddServiceBusOptions(Configuration, "Chatter:ServiceBus");
                    });
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
