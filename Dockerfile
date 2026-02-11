FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY src/OnThisDay/OnThisDay.csproj src/OnThisDay/
RUN dotnet restore src/OnThisDay/OnThisDay.csproj

COPY src/ src/
RUN dotnet publish src/OnThisDay/OnThisDay.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

RUN apk add --no-cache ffmpeg

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "OnThisDay.dll"]
