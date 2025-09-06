:: echo Build
:: dotnet build

echo Build for Linux
dotnet publish ./qbt_auto.csproj -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true -o ./bin/linux

echo Build for Windows
dotnet publish ./qbt_auto.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o ./bin/win


copy C:\Users\JGarza\GitHub\qbt_auto\bin\linux\qbt_auto \\192.168.1.250\TBP-Home\GitHub\temp\qbt_auto
copy C:\Users\JGarza\GitHub\qbt_auto\NLog.config \\192.168.1.250\TBP-Home\GitHub\temp\NLog.config
copy C:\Users\JGarza\GitHub\qbt_auto\config.json \\192.168.1.250\TBP-Home\GitHub\temp\config.json

del \\192.168.1.250\TBP-Home\GitHub\temp\log
