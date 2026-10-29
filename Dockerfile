FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/Elementarr/Elementarr.csproj src/Elementarr/
COPY src/Elementarr.UpdateCore/Elementarr.UpdateCore.csproj src/Elementarr.UpdateCore/
RUN dotnet restore src/Elementarr/Elementarr.csproj
COPY src/Elementarr/ src/Elementarr/
COPY src/Elementarr.UpdateCore/ src/Elementarr.UpdateCore/
RUN dotnet publish src/Elementarr/Elementarr.csproj -c Release --no-restore -o /out \
    && mkdir /persist && chmod 700 /persist

FROM build AS tests
COPY tests/ tests/
RUN dotnet run --project tests/Elementarr.Tests -c Release

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build --chown=app:app /out/ ./
COPY --from=build --chown=app:app /persist/ /data/
ENV ASPNETCORE_HTTP_PORTS=8080 ELEMENTARR_DATA=/data
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Elementarr.dll"]
