FROM mcr.microsoft.com/dotnet/core/aspnet:3.1-nanoserver-1809 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/core/sdk:3.1-nanoserver-1809 AS build
WORKDIR /src
COPY ["samples/HotelBookingService/src/HotelBooking.Api/HotelBooking.Api.csproj", "samples/HotelBookingService/src/HotelBooking.Api/"]
COPY ["samples/HotelBookingService/src/HotelBooking.Application/HotelBooking.Application.csproj", "samples/HotelBookingService/src/HotelBooking.Application/"]
COPY ["samples/HotelBookingService/src/HotelBooking.Domain/HotelBooking.Domain.csproj", "samples/HotelBookingService/src/HotelBooking.Domain/"]
COPY ["samples/HotelBookingService/src/HotelBooking.Infrastructure/HotelBooking.Infrastructure.csproj", "samples/HotelBookingService/src/HotelBooking.Infrastructure/"]
COPY ["src/Chatter.CQRS/src/Chatter.CQRS/Chatter.CQRS.csproj", "src/Chatter.CQRS/src/Chatter.CQRS/"]
COPY ["src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Chatter.MessageBrokers.csproj", "src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/"]
RUN dotnet restore "samples/HotelBookingService/src/HotelBooking.Api/HotelBooking.Api.csproj"
COPY . .
WORKDIR "/src/samples/HotelBookingService/src/HotelBooking.Api"
RUN dotnet build "HotelBooking.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "HotelBooking.Api.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "HotelBooking.Api.dll"]