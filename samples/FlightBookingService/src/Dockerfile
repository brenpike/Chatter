FROM mcr.microsoft.com/dotnet/core/aspnet:3.1-nanoserver-1809 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/core/sdk:3.1-nanoserver-1809 AS build
WORKDIR /src
COPY ["samples/FlightBookingService/src/FlightBooking.Api/FlightBooking.Api.csproj", "samples/FlightBookingService/src/FlightBooking.Api/"]
COPY ["samples/FlightBookingService/src/FlightBooking.Application/FlightBooking.Application.csproj", "samples/FlightBookingService/src/FlightBooking.Application/"]
COPY ["samples/FlightBookingService/src/FlightBooking.Domain/FlightBooking.Domain.csproj", "samples/FlightBookingService/src/FlightBooking.Domain/"]
COPY ["samples/FlightBookingService/src/FlightBooking.Infrastructure/FlightBooking.Infrastructure.csproj", "samples/FlightBookingService/src/FlightBooking.Infrastructure/"]
COPY ["src/Chatter.CQRS/src/Chatter.CQRS/Chatter.CQRS.csproj", "src/Chatter.CQRS/src/Chatter.CQRS/"]
COPY ["src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Chatter.MessageBrokers.csproj", "src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/"]
RUN dotnet restore "samples/FlightBookingService/src/FlightBooking.Api/FlightBooking.Api.csproj"
COPY . .
WORKDIR "/src/samples/FlightBookingService/src/FlightBooking.Api"
RUN dotnet build "FlightBooking.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "FlightBooking.Api.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "FlightBooking.Api.dll"]