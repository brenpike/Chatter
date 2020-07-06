FROM mcr.microsoft.com/dotnet/core/aspnet:3.1-nanoserver-1809 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/core/sdk:3.1-nanoserver-1809 AS build
WORKDIR /src
COPY ["samples/TravelBookingService/src/TravelBooking.Api/TravelBooking.Api.csproj", "samples/TravelBookingService/src/TravelBooking.Api/"]
COPY ["samples/TravelBookingService/src/TravelBooking.Application/TravelBooking.Application.csproj", "samples/TravelBookingService/src/TravelBooking.Application/"]
COPY ["samples/TravelBookingService/src/TravelBooking.Domain/TravelBooking.Domain.csproj", "samples/TravelBookingService/src/TravelBooking.Domain/"]
COPY ["samples/TravelBookingService/src/TravelBooking.Infrastructure/TravelBooking.Infrastructure.csproj", "samples/TravelBookingService/src/TravelBooking.Infrastructure/"]
COPY ["src/Chatter.CQRS/src/Chatter.CQRS/Chatter.CQRS.csproj", "src/Chatter.CQRS/src/Chatter.CQRS/"]
COPY ["src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Chatter.MessageBrokers.csproj", "src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/"]
RUN dotnet restore "samples/TravelBookingService/src/TravelBooking.Api/TravelBooking.Api.csproj"
COPY . .
WORKDIR "/src/samples/TravelBookingService/src/TravelBooking.Api"
RUN dotnet build "TravelBooking.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "TravelBooking.Api.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "TravelBooking.Api.dll"]