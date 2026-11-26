FROM ://microsoft.com AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app

FROM ://microsoft.com
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0
EXPOSE 10000
ENTRYPOINT ["dotnet", "UsdtDeskApp.dll"]
