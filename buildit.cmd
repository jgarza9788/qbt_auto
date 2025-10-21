:: echo Build
:: dotnet build

echo Build for Linux
dotnet publish ./qbt_auto.csproj -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true -o ./bin/linux

echo Build for Windows
dotnet publish ./qbt_auto.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o ./bin/win


copy .\bin\linux\qbt_auto \\192.168.1.250\TBP-Home\GitHub\qbt_auto\
copy .\NLog.config \\192.168.1.250\TBP-Home\GitHub\qbt_auto\NLog.config
copy .\config.json \\192.168.1.250\TBP-Home\GitHub\qbt_auto\config.json

del \\192.168.1.250\TBP-Home\GitHub\temp\log
