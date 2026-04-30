dotnet publish ./agent/RustOpsAgent/RustOpsAgent.csproj -c Release -r linux-x64 -o ../agent/RustOpsAgent/
dotnet publish ./remote-agent/RustOpsRemoteAgent/RustOpsRemoteAgent.csproj -c Release -r linux-x64 -o ../remote-agent/RustOpsRemoteAgent/
dotnet publish ./SteamBot/OpsSteamBot/OpsSteamBot.csproj -c Release -r linux-x64 -o ../SteamBot/OpsSteamBot/
dotnet publish ./api/rustmgrapi.csproj -c Release -r linux-x64 -o ../api/
