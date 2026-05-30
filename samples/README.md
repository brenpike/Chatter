# Samples

The sample applications (CarRental, FlightBooking, HotelBooking, TravelBooking, SharedKernel) illustrate end-to-end usage of the Chatter libraries and are intended as throwaway references, not production templates.

## Known Limitations

**Container networking not validated against current images.** As part of the framework uplift, the sample projects were retargeted to `net8.0;net10.0` and their Dockerfiles updated to .NET 10 base images. The container runtime networking in `docker-compose.yml` has **not** been validated against these images. Specifically:

- ASP.NET Core 8.0+ images default `ASPNETCORE_HTTP_PORTS` to `8080`, but the Dockerfiles still `EXPOSE 80/443` and `docker-compose.yml` maps host ports to container `80`/`443`. Containerized samples may not be reachable without adjusting `ASPNETCORE_HTTP_PORTS` or the port mappings.
- The HTTPS (`443`) mappings have no certificate configured.

These samples are slated for replacement; container networking will be addressed in that future effort. Running samples directly via `dotnet run` (not containers) is unaffected.
