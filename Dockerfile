# ---- Stage 1: build -------------------------------------------------------
# The SDK image has the compiler. It is big and is only used to build; it is not shipped.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy only the project file first and download packages. Docker caches each step,
# so this slow step is skipped on later builds unless the .csproj changes.
COPY TvRemote.csproj .
RUN dotnet restore

# Now the source code, then compile into /app.
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

# ---- Stage 2: run ---------------------------------------------------------
# The ASP.NET runtime image: no compiler, much smaller. Only the compiled app is copied in.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Run as the image's built-in non-root user. The app listens on port 8080 (the image's default).
USER $APP_UID
ENTRYPOINT ["dotnet", "TvRemote.dll"]
