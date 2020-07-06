FROM mcr.microsoft.com/dotnet/core/aspnet:3.1-nanoserver-1809 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/core/sdk:3.1-nanoserver-1809 AS build
WORKDIR /src
COPY ["samples/CarRentalService/src/CarRental.Api/CarRental.Api.csproj", "samples/CarRentalService/src/CarRental.Api/"]
COPY ["samples/CarRentalService/src/CarRental.Application/CarRental.Application.csproj", "samples/CarRentalService/src/CarRental.Application/"]
COPY ["samples/CarRentalService/src/CarRental.Domain/CarRental.Domain.csproj", "samples/CarRentalService/src/CarRental.Domain/"]
COPY ["samples/CarRentalService/src/CarRental.Infrastructure/CarRental.Infrastructure.csproj", "samples/CarRentalService/src/CarRental.Infrastructure/"]
COPY ["src/Chatter.CQRS/src/Chatter.CQRS/Chatter.CQRS.csproj", "src/Chatter.CQRS/src/Chatter.CQRS/"]
COPY ["src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Chatter.MessageBrokers.csproj", "src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/"]
RUN dotnet restore "samples/CarRentalService/src/CarRental.Api/CarRental.Api.csproj"
COPY . .
WORKDIR "/src/samples/CarRentalService/src/CarRental.Api"
RUN dotnet build "CarRental.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "CarRental.Api.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CarRental.Api.dll"]