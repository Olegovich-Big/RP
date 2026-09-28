FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY EventsLogger/EventsLogger.csproj EventsLogger/
COPY Valuator.Shared/Valuator.Shared.csproj Valuator.Shared/
RUN dotnet restore EventsLogger/EventsLogger.csproj
COPY EventsLogger/ EventsLogger/
COPY Valuator.Shared/ Valuator.Shared/
RUN dotnet publish EventsLogger/EventsLogger.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "EventsLogger.dll"]
