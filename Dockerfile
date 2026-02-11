FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/OnThisDay/OnThisDay.csproj src/OnThisDay/
RUN dotnet restore src/OnThisDay/OnThisDay.csproj

COPY src/ src/
RUN dotnet publish src/OnThisDay/OnThisDay.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update && \
    apt-get install -y --no-install-recommends ffmpeg && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "OnThisDay.dll"]
