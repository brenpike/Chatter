using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using TravelBooking.Application.Commands;

namespace TravelBooking.Api
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
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Travel Booking Api", Version = "v1" });
            });

            services.AddChatterCqrs()
                .AddMessageBrokers(typeof(BookTravelCommand))
                .AddSagas(options =>
                {
                    options.AddSagaOptions(Configuration, "Chatter:Sagas:TravelBooking");
                    //options.AddAllSagaOptions(Configuration);
                })
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

            //This starts receving all message broker message receivers on startup
            app.StartChatterReceivers();

            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "Travel Booking Api V1");
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
